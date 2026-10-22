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
