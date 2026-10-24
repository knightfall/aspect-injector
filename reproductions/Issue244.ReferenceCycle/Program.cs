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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace AspectInjector.ReferenceCycleProof;

internal static class Program
{
    private static int _sequence;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Main(string[] args)
    {
        var revision = args[0];
        var work = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var external = args.Length > 3 && args[3] == "external";
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(output);
        Emit("environment", new
        {
            revision, os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription,
            cecil_version = typeof(AssemblyDefinition).Assembly.GetName().Version.ToString(),
            cecil_sha256 = Hash(typeof(AssemblyDefinition).Assembly.Location),
            fluentil_sha256 = Hash(typeof(PatcherBase).Assembly.Location)
        });
        var results = new List<Observation>();
        results.Add(RunCase(work, "control-borrowed", cycle: false, owned: false, external));
        results.Add(RunCase(work, "cycle-borrowed", cycle: true, owned: false, external));
        results.Add(RunCase(work, "cycle-owned", cycle: true, owned: true, external));
        File.WriteAllText(Path.Combine(output, "observations.json"), JsonSerializer.Serialize(new
        {
            schema = 1, revision, os = RuntimeInformation.OSDescription,
            is_windows = OperatingSystem.IsWindows(), runtime = RuntimeInformation.FrameworkDescription,
            cecil_sha256 = Hash(typeof(AssemblyDefinition).Assembly.Location),
            fluentil_sha256 = Hash(typeof(PatcherBase).Assembly.Location), cases = results
        }, JsonOptions));
    }

