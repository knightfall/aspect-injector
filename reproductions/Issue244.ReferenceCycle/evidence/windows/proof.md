# Reference-cycle reproduction proof

Baseline: `fb24e3ce40b99e15d2af3cd40e0f0821ddafa197`. Candidate: `159da826cc1f6f12ae2da3c5573d1de38b4769a8`.
Platform: Microsoft Windows 10.0.26100. Runtime: .NET 10.0.13.

Let T be the operation-owned target, B the target reached through Dependency.Middle.BaseType,
and X(p) mean that opening file p with FileShare.None succeeds. Object equality is checked
with ReferenceEquals; displayed object IDs are diagnostic only.

| Revision | Case | B = T | Process completed | X(target) after Process | X(target) after resolver disposal |
| --- | --- | --- | --- | --- | --- |
| baseline | control-borrowed | null | true | true | true |
| baseline | cycle-borrowed | true | true | true | true |
| baseline | cycle-owned | true | true | true | true |
| candidate | control-borrowed | null | true | true | true |
| candidate | cycle-borrowed | null | false | true | true |
| candidate | cycle-owned | null | false | true | true |

## Checked propositions

- PASS **P1**: Both executions use the same Mono.Cecil binary.
- PASS **P2**: Both revisions receive byte-identical inputs for every corresponding case.
- PASS **P3**: CLR accepts the non-cyclic inheritance chain, including the assembly-reference cycle.
- PASS **P4**: Every input DLL can be opened exclusively before processing.
- PASS **P5**: The control succeeds and releases the target in both revisions.
- PASS **P6**: The baseline resolves cycle back-references to the operation target and releases its file.
- PASS **P7**: Candidate dependency modules use the underlying resolver.
- PASS **P8**: The candidate attempts an extra read-only target load through the underlying resolver.
- PASS **P9**: Windows reports ERROR_SHARING_VIOLATION (32) during both candidate cycle operations.
- PASS **P10**: Unwinding the failed operations releases the target.

## Conclusion

The reference-cycle regression is reproduced.
The control and baseline establish that this fixture is valid and initially unlocked. The candidate's
lookup trace establishes the bypass through the dependency resolver.
The recorded Windows sharing violation identifies the conflicting open during processing.

This is an executable counterexample for the recorded inputs, revisions, runtime, and platform.
Only propositions actually checked on this platform are evidence from this run. It does not claim
that every application has an assembly-reference cycle or explain unrelated apphost.exe failures.

Raw traces: baseline/events.jsonl and candidate/events.jsonl. Machine assertions: proof.json.
Source/binary/input identities: manifest.json and each observations.json.
