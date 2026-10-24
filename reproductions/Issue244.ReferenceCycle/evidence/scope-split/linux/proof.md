# Reference-cycle reproduction proof

Baseline: `fb24e3ce40b99e15d2af3cd40e0f0821ddafa197`. Candidate: `159da826cc1f6f12ae2da3c5573d1de38b4769a8`.
Platform: Debian GNU/Linux 13 (trixie). Runtime: .NET 10.0.13.
Fixed revision: `1b248098100f2d9e8201460201827dbb3f843e9b`.

Let T be the operation-owned target, B the target reached through Dependency.Middle.BaseType,
and X(p) mean that opening file p with FileShare.None succeeds. Object equality is checked
with ReferenceEquals; displayed object IDs are diagnostic only.

| Revision | Case | B = T | Process completed | X(target) after Process | X(target) after resolver disposal |
| --- | --- | --- | --- | --- | --- |
| baseline | control-borrowed | null | true | true | true |
| baseline | cycle-borrowed | true | true | true | true |
| baseline | cycle-owned | true | true | true | true |
| candidate | control-borrowed | null | true | true | true |
| candidate | cycle-borrowed | false | true | false | true |
| candidate | cycle-owned | false | true | true | true |
| fixed | control-borrowed | null | true | true | true |
| fixed | cycle-borrowed | true | true | true | true |
| fixed | cycle-owned | true | true | true | true |

## Checked propositions

- PASS **P1**: Both executions use the same Mono.Cecil binary.
- PASS **P2**: Both revisions receive byte-identical inputs for every corresponding case.
- PASS **P3**: CLR accepts the non-cyclic inheritance chain, including the assembly-reference cycle.
- PASS **P4**: Every input DLL can be opened exclusively before processing.
- PASS **P5**: The control succeeds and releases the target in both revisions.
- PASS **P6**: The baseline resolves cycle back-references to the operation target and releases its file.
- PASS **P7**: Candidate dependency modules use the underlying resolver.
- PASS **P8**: The candidate attempts an extra read-only target load through the underlying resolver.
- PASS **P9**: During processing, candidate back-references are distinct target instances with confirmed cache hits and no extra I/O.
- PASS **P10**: The borrowed cache retains the target lock after processing; disposing it releases the lock.
- PASS **P11**: Owned-resolver cleanup releases the duplicate target after processing.
- PASS **P12**: Linux independently shows an extra target FD, then zero after cache disposal.
- PASS **F1**: Fixed and reviewed revisions use the same Mono.Cecil binary.
- PASS **F2**: Fixed and reviewed revisions receive byte-identical fixture assemblies.
- PASS **F3**: Every fixed-revision fixture is CLR-valid and initially unlocked.
- PASS **F4**: Every fixed-revision operation completes and direct self-resolution returns T.
- PASS **F5**: Fixed cycle back-references resolve to the operation-owned target T.
- PASS **F6**: The fixed resolver performs zero underlying target loads.
- PASS **F7**: Every fixed-revision target is unlocked immediately after Process and after resolver disposal.
- PASS **F8**: Processing disposes owned resolvers once and leaves borrowed resolvers undisposed.
- PASS **F9**: Linux independently reports zero target FDs after fixed processing.

## Conclusion

The reference-cycle regression is reproduced.
The control and baseline establish that this fixture is valid and initially unlocked. The candidate's
lookup trace establishes the bypass through the dependency resolver.
The cache-disposal intervention identifies the retained handle; Linux FD observations independently corroborate it.

This is an executable counterexample for the recorded inputs, revisions, runtime, and platform.
Only propositions actually checked on this platform are evidence from this run. It does not claim
that every application has an assembly-reference cycle or explain unrelated apphost.exe failures.

Raw traces: baseline/events.jsonl and candidate/events.jsonl. Machine assertions: proof.json.
Source/binary/input identities: manifest.json and each observations.json.

The fixed revision passes every remediation proposition: cycle back-references resolve to T, no underlying target load occurs, and target handles are released after Process for both ownership modes.
Fixed-revision raw trace: fixed/events.jsonl. This verifies the recorded fixture; broader lifecycle and reuse cases are covered by ReferenceCycleTests.
