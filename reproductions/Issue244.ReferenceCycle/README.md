# Reproduce the dependency back-reference lock

This records Astra's P2 finding against commit
`159da826cc1f6f12ae2da3c5573d1de38b4769a8`: a dependency can resolve a reference
back to the target through the underlying cache, bypassing `TargetAssemblyResolver`.

The fixture has an assembly-reference cycle and an acyclic, CLR-validated type
hierarchy: `Target.Leaf : Dependency.Middle : Target.Root : System.Object`.

## Run

From a checkout of this PR with its Git history available, install the .NET 10
SDK and Git, then run this .NET file-based app:

```sh
dotnet run --file reproductions/Issue244.ReferenceCycle/reproduce.cs
```

Pass app options after `--`. Use `--dotnet /path/to/dotnet` to select the SDK executable and `--output PATH`
to select the evidence directory. The defaults compare these immutable commits:

- Baseline: `fb24e3ce40b99e15d2af3cd40e0f0821ddafa197`.
- Reviewed candidate: `159da826cc1f6f12ae2da3c5573d1de38b4769a8`.

The runner extracts both revisions with `git archive` and builds the same probe
against each revision's actual `FluentIL` project. It does not substitute a copied
or edited baseline implementation. The probe overrides the resolver factory to
provide an instrumented resolver with the same search directory and reference
list, and records lookup/disposal events without changing their behavior.

Exit code **0 means the known regression was reproduced** and every proof
obligation passed. Exit code 1 means at least one expected observation was not
established. This is an evidence collector for pinned snapshots, rather than a
test asserting that the reviewed candidate is fixed.

## Evidence

The default output directory is `artifacts/reference-cycle-proof`:

| File | Evidence |
| --- | --- |
| `reproduction.log` | Human-readable PASS/FAIL result for each proposition |
| `proof.md` | Definitions, comparison table, propositions, and conclusion |
| `proof.json` | Machine-readable proposition results |
| `manifest.json` | Commit IDs, source/probe/binary hashes, build commands, SDK/runtime information |
| `{baseline,candidate}/events.jsonl` | Ordered raw lookup, identity, file-open, and disposal events |
| `{baseline,candidate}/observations.json` | Complete observations, including input hashes and exceptions |
| `{baseline,candidate}/build.log` | Actual build output |

Captured runs are checked in under [evidence/linux](evidence/linux/proof.md) and
[evidence/windows](evidence/windows/proof.md). The Windows run came from
[GitHub Actions run 39813372739](https://github.com/knightfall/aspect-injector/actions/runs/39813372739);
its [provenance record](evidence/windows/ci-source.json) identifies the commit,
job, artifact, and verified archive digest.
The [reproduction workflow](../../.github/workflows/reference-cycle-proof.yml)
runs the same collector on Windows and Linux and uploads all evidence as
`reference-cycle-proof-Windows` and `reference-cycle-proof-Linux` artifacts.

## Proof obligations

Let `T` denote the operation-owned target assembly, `B` the assembly reached by
resolving `Dependency.Middle.BaseType`, and `X(p)` a successful exclusive
read/write open of path `p` with `FileShare.None`.

The collector checks that both revisions receive byte-identical input assemblies,
load the same Mono.Cecil binary, accept the fixture through the CLR, and start with
`X(target) = true`. A control with no dependency back-reference succeeds on both
revisions. On the baseline, the cyclic fixture also succeeds, `B = T`, and the
target is unlocked after processing.

On Linux, the candidate's trace establishes `B != T`, that the underlying cache
loaded `B`, and `X(target) = false` after borrowed-resolver processing. Disposing
that cache changes `X(target)` to true. `/proc/self/fd` independently records the
extra target handle and its disappearance. No target rewrite or garbage
collection is requested between those two measurements. The owned-resolver case
distinguishes the identity error from cleanup at the end of the outer operation.

On Windows, the collector instead requires an actual `IOException` with
`ERROR_SHARING_VIOLATION` (32) during the extra read-only target lookup, for both
owned and borrowed resolvers. The checked-in Windows evidence records this
failure in both cases, with HRESULT `0x80070020` and full exception stack traces.

These assertions establish a concrete counterexample and its cache/handle
mechanism for the recorded revisions and environment. They do not prove that all
applications contain such a reference cycle, or identify the cause of every
`apphost.exe` access-denied report.
