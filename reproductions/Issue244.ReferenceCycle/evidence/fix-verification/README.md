# Reference-cycle fix validation

Implementation commit: `627966b299595db7ff2b775709bb575b7d36913a`.
Reviewed regression: `159da826cc1f6f12ae2da3c5573d1de38b4769a8`.
Original baseline: `fb24e3ce40b99e15d2af3cd40e0f0821ddafa197`.

The [Linux proof](linux/proof.md) rebuilds all three immutable snapshots with the
same probe. All 12 historical P propositions and all 9 fix F propositions pass.
The fixed snapshot resolves the dependency back-reference to the active target,
performs zero underlying target loads, and releases target handles for owned and
borrowed resolvers. The manifests identify sources, binaries, fixture bytes,
runtime, and build commands. Historical evidence in `../linux` and `../windows`
is preserved unchanged.

The [Windows proof](windows/proof.md) checks the same three snapshots. All 10
historical propositions and all 8 fix propositions pass: the reviewed snapshot
throws sharing violation `0x80070020` for both ownership modes, while the fixed
snapshot completes, resolves back-references to the active target, performs no
duplicate target load, and immediately releases target handles. This run used
commit `49de0f440f2da62e66137426a046d869e3de7795`, which adds Linux evidence to
the implementation commit above. The [CI provenance](windows/ci-source.json)
records the job, source commit, artifact, and verified archive digest.

Full Windows and Linux Release CI passed the .NET 6, .NET 8, and .NET 10
suites at the same source commit. The [CI record](ci-validation.json) links the
runs. The first Windows attempt passed all 35 build tests but failed the older
`AdviceAround_Methods` runtime event-order test; a retry passed without source
changes. Its helper waits a fixed 300 ms for an async-void body that delays
200 ms. The [failure excerpt](tests/windows-first-attempt.txt) is preserved.

## Regression tests

The new `ReferenceCycleTests.cs` has SHA-256
`724ded52462eb3e8dcf574d7860664d3a8d2a561fbac0af212db47c5ec081af9`.
The same test source was used before and after the implementation change:

| Implementation | Runtime | Scope | Passed | Failed | Log |
| --- | --- | --- | --- | --- | --- |
| Handoff `6ad8183` | .NET 10 | 14 new cycle cases | 0 | 14 | [Before](tests/before-net10.txt) |
| Fix `627966b` | .NET 8 | Entire build-test project | 35 | 0 | [After](tests/after-net8.txt) |
| Fix `627966b` | .NET 10 | Entire build-test project | 35 | 0 | [After](tests/after-net10.txt) |

Before and .NET 8 logs omit preceding compiler output; test output is preserved.
The before implementation was unchanged while the new tests were added. The
new cases check canonical target identity, no duplicate loads, exclusive file
access, borrowed dependency lifetime, preloaded metadata, replacement targets,
existing cache entries, exceptions, and nested scope restoration.

## Negative control for the verifier

The collector was also run with `--fixed 159da826cc1f6f12ae2da3c5573d1de38b4769a8`.
It reproduced the historical failure but rejected this purported fix with exit
code 1 and `FIX_VERIFIED=false`. F5, F6, F7, and F9 fail, as expected. See the
[assertions](negative-control/proof.json), [log](negative-control/reproduction.log),
[manifest](negative-control/manifest.json), and
[purported fixed observations](negative-control/observations.json).

This is execution evidence for the recorded fixtures and environments, rather
than a proof of correctness for every assembly graph or custom resolver.

Astra's follow-up review confirms the project-resolver fix but identifies a
remaining third-party caching resolver case. Its independent three-snapshot
[counterexample](../third-party-limit/proof.md) intentionally reports
`FIX_VERIFIED=false`; the [reproduction instructions](../../README.md#remaining-third-party-resolver-finding)
describe the API boundary and required extension. This case is not included in
the successful default-profile fix claim above.
