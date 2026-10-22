# Reproduce the dependency back-reference lock

This records Astra's P2 finding against commit
`159da826cc1f6f12ae2da3c5573d1de38b4769a8`: a dependency can resolve a reference
back to the target through the underlying cache, bypassing `TargetAssemblyResolver`.

The fixture has an assembly-reference cycle and an acyclic, CLR-validated type
hierarchy: `Target.Leaf : Dependency.Middle : Target.Root : System.Object`.

The [scope review](scope-review.md) records why compilation cache and callback
hardening was moved to a separate branch. Historical captures remain unchanged.

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
