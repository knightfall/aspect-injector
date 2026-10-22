using AspectInjector.Core;
using AspectInjector.Core.Contracts;
using AspectInjector.Core.Models;
using AspectInjector.Core.Services;
using FluentIL;
using FluentIL.Common;
using FluentIL.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace AspectInjector.Tests.Build
{
    public class CompilationStateTests
    {
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