    private static Observation RunCase(string work, string name, bool cycle, bool owned, bool external)
    {
        var directory = Path.Combine(work, name);
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, "Target.dll");
        var dependencyPath = Path.Combine(directory, "Dependency.dll");
        CreateFixture(targetPath, dependencyPath, cycle);
        var result = new Observation
        {
            name = name, cycle = cycle, owned_resolver = owned,
            target_input_sha256 = Hash(targetPath), dependency_input_sha256 = Hash(dependencyPath),
            clr_graph_valid = ValidateWithClr(directory, cycle),
            before = ExclusiveOpen(targetPath), target_fds_before = TargetFileDescriptors(targetPath)
        };
        Emit("case.begin", result);
        IRecordingResolver resolver = external ? new ExternalCachingResolver(name) : new RecordingResolver(name);
        resolver.AddSearchDirectory(directory);
        resolver.AddReference(dependencyPath);
        var patcher = new RecordingPatcher(name, resolver, result);
        try
        {
            if (owned)
                patcher.Process(targetPath, new[] { dependencyPath }, false, false);
            else
                patcher.Process(targetPath, resolver, false, false);
            result.process_completed = true;
        }
        catch (Exception exception)
        {
            result.process_exception = Describe(exception);
            Emit("process.exception", new { name, error = result.process_exception });
        }
        result.after_process = ExclusiveOpen(targetPath);
        result.target_fds_after_process = TargetFileDescriptors(targetPath);
        result.resolver_disposals_after_process = resolver.DisposeCount;
        result.target_lookup_attempts = resolver.Lookups.Where(lookup => lookup.assembly_name == "Target").ToList();
        Emit("case.after_process", new
        {
            name, result.process_completed, result.back_reference_is_operation_target,
            result.after_process, result.target_fds_after_process,
            result.resolver_disposals_after_process, result.back_reference_was_loaded_by_underlying_cache,
            result.back_reference_cache_hit_without_io
        });
        resolver.Dispose();
        result.after_resolver_dispose = ExclusiveOpen(targetPath);
        result.target_fds_after_resolver_dispose = TargetFileDescriptors(targetPath);
        Emit("case.after_resolver_dispose", new
        {
            name, result.after_resolver_dispose, result.target_fds_after_resolver_dispose
        });
        // Baseline dependency cleanup is deliberately performed only after measurements.
        foreach (var assembly in resolver.Loaded)
            assembly.Dispose();
        return result;
    }

    private static void CreateFixture(string targetPath, string dependencyPath, bool cycle)
    {
        using var target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Target", new Version(1, 0, 0, 0)), "Target", ModuleKind.Dll);
        using var dependency = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Dependency", new Version(1, 0, 0, 0)), "Dependency", ModuleKind.Dll);
        target.MainModule.Mvid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        dependency.MainModule.Mvid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var root = new TypeDefinition("Test", "Root", Mono.Cecil.TypeAttributes.Public, target.MainModule.TypeSystem.Object);
        target.MainModule.Types.Add(root);
        var middleBase = cycle ? dependency.MainModule.ImportReference(root) : dependency.MainModule.TypeSystem.Object;
        var middle = new TypeDefinition("Test", "Middle", Mono.Cecil.TypeAttributes.Public, middleBase);
        dependency.MainModule.Types.Add(middle);
        target.MainModule.Types.Add(new TypeDefinition("Test", "Leaf", Mono.Cecil.TypeAttributes.Public, target.MainModule.ImportReference(middle)));
        target.Write(targetPath, new WriterParameters { Timestamp = 0 });
        dependency.Write(dependencyPath, new WriterParameters { Timestamp = 0 });
    }

    private static bool ValidateWithClr(string directory, bool cycle)
    {
        var context = new FixtureLoadContext(directory);
        try
        {
            var target = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(Path.Combine(directory, "Target.dll"))));
            var leaf = target.GetType("Test.Leaf", throwOnError: true);
            if (leaf.BaseType.FullName != "Test.Middle") return false;
            var root = leaf.BaseType.BaseType;
            return cycle
                ? root.FullName == "Test.Root" && root.Assembly == target && root.BaseType == typeof(object)
                : root == typeof(object);
        }
        finally { context.Unload(); }
    }

    private sealed class FixtureLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly Load(AssemblyName name)
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            // Load from bytes, so CLR validation cannot retain handles on the probe inputs.
            return File.Exists(path) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(path))) : null;
        }
    }

    private static OpenResult ExclusiveOpen(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return new OpenResult(true, null);
        }
        catch (Exception exception) { return new OpenResult(false, Describe(exception)); }
    }

    private static int? TargetFileDescriptors(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var count = 0;
        foreach (var descriptor in Directory.EnumerateFiles("/proc/self/fd"))
        {
            try { if (new FileInfo(descriptor).LinkTarget == path) count++; }
            catch (IOException) { }
        }
        return count;
    }

    private static Error Describe(Exception exception)
        => new(exception.GetType().FullName, exception.Message, exception.HResult,
            $"0x{exception.HResult:X8}", exception.HResult & 0xffff, exception.StackTrace);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Emit(string name, object data)
        => Console.WriteLine(JsonSerializer.Serialize(new { seq = ++_sequence, @event = name, data }));

    private sealed class RecordingPatcher(string name, IRecordingResolver resolver, Observation observation) : PatcherBase(new QuietLogger())
    {
        protected override Rule GenericInfoRule => null;
        protected override Rule GenericErrorRule => null;
        protected override IAssemblyResolver GetResolver(string assemblyFile, IReadOnlyList<string> references) => resolver;

        protected override bool PatchAssembly(AssemblyDefinition assembly, bool optimize, bool verbose)
        {
            observation.operation_target_id = RuntimeHelpers.GetHashCode(assembly);
            observation.operation_resolver = assembly.MainModule.AssemblyResolver.GetType().FullName;
            observation.direct_self_is_operation_target = ReferenceEquals(assembly.MainModule.AssemblyResolver.Resolve(assembly.Name), assembly);
            Emit("operation.target", new
            {
                name, observation.operation_target_id, observation.operation_resolver,
                observation.direct_self_is_operation_target
            });
            var dependency = assembly.MainModule.AssemblyResolver.Resolve(new AssemblyNameReference("Dependency", new Version(1, 0, 0, 0)));
            observation.dependency_resolver_is_underlying = ReferenceEquals(dependency.MainModule.AssemblyResolver, resolver);
            Emit("dependency.resolver", new { name, observation.dependency_resolver_is_underlying });
            if (observation.cycle)
            {
                var backReference = dependency.MainModule.GetType("Test.Middle").BaseType.Resolve().Module.Assembly;
                observation.BackReference = backReference;
                observation.back_reference_is_operation_target = ReferenceEquals(backReference, assembly);
                observation.back_reference_id = RuntimeHelpers.GetHashCode(backReference);
                observation.back_reference_path = backReference.MainModule.FileName;
                observation.back_reference_was_loaded_by_underlying_cache = resolver.Loaded.Any(value => ReferenceEquals(value, backReference));
                var lookupsBefore = resolver.Lookups.Count;
                var cached = resolver.Resolve(backReference.Name);
                observation.back_reference_cache_probe_added_lookups = resolver.Lookups.Count - lookupsBefore;
                observation.back_reference_cache_hit_without_io = ReferenceEquals(cached, backReference) &&
                    observation.back_reference_cache_probe_added_lookups == 0;
                Emit("dependency.back_reference", new
                {
                    name, observation.back_reference_id, observation.back_reference_path,
                    observation.back_reference_is_operation_target, observation.back_reference_cache_hit_without_io,
                    observation.back_reference_cache_probe_added_lookups
                });
            }
            // Force WriteAssembly in both revisions: the baseline closes targets only here.
            return true;
        }
    }

    private interface IRecordingResolver : IAssemblyResolver
    {
        List<AssemblyDefinition> Loaded { get; }
        List<Lookup> Lookups { get; }
        int DisposeCount { get; }
        void AddSearchDirectory(string directory);
        void AddReference(string path);
    }

    private sealed class RecordingResolver(string name) : KnownReferencesAssemblyResolver, IRecordingResolver
    {
        public List<AssemblyDefinition> Loaded { get; } = new();
        public List<Lookup> Lookups { get; } = new();
        public int DisposeCount { get; private set; }

        protected override AssemblyDefinition LookupAssembly(AssemblyNameReference assemblyName, ReaderParameters parameters)
        {
            var lookup = new Lookup { assembly_name = assemblyName.Name, read_write = parameters.ReadWrite };
            Lookups.Add(lookup);
            Emit("underlying.lookup.begin", new { name, lookup.assembly_name, lookup.read_write });
            try
            {
                var assembly = base.LookupAssembly(assemblyName, parameters);
                Loaded.Add(assembly);
                lookup.returned_id = RuntimeHelpers.GetHashCode(assembly);
                Emit("underlying.lookup.end", new { name, lookup.assembly_name, lookup.returned_id });
                return assembly;
            }
            catch (Exception exception)
            {
                lookup.error = Describe(exception);
                Emit("underlying.lookup.exception", new { name, lookup.assembly_name, lookup.error });
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            Emit("underlying.dispose", new { name, disposing, count = DisposeCount });
            base.Dispose(disposing);
        }
    }

    // Models an opaque caller cache that worked on the baseline. It intentionally
    // offers only Cecil's public IAssemblyResolver contract, without target registration.
    private sealed class ExternalCachingResolver(string name) : DefaultAssemblyResolver, IRecordingResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> _cache = new();
        public List<AssemblyDefinition> Loaded { get; } = new();
        public List<Lookup> Lookups { get; } = new();
        public int DisposeCount { get; private set; }
        public void AddReference(string path) { } // Both fixtures are in the configured search directory.
        public override AssemblyDefinition Resolve(AssemblyNameReference assemblyName)
            => Resolve(assemblyName, new ReaderParameters());

        public override AssemblyDefinition Resolve(AssemblyNameReference assemblyName, ReaderParameters parameters)
        {
            if (_cache.TryGetValue(assemblyName.FullName, out var cached)) return cached;
            var lookup = new Lookup { assembly_name = assemblyName.Name, read_write = parameters.ReadWrite };
            Lookups.Add(lookup);
            Emit("underlying.lookup.begin", new { name, lookup.assembly_name, lookup.read_write });
            try
            {
                var assembly = base.Resolve(assemblyName, parameters);
                _cache[assemblyName.FullName] = assembly;
                Loaded.Add(assembly);
                lookup.returned_id = RuntimeHelpers.GetHashCode(assembly);
                Emit("underlying.lookup.end", new { name, lookup.assembly_name, lookup.returned_id });
                return assembly;
            }
            catch (Exception exception)
            {
                lookup.error = Describe(exception);
                Emit("underlying.lookup.exception", new { name, lookup.assembly_name, lookup.error });
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            Emit("underlying.dispose", new { name, disposing, count = DisposeCount });
            if (disposing)
            {
                foreach (var assembly in _cache.Values) assembly.Dispose();
                _cache.Clear();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class QuietLogger : ILogger
    {
        public bool IsErrorThrown => false;
        public void Log(Rule rule, SequencePoint point, params string[] messages) { }
    }

    private sealed record Error(string type, string message, int hresult, string hresult_hex, int win32_code, string stack_trace);
    private sealed record OpenResult(bool succeeded, Error error);
    private sealed class Lookup
    {
        public string assembly_name { get; set; }
        public bool read_write { get; set; }
        public int? returned_id { get; set; }
        public Error error { get; set; }
    }
    private sealed class Observation
    {
        public string name { get; set; }
        public bool cycle { get; set; }
        public bool owned_resolver { get; set; }
        public string target_input_sha256 { get; set; }
        public string dependency_input_sha256 { get; set; }
        public bool clr_graph_valid { get; set; }
        public bool process_completed { get; set; }
        public Error process_exception { get; set; }
        public int operation_target_id { get; set; }
        public string operation_resolver { get; set; }
        public bool direct_self_is_operation_target { get; set; }
        public bool dependency_resolver_is_underlying { get; set; }
        public bool? back_reference_is_operation_target { get; set; }
        public int? back_reference_id { get; set; }
        public string back_reference_path { get; set; }
        public bool back_reference_was_loaded_by_underlying_cache { get; set; }
        public bool back_reference_cache_hit_without_io { get; set; }
        public int? back_reference_cache_probe_added_lookups { get; set; }
        public int resolver_disposals_after_process { get; set; }
        public OpenResult before { get; set; }
        public OpenResult after_process { get; set; }
        public OpenResult after_resolver_dispose { get; set; }
        public int? target_fds_before { get; set; }
        public int? target_fds_after_process { get; set; }
        public int? target_fds_after_resolver_dispose { get; set; }
        public List<Lookup> target_lookup_attempts { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public AssemblyDefinition BackReference { get; set; }
    }
}
