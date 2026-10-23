# Reproduce the dependency back-reference lock

This records Astra's P2 finding against commit
`159da826cc1f6f12ae2da3c5573d1de38b4769a8`: a dependency can resolve a reference
back to the target through the underlying cache, bypassing `TargetAssemblyResolver`.

The fixture has an assembly-reference cycle and an acyclic, CLR-validated type
hierarchy: `Target.Leaf : Dependency.Middle : Target.Root : System.Object`.

The [scope review](scope-review.md) records why compilation cache and callback
hardening was moved to a separate branch. Historical captures remain unchanged.

## How the proof works

The proof is a controlled comparison of the original baseline, the reviewed
candidate containing the regression, and an optional fixed revision. It asks
whether a dependency's reference back to `Target.dll` resolves to the assembly
object already owned by the active `Process` call, and whether processing
releases the target's file handle.

A resolver loads metadata for referenced DLLs. Let `T` be the in-memory Cecil
assembly object for the active target. Walking the fixture's inheritance chain
loads `Dependency.dll`, then resolves `Dependency.Middle`'s base type back into
`Target.dll`:

```mermaid
flowchart LR
    T["Target.dll: active assembly object T"] -->|loads dependency| D["Dependency.dll"]
    D -->|correct: reuse T| T
    D -->|regression: load another copy| T2["Same Target.dll: second object T2"]
```

The first and last application types in the inheritance chain belong to the
same DLL. This creates a cycle between assembly references while keeping the
type hierarchy acyclic. The probe validates that hierarchy with the CLR by
loading the fixture from byte streams, so validation does not retain file handles
on the probe inputs.

The regressed implementation attempts another target load through the underlying
resolver. On Linux and macOS, the resulting `T2` enters the cache, so disposing
`T` after processing leaves `T2` and its file handle alive in a caller-owned
resolver. Windows rejects the extra load with a sharing violation, as detailed
below. The fixed implementation temporarily registers `T` with the shared
resolver so the back-reference reuses it without another target load. The probe
uses `ReferenceEquals` to distinguish the objects: two objects can represent the
same assembly name and file path while owning separate handles.

The same [probe](Program.cs) runs three cases against each revision:

| Case | Dependency refers back to target | Resolver ownership | What it isolates |
| --- | --- | --- | --- |
| `control-borrowed` | No | Caller owns it | Valid inputs and ordinary resolution without the cycle |
| `cycle-borrowed` | Yes | Caller owns it | Target identity and handles retained after `Process` returns |
| `cycle-owned` | Yes | Processing owns it | Target identity and cleanup when processing disposes its resolver |

For each case, the probe first checks that the target can be opened exclusively.
It then runs the real `PatcherBase.Process` path, recording resolver lookups,
object identity, and disposal calls. It attempts another exclusive .NET file
open immediately after processing, explicitly disposes the resolver, and tries
the exclusive open again. Remaining fixture cleanup happens after those
measurements. No garbage collection or target rewrite is requested between the
two final measurements.

The recording patcher walks the fixture's type graph and reports a modification
to exercise the real write path, including the baseline's target cleanup there.
This experiment exercises assembly loading, resolution, writing, and disposal.
Broader aspect weaving, lifecycle, and MSBuild integration checks are separate.

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
Each revision's built probe runs in a separate process. Production sources come
from the selected Git snapshots; the probe source, project, and collector come
from the invoking checkout and have their hashes recorded in `manifest.json`.
Consequently, `--fixed HEAD` selects committed production sources, excluding
uncommitted production edits.

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
is manually triggered and runs the same collector on Windows, Linux, and macOS.
It uploads evidence as `reference-cycle-proof-Windows`,
`reference-cycle-proof-Linux`, and `reference-cycle-proof-macOS` artifacts.

## Proof obligations

Let `T` denote the operation-owned target assembly, `B` the assembly reached by
resolving `Dependency.Middle.BaseType`, and `X(p)` a successful exclusive
read/write open of path `p` with `FileShare.None`.

The [collector](reproduce.cs) evaluates the recorded `observations.json` objects.
Each printed PASS or FAIL is computed from the identity, lookup, file-open, and
disposal observations.

The collector checks that both revisions receive byte-identical input assemblies,
load the same Mono.Cecil binary, accept the fixture through the CLR, and start with
`X(target) = true`. A control with no dependency back-reference succeeds on both
revisions. On the baseline, the cyclic fixture also succeeds, `B = T`, and the
target is unlocked after processing.

On Linux and macOS, the candidate's trace establishes `B != T`, that the
underlying cache loaded `B`, and `X(target) = false` after borrowed-resolver
processing. Disposing that cache changes `X(target)` to true, identifying its
retained target handle. A repeated resolution must return the cached object
without another underlying lookup.

The expected `cycle-borrowed` comparison on these platforms is:

