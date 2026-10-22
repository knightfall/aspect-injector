using AspectInjector.Core;
using AspectInjector.Core.Contracts;
using AspectInjector.Core.Models;
using AspectInjector.Core.Services;
using AspectInjector.Rules;
using FluentIL;
using FluentIL.Common;
using FluentIL.Logging;
using FluentIL.Resolvers;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace AspectInjector.Tests.Build
{
    public class AssemblyLifetimeTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AspectInjectorTests", Guid.NewGuid().ToString("N"));

        public AssemblyLifetimeTests()
        {
            Directory.CreateDirectory(_directory);
        }

        [Theory]
        [InlineData(PatchResult.Unmodified, true)]
        [InlineData(PatchResult.Modified, true)]
        [InlineData(PatchResult.LoggedError, true)]
        [InlineData(PatchResult.PatchThrows, true)]
        [InlineData(PatchResult.WriteThrows, true)]
        [InlineData(PatchResult.Unmodified, false)]
        [InlineData(PatchResult.Modified, false)]
        public void ProcessingReleasesTargetSymbolsAndDependencies(PatchResult result, bool withSymbols)
        {
            var target = CreateAssembly("Target", withSymbols);
            var dependency = CreateAssembly("Dependency");
            var logger = new TestLogger();
            var patcher = new TestPatcher(logger, result);

            var error = Record.Exception(() => patcher.Process(target, new[] { dependency }, false, false));

            if (result == PatchResult.PatchThrows || result == PatchResult.WriteThrows)
                Assert.NotNull(error);
            else
                Assert.Null(error);

            Assert.True(patcher.PatchCalled);
            Assert.Equal(result == PatchResult.LoggedError, logger.IsErrorThrown);
            AssertUnlocked(target);
            if (withSymbols)
                AssertUnlocked(Path.ChangeExtension(target, ".pdb"));
            AssertUnlocked(dependency);

            if (result == PatchResult.Modified)
            {
                using var assembly = AssemblyDefinition.ReadAssembly(target);
                Assert.Contains(assembly.MainModule.Types, type => type.Name == "AddedByPatcher");
            }
        }

        [Fact]
        public void BorrowedResolverRemainsUsableAndDoesNotKeepTheDisposedTarget()
        {
            var target = CreateAssembly("Target");
            var dependency = CreateAssembly("Dependency");
            using var resolver = new TrackingResolver();
            resolver.AddSearchDirectory(_directory);
            resolver.AddReference(dependency);
            var patcher = new TestPatcher(new TestLogger(), PatchResult.Unmodified);

            patcher.Process(target, resolver, false, false);

            Assert.Equal(0, resolver.DisposeCount);
            AssertUnlocked(target);
            var reopened = resolver.Resolve(new AssemblyNameReference("Target", new Version(1, 0, 0, 0)));
            Assert.NotSame(patcher.Target, reopened);

            resolver.Dispose();
            AssertUnlocked(target);
            AssertUnlocked(dependency);
        }

        [Fact]
        public void ResolverDisposalClosesCachedAssembliesAndCanBeRepeated()
        {
            var dependency = CreateAssembly("Dependency");
            var resolver = new KnownReferencesAssemblyResolver();
            resolver.AddReference(dependency);
            resolver.Resolve(new AssemblyNameReference("Dependency", new Version(1, 0, 0, 0)), new ReaderParameters { ReadSymbols = true });

            resolver.Dispose();
            resolver.Dispose();

            AssertUnlocked(dependency);
            AssertUnlocked(Path.ChangeExtension(dependency, ".pdb"));
        }

        [Fact]
        public void OwnedResolverIsDisposedBeforeEarlyReturn()
        {
            var target = CreateAssembly("Target");
            var resolver = new TrackingResolver();
            var logger = new TestLogger { IsErrorThrown = true };
            var patcher = new TestPatcher(logger, PatchResult.Unmodified, resolver);

            patcher.Process(target, Array.Empty<string>(), false, false);

            Assert.Equal(1, resolver.DisposeCount);
            Assert.False(patcher.PatchCalled);
        }

        [Fact]
        public void AspectCacheIsSharedWithinACompilationButNotAcrossCompilations()
        {
            using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("CacheTest", new Version(1, 0)), "CacheTest", ModuleKind.Dll);
            var type = new TypeDefinition("Tests", "Target", TypeAttributes.Public);
            assembly.MainModule.Types.Add(type);
            var firstEffects = new CountingEffectReader();
            var secondEffects = new CountingEffectReader();
            var first = new AspectReader(new[] { firstEffects }, new TestLogger());
            var second = new AspectReader(new[] { secondEffects }, new TestLogger());

            Assert.Null(first.Read(type));
            Assert.Null(first.Read(type));
            Assert.Null(second.Read(type));

            Assert.Equal(1, firstEffects.ReadCount);
            Assert.Equal(1, secondEffects.ReadCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProcessorRestoresModificationCallbackEvenWhenWeavingFails(bool fail)
        {
            using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("CallbackTest", new Version(1, 0)), "CallbackTest", ModuleKind.Dll);
            var logger = new TestLogger();
            var processor = new TestProcessor(new AspectReader(Array.Empty<IEffectReader>(), logger), fail, logger);
            var previous = CutEvents.OnModify;
            Action<MethodBody> callback = _ => { };
            CutEvents.OnModify = callback;
            try
            {
                var error = Record.Exception(() => processor.Patch(assembly));
                if (fail)
                    Assert.IsType<InvalidOperationException>(error);
                else
                    Assert.Null(error);
                Assert.Same(callback, CutEvents.OnModify);
            }
            finally
            {
                CutEvents.OnModify = previous;
            }
        }

        [Fact]
        public void ConcurrentBuildsHaveIndependentModificationCallbacks()
        {
            var previous = CutEvents.OnModify;
            Action<MethodBody> callback = _ => { };
            using var ready = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                CutEvents.OnModify = _ => { };
                ready.Set();
                done.Wait(TimeSpan.FromSeconds(10));
                CutEvents.OnModify = null;
            });
            CutEvents.OnModify = callback;
            thread.Start();
            try
            {
                Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
                Assert.Same(callback, CutEvents.OnModify);
            }
            finally
            {
                done.Set();
                thread.Join();
                CutEvents.OnModify = previous;
            }
        }

        private string CreateAssembly(string name, bool withSymbols = true)
        {
            var path = Path.Combine(_directory, name + ".dll");
            using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
            var type = new TypeDefinition("Tests", "Target", TypeAttributes.Public, assembly.MainModule.TypeSystem.Object);
            var method = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            method.DebugInformation.SequencePoints.Add(new SequencePoint(method.Body.Instructions[0], new Document(name + ".cs"))
            {
                StartLine = 1, EndLine = 1, StartColumn = 1, EndColumn = 2
            });
            type.Methods.Add(method);
            assembly.MainModule.Types.Add(type);
            assembly.Write(path, new WriterParameters { WriteSymbols = withSymbols, SymbolWriterProvider = new PortablePdbWriterProvider() });
            return path;
        }

        private static void AssertUnlocked(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(stream.CanWrite);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, true);
        }

        public enum PatchResult { Unmodified, Modified, LoggedError, PatchThrows, WriteThrows }

        private sealed class TestPatcher : PatcherBase
        {
            private readonly PatchResult _result;
            private readonly IAssemblyResolver _resolver;
            public bool PatchCalled { get; private set; }
            public AssemblyDefinition Target { get; private set; }

            public TestPatcher(ILogger logger, PatchResult result, IAssemblyResolver resolver = null) : base(logger)
            {
                _result = result;
                _resolver = resolver;
            }

            protected override Rule GenericInfoRule => GeneralRules.Info;
            protected override Rule GenericErrorRule => GeneralRules.CompilationMustSecceedIfNoOtherErrors;
            protected override IAssemblyResolver GetResolver(string path, IReadOnlyList<string> references)
                => _resolver ?? base.GetResolver(path, references);

            protected override bool PatchAssembly(AssemblyDefinition assembly, bool optimize, bool verbose)
            {
                PatchCalled = true;
                Target = assembly;
                Assert.Same(assembly, assembly.MainModule.AssemblyResolver.Resolve(assembly.Name));
                assembly.MainModule.AssemblyResolver.Resolve(new AssemblyNameReference("Dependency", new Version(1, 0, 0, 0)));
                if (_result == PatchResult.PatchThrows)
                    throw new InvalidOperationException("Patch failed");
                if (_result == PatchResult.LoggedError)
                    _log.Log(GeneralRules.CompilationMustSecceedIfNoOtherErrors, null, "Patch failed");
                if (_result == PatchResult.Modified)
                    assembly.MainModule.Types.Add(new TypeDefinition("Tests", "AddedByPatcher", TypeAttributes.Public, assembly.MainModule.TypeSystem.Object));
                if (_result == PatchResult.WriteThrows)
                {
                    var instruction = Instruction.Create(OpCodes.Nop);
                    instruction.OpCode = OpCodes.Call;
                    assembly.MainModule.Types.Single(t => t.Name == "Target").Methods[0].Body.Instructions.Insert(0, instruction);
                }
                return _result == PatchResult.Modified || _result == PatchResult.WriteThrows;
            }
        }

        private sealed class TestProcessor : Processor
        {
            public TestProcessor(IAspectReader reader, bool fail, ILogger logger)
                : base(reader, new TestInjectionReader(fail), null, Weavers(fail), logger) { }

            public bool Patch(AssemblyDefinition assembly) => PatchAssembly(assembly, false, false);

            private static IEnumerable<IEffectWeaver> Weavers(bool fail)
            {
                if (fail)
                    throw new InvalidOperationException("Weaving failed");
                yield break;
            }
        }

        private sealed class TestInjectionReader : IInjectionReader
        {
            private readonly bool _fail;
            public TestInjectionReader(bool fail) => _fail = fail;
            public IReadOnlyCollection<InjectionDefinition> ReadAll(AssemblyDefinition assembly)
                => _fail ? new[] { new InjectionDefinition() } : Array.Empty<InjectionDefinition>();
        }

        private sealed class TrackingResolver : KnownReferencesAssemblyResolver
        {
            public int DisposeCount { get; private set; }
            protected override void Dispose(bool disposing)
            {
                DisposeCount++;
                base.Dispose(disposing);
            }
        }

        private sealed class CountingEffectReader : IEffectReader
        {
            public int ReadCount { get; private set; }
            public IReadOnlyCollection<Effect> Read(ICustomAttributeProvider host)
            {
                ReadCount++;
                return Array.Empty<Effect>();
            }
        }

        private sealed class TestLogger : ILogger
        {
            public bool IsErrorThrown { get; set; }
            public void Log(Rule rule, SequencePoint point, params string[] messages)
            {
                if (rule.Severity == RuleSeverity.Error)
                    IsErrorThrown = true;
            }
        }
    }
}
