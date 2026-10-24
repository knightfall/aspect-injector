#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Diagnostics;
using System.Formats.Tar;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Run with: dotnet run --file reproductions/Issue244.ReferenceCycle/reproduce.cs
return await Reproduction.Run(args);

internal static class Reproduction
{
    private const string Baseline = "fb24e3ce40b99e15d2af3cd40e0f0821ddafa197";
    private const string Candidate = "159da826cc1f6f12ae2da3c5573d1de38b4769a8";
    private static readonly string Source = SourcePath();
    private static readonly string Here = Path.GetDirectoryName(Source)!;
    private static readonly string Repository = Path.GetFullPath(Path.Combine(Here, "../.."));
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(false);

    public static async Task<int> Run(string[] arguments)
    {
        var options = new Dictionary<string, string>
        {
            ["--baseline"] = Baseline,
            ["--candidate"] = Candidate,
            ["--fixed"] = "",
            ["--resolver"] = "known",
            ["--dotnet"] = "dotnet",
            ["--output"] = Path.Combine(Repository, "artifacts/reference-cycle-proof")
        };
        if (arguments.Contains("--help"))
        {
            Console.WriteLine("Compare immutable revisions and check the reference-cycle counterexample.\n" +
                "Options: --baseline COMMIT --candidate COMMIT [--fixed COMMIT] [--resolver known|external] --dotnet PATH --output DIRECTORY");
            return 0;
        }

        try
        {
            for (var index = 0; index < arguments.Length; index += 2)
            {
                if (!options.ContainsKey(arguments[index]) || index + 1 == arguments.Length)
                    throw new ArgumentException($"Unknown option or missing value: {arguments[index]}");
                options[arguments[index]] = arguments[index + 1];
            }
            if (options["--resolver"] is not ("known" or "external"))
                throw new ArgumentException("--resolver must be known or external.");
            return await Collect(options);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> Collect(Dictionary<string, string> options)
    {
        var output = Path.GetFullPath(options["--output"]);
        Directory.CreateDirectory(output);
        var revisions = new Dictionary<string, object>();
        var manifest = new
        {
            schema = 1,
            scope = "Astra P2: transitive dependency back-references bypass target ownership",
            resolver_profile = options["--resolver"],
            recorded_at_utc = DateTimeOffset.UtcNow.ToString("O"),
            platform = RuntimeInformation.OSDescription,
            runner_runtime = RuntimeInformation.FrameworkDescription,
            dotnet_info = await Command(options["--dotnet"], ["--info"]),
            harness_source_sha256 = Hash(Path.Combine(Here, "Program.cs")),
            harness_project_sha256 = Hash(Path.Combine(Here, "ReferenceCycleProof.csproj")),
            runner_sha256 = Hash(Source),
            revisions
        };
        var observations = new Dictionary<string, JsonNode>();
        var work = Directory.CreateTempSubdirectory("aspect-injector-cycle-proof-").FullName;
        try
        {
            var labels = options["--fixed"].Length == 0 ? new[] { "baseline", "candidate" } : new[] { "baseline", "candidate", "fixed" };
            foreach (var label in labels)
            {
                var source = Path.Combine(work, label + "-source");
                var commit = await Command("git", ["rev-parse", "--verify", options["--" + label] + "^{commit}"]);
                var archive = source + ".tar";
                await Command("git", ["archive", "--output", archive, commit]);
                Directory.CreateDirectory(source);
                TarFile.ExtractToDirectory(archive, source, overwriteFiles: false);
                var sourceHashes = new Dictionary<string, string?>();
                foreach (var path in new[]
                {
                    "src/FluentIL/PatcherBase.cs",
                    "src/FluentIL/Resolvers/CachedAssemblyResolver.cs",
                    "src/FluentIL/Resolvers/KnownReferencesAssemblyResolver.cs",
                    "src/FluentIL/Resolvers/TargetAssemblyResolver.cs"
                })
                    sourceHashes[path] = File.Exists(Path.Combine(source, path)) ? Hash(Path.Combine(source, path)) : null;

                var build = Path.Combine(work, label + "-build");
                var destination = Path.Combine(output, label);
                Directory.CreateDirectory(destination);
                string[] buildArguments =
                [
                    "build", Path.Combine(Here, "ReferenceCycleProof.csproj"), "-c", "Release",
                    "--artifacts-path", build, "--disable-build-servers", $"-p:ReproRepository={source}", "-v:minimal"
                ];
                Console.WriteLine($"Building and probing {label}: {commit}");
                await Command(options["--dotnet"], buildArguments, Path.Combine(destination, "build.log"));
                var binaries = Directory.GetFiles(Path.Combine(build, "bin"), "AspectInjector.ReferenceCycleProof.dll", SearchOption.AllDirectories);
                if (binaries.Length != 1)
                    throw new InvalidOperationException($"Expected one probe binary, found {binaries.Length}.");
                await Command(options["--dotnet"],
                    [binaries[0], commit, Path.Combine(work, label + "-cases"), destination, options["--resolver"]],
                    Path.Combine(destination, "events.jsonl"));
                observations[label] = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "observations.json")))!;
                revisions[label] = new
                {
                    commit, source_sha256 = sourceHashes, probe_binary_sha256 = Hash(binaries[0]),
                    build_command = new[] { options["--dotnet"] }.Concat(buildArguments).ToArray()
                };
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        var checks = Evaluate(observations);
        var reproduced = checks.Where(check => check.id.StartsWith('P')).All(check => check.passed);
        var fixVerified = observations.ContainsKey("fixed") ? checks.Where(check => check.id.StartsWith('F')).All(check => check.passed) : (bool?)null;
        WriteJson(output, "manifest.json", manifest);
        WriteJson(output, "proof.json", new { schema = fixVerified.HasValue ? 2 : 1, regression_reproduced = reproduced, fix_verified = fixVerified, checks });
        var baseline = observations["baseline"];
        var candidate = observations["candidate"];
        var report = new List<string>
        {
            "# Reference-cycle reproduction proof", "",
            $"Baseline: `{baseline["revision"]}`. Candidate: `{candidate["revision"]}`.",
            $"Platform: {candidate["os"]}. Runtime: {candidate["runtime"]}.", "",
            "Let T be the operation-owned target, B the target reached through Dependency.Middle.BaseType,",
            "and X(p) mean that opening file p with FileShare.None succeeds. Object equality is checked",
            "with ReferenceEquals; displayed object IDs are diagnostic only.", "",
            "| Revision | Case | B = T | Process completed | X(target) after Process | X(target) after resolver disposal |",
            "| --- | --- | --- | --- | --- | --- |"
        };
        if (observations.TryGetValue("fixed", out var fixedRevision))
            report.Insert(4, $"Fixed revision: `{fixedRevision["revision"]}`.");
        foreach (var (label, revision) in observations)
            foreach (var row in Cases(revision))
                report.Add($"| {label} | {row["name"]} | {Display(row["back_reference_is_operation_target"])} | " +
                    $"{Display(row["process_completed"])} | {Display(row["after_process"]!["succeeded"])} | " +
                    $"{Display(row["after_resolver_dispose"]!["succeeded"])} |");
        report.AddRange(["", "## Checked propositions", ""]);
        report.AddRange(checks.Select(check => $"- {(check.passed ? "PASS" : "FAIL")} **{check.id}**: {check.proposition}."));
        report.AddRange([
            "", "## Conclusion", "",
            reproduced ? "The reference-cycle regression is reproduced." : "The expected counterexample was not fully reproduced; inspect failed propositions.",
            "The control and baseline establish that this fixture is valid and initially unlocked. The candidate's",
            "lookup trace establishes the bypass through the dependency resolver.",
            Bool(candidate["is_windows"])
                ? "The recorded Windows sharing violation identifies the conflicting open during processing."
                : "The cache-disposal intervention identifies the retained handle; Linux FD observations independently corroborate it.", "",
            "This is an executable counterexample for the recorded inputs, revisions, runtime, and platform.",
            "Only propositions actually checked on this platform are evidence from this run. It does not claim",
            "that every application has an assembly-reference cycle or explain unrelated apphost.exe failures.", "",
            "Raw traces: baseline/events.jsonl and candidate/events.jsonl. Machine assertions: proof.json.",
            "Source/binary/input identities: manifest.json and each observations.json."
        ]);
        if (fixVerified.HasValue)
            report.AddRange(["", fixVerified.Value
                ? "The fixed revision passes every remediation proposition: cycle back-references resolve to T, no underlying target load occurs, and target handles are released after Process for both ownership modes."
                : "The fixed revision did not pass every remediation proposition; inspect the failed F checks.",
                "Fixed-revision raw trace: fixed/events.jsonl. This verifies the recorded fixture; broader lifecycle and reuse cases are covered by ReferenceCycleTests."]);
        File.WriteAllLines(Path.Combine(output, "proof.md"), report, Utf8);
        var lines = checks.Select(check => $"{check.id} {(check.passed ? "PASS" : "FAIL")} {check.proposition}")
            .Append($"REGRESSION_REPRODUCED={reproduced.ToString().ToLowerInvariant()}").ToList();
        if (fixVerified.HasValue) lines.Add($"FIX_VERIFIED={fixVerified.Value.ToString().ToLowerInvariant()}");
        File.WriteAllLines(Path.Combine(output, "reproduction.log"), lines, Utf8);
        foreach (var line in lines) Console.WriteLine(line);
        Console.WriteLine($"Evidence directory: {output}");
        return reproduced && fixVerified != false ? 0 : 1;
    }

    private static List<Check> Evaluate(Dictionary<string, JsonNode> observations)
    {
        var checks = new List<Check>();
        void Check(string id, string proposition, bool condition) => checks.Add(new(id, proposition, condition));
        var baseline = observations["baseline"];
        var candidate = observations["candidate"];
        var baseCases = Cases(baseline).ToDictionary(row => row["name"]!.GetValue<string>());
        var headCases = Cases(candidate).ToDictionary(row => row["name"]!.GetValue<string>());
        string[] cycleNames = ["cycle-borrowed", "cycle-owned"];
        var borrowed = headCases[cycleNames[0]];
        var owned = headCases[cycleNames[1]];
        JsonNode[] cycles = [borrowed, owned];

        Check("P1", "Both executions use the same Mono.Cecil binary", JsonNode.DeepEquals(baseline["cecil_sha256"], candidate["cecil_sha256"]));
        Check("P2", "Both revisions receive byte-identical inputs for every corresponding case", baseCases.All(pair =>
            new[] { "target_input_sha256", "dependency_input_sha256" }.All(key => JsonNode.DeepEquals(pair.Value[key], headCases[pair.Key][key]))));
        Check("P3", "CLR accepts the non-cyclic inheritance chain, including the assembly-reference cycle",
            observations.Values.SelectMany(Cases).All(row => Bool(row["clr_graph_valid"])));
        Check("P4", "Every input DLL can be opened exclusively before processing",
            observations.Values.SelectMany(Cases).All(row => Bool(row["before"]!["succeeded"])));
        Check("P5", "The control succeeds and releases the target in both revisions", new[] { baseCases, headCases }.All(revision =>
            Bool(revision["control-borrowed"]["process_completed"]) && Bool(revision["control-borrowed"]["direct_self_is_operation_target"]) &&
            Bool(revision["control-borrowed"]["after_process"]!["succeeded"])));
        Check("P6", "The baseline resolves cycle back-references to the operation target and releases its file", cycleNames.All(name =>
            Bool(baseCases[name]["process_completed"]) && Bool(baseCases[name]["back_reference_is_operation_target"]) &&
            Bool(baseCases[name]["after_process"]!["succeeded"])));
        Check("P7", "Candidate dependency modules use the underlying resolver", cycles.All(row => Bool(row["dependency_resolver_is_underlying"])));
        Check("P8", "The candidate attempts an extra read-only target load through the underlying resolver", cycles.All(row =>
            row["target_lookup_attempts"]!.AsArray().Count == 1 && row["target_lookup_attempts"]![0]!["read_write"]?.GetValue<bool>() == false));
        if (Bool(candidate["is_windows"]))
        {
            Check("P9", "Windows reports ERROR_SHARING_VIOLATION (32) during both candidate cycle operations", cycles.All(row =>
                !Bool(row["process_completed"]) && row["process_exception"]?["type"]?.GetValue<string>() == "System.IO.IOException" &&
                row["process_exception"]?["win32_code"]?.GetValue<int>() == 32 &&
                row["target_lookup_attempts"]?[0]?["error"]?["win32_code"]?.GetValue<int>() == 32));
            Check("P10", "Unwinding the failed operations releases the target", cycles.All(row =>
                Bool(row["after_process"]!["succeeded"]) && Bool(row["after_resolver_dispose"]!["succeeded"])));
        }
        else
        {
            Check("P9", "During processing, candidate back-references are distinct target instances with confirmed cache hits and no extra I/O", cycles.All(row =>
                Bool(row["process_completed"]) && row["back_reference_is_operation_target"]?.GetValue<bool>() == false &&
                Bool(row["back_reference_was_loaded_by_underlying_cache"]) && Bool(row["back_reference_cache_hit_without_io"])));
            Check("P10", "The borrowed cache retains the target lock after processing; disposing it releases the lock",
                !Bool(borrowed["after_process"]!["succeeded"]) && Bool(borrowed["after_resolver_dispose"]!["succeeded"]));
            Check("P11", "Owned-resolver cleanup releases the duplicate target after processing",
                Bool(owned["after_process"]!["succeeded"]) && owned["resolver_disposals_after_process"]!.GetValue<int>() == 1);
            if (OperatingSystem.IsLinux())
                Check("P12", "Linux independently shows an extra target FD, then zero after cache disposal",
                    borrowed["target_fds_before"]!.GetValue<int>() == 0 && borrowed["target_fds_after_process"]!.GetValue<int>() > 0 &&
                    borrowed["target_fds_after_resolver_dispose"]!.GetValue<int>() == 0);
        }
        if (observations.TryGetValue("fixed", out var fixedRevision))
        {
            var fixedCases = Cases(fixedRevision).ToArray();
            Check("F1", "Fixed and reviewed revisions use the same Mono.Cecil binary",
                JsonNode.DeepEquals(fixedRevision["cecil_sha256"], candidate["cecil_sha256"]));
            Check("F2", "Fixed and reviewed revisions receive byte-identical fixture assemblies", fixedCases.Length == headCases.Count &&
                fixedCases.Select(row => row["name"]!.GetValue<string>()).Distinct().Count() == headCases.Count && fixedCases.All(row =>
                new[] { "target_input_sha256", "dependency_input_sha256" }.All(key => JsonNode.DeepEquals(row[key], headCases[row["name"]!.GetValue<string>()][key]))));
            Check("F3", "Every fixed-revision fixture is CLR-valid and initially unlocked", fixedCases.All(row =>
                Bool(row["clr_graph_valid"]) && Bool(row["before"]!["succeeded"])));
            Check("F4", "Every fixed-revision operation completes and direct self-resolution returns T", fixedCases.All(row =>
                Bool(row["process_completed"]) && row["process_exception"] is null && Bool(row["direct_self_is_operation_target"])));
            Check("F5", "Fixed cycle back-references resolve to the operation-owned target T", fixedCases.Where(row => Bool(row["cycle"])).All(row =>
                Bool(row["back_reference_is_operation_target"]) && !Bool(row["back_reference_was_loaded_by_underlying_cache"])));
            Check("F6", "The fixed resolver performs zero underlying target loads", fixedCases.All(row => row["target_lookup_attempts"]!.AsArray().Count == 0));
            Check("F7", "Every fixed-revision target is unlocked immediately after Process and after resolver disposal", fixedCases.All(row =>
                Bool(row["after_process"]!["succeeded"]) && Bool(row["after_resolver_dispose"]!["succeeded"])));
            Check("F8", "Processing disposes owned resolvers once and leaves borrowed resolvers undisposed", fixedCases.All(row =>
                row["resolver_disposals_after_process"]!.GetValue<int>() == (Bool(row["owned_resolver"]) ? 1 : 0)));
            if (OperatingSystem.IsLinux())
                Check("F9", "Linux independently reports zero target FDs after fixed processing", fixedCases.All(row =>
                    row["target_fds_after_process"]!.GetValue<int>() == 0 && row["target_fds_after_resolver_dispose"]!.GetValue<int>() == 0));
        }
        return checks;
    }

    private static async Task<string> Command(string executable, string[] arguments, string? output = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await stdout + await stderr;
        if (output is not null) await File.WriteAllTextAsync(output, text, Utf8);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Command failed ({process.ExitCode}): {executable} {string.Join(' ', arguments)}\n{text}");
        return text.Trim();
    }

    private static IEnumerable<JsonNode> Cases(JsonNode revision) => revision["cases"]!.AsArray().Select(row => row!);
    private static bool Bool(JsonNode? value) => value?.GetValue<bool>() == true;
    private static string Display(JsonNode? value) => value?.ToJsonString() ?? "null";
    private static string SourcePath([CallerFilePath] string path = "") => path;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void WriteJson(string output, string name, object value)
        => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, JsonOptions) + "\n", Utf8);

    private sealed record Check(string id, string proposition, bool passed);
}