| Revision | Back-reference is `T` | Exclusive open after `Process` | Exclusive open after resolver disposal |
| --- | --- | --- | --- |
| Original baseline | Yes | Succeeds | Succeeds |
| Regressed candidate | No | Fails | Succeeds |
| Fixed revision, when supplied | Yes | Succeeds | Succeeds |

The owned-resolver case distinguishes the identity error from cleanup at the end
of processing: the regressed version can release the file by disposing its
resolver while still having resolved the back-reference to a duplicate object.
Linux additionally counts target file descriptors through `/proc/self/fd` to
corroborate the retained handle and its disappearance. macOS uses object identity,
resolver traces, and exclusive-open checks without Linux file-descriptor counts.
With `--fixed`, macOS evaluates P1-P11 and F1-F8, for 19 assertions; Linux adds
P12 and F9.

On Windows, the collector instead requires an actual `IOException` with
`ERROR_SHARING_VIOLATION` (32) during the extra read-only target lookup, for both
owned and borrowed resolvers. The checked-in Windows evidence records this
failure in both cases, with HRESULT `0x80070020` and full exception stack traces.

These assertions establish a concrete counterexample and its cache/handle
mechanism for the recorded revisions and environment. They do not prove that all
applications contain such a reference cycle, or identify the cause of every
`apphost.exe` access-denied report.

## Verify the fix

To reproduce the historical failure and check a fixed revision in the same run:

```sh
dotnet run --file reproductions/Issue244.ReferenceCycle/reproduce.cs -- --fixed HEAD --output artifacts/reference-cycle-fix-proof
```

`--fixed` adds a third immutable Git snapshot. The original P propositions still
require the reviewed candidate to reproduce the regression. Additional F
propositions require the fixed revision to receive identical fixture bytes and
the same Cecil binary, resolve each cycle back-reference to the active target,
perform zero underlying target loads, and release the target immediately after
processing in both ownership modes. They also check resolver ownership and Linux
file descriptors. With `--fixed`, exit code 0 requires both the historical
counterexample and every remediation proposition to pass. The output adds
`fixed/events.jsonl`, `fixed/observations.json`, and `FIX_VERIFIED` in the log.

The fix temporarily registers the operation target in `CachedAssemblyResolver`,
which is also used by dependency modules. The registration takes priority over
persistent cache entries, restores a previous registration for nested processing,
and is removed on success and exceptions. It neither adds the target to the
persistent cache nor disposes borrowed dependencies. This also covers dependencies
loaded before processing, without rebinding their module resolvers.

`ReferenceCycleTests` adds 14 cases for owned and borrowed processing, modified
and unmodified outputs, exceptions, preloaded dependencies, replaced input
targets, existing caller cache entries, and nested operations. The tests assert
canonical target identity and exclusive file access; a successful write alone
would miss the Linux duplicate-instance defect.

The existing historical evidence directories above remain unchanged. New
[before/after proof and test logs](evidence/fix-verification/README.md) record
verification of the fix and a negative control that rejects the buggy revision.

## Remaining third-party resolver finding

The fix covers `CachedAssemblyResolver` and its subclasses, including the normal
MSBuild `KnownReferencesAssemblyResolver`. Astra's follow-up review found a
remaining case with an external resolver that inherits Cecil's
`DefaultAssemblyResolver` and caches **both** `Resolve` overloads. This differs
from plain `DefaultAssemblyResolver`, whose parameterized reads are uncached.
The external cache succeeds on the original baseline, but current cycle
back-references still bypass the scoped registration. Existing non-cycle tests
of external caching resolver reuse do not cover this cycle behavior.

The collector includes this resolver as an explicit profile:

```sh
dotnet run --file reproductions/Issue244.ReferenceCycle/reproduce.cs -- --resolver external --fixed HEAD --output artifacts/reference-cycle-external-limit
```

This currently exits **1**, with `REGRESSION_REPRODUCED=true` and
`FIX_VERIFIED=false`. The [captured Linux counterexample](evidence/third-party-limit/proof.md)
uses the same immutable baseline, reviewed, and fixed snapshots. The fixed
snapshot fails F5, F6, F7, and F9: a duplicate target is loaded, and the borrowed
cache retains its handle. This external-profile capture is Linux evidence;
the successful Windows fix evidence uses the default project resolver profile.

There is no transparent repair for arbitrary preloaded dependency modules through
`IAssemblyResolver` alone: cache hits can ignore new reader parameters, Cecil's
module resolvers have no public setters, and the interface offers no cache
registration, eviction, or dependency ownership protocol. Disposing or replacing
caller-owned definitions would violate the borrowed resolver contract. A broader
solution needs an explicit target-registration capability implemented by the
external resolver, or a routing wrapper installed before dependencies are loaded
with defined ownership rules. Until then, use the project's resolver hierarchy
for cyclic graphs. The PR does not claim that the external case is fixed.
