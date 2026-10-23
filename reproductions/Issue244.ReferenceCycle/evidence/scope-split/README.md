# Validation after separating compilation state hardening

Implementation: `1b248098100f2d9e8201460201827dbb3f843e9b`.
The [scope review](../../scope-review.md) describes the upstream issue research
and the five production files and four test cases deferred to another branch.

The narrowed branch passes all 31 build-test cases on [.NET 8](tests/net8.txt)
and [.NET 10](tests/net10.txt). The 14 reference-cycle cases remain in this
suite, alongside target/symbol cleanup and console build/rebuild/run checks.

The new [Linux proof](linux/proof.md) rebuilds the baseline, reviewed regression,
and narrowed implementation from immutable commits. All 12 historical and 9 fix
assertions pass. Input bytes and Cecil binary hashes agree between revisions;
fixed cycle operations resolve the active target, load no duplicate target,
and leave no target file descriptors open after processing. This demonstrates
that the recorded resolver/handle fix does not require the deferred weak-cache
or callback changes for these fixtures.

The [Windows proof](windows/proof.md) passes all 10 historical and 8 fix
assertions at the same implementation commit. Its artifact archive digest was
verified before extraction. Both full Windows/Linux Release jobs pass the
.NET 6, .NET 8, and .NET 10 suites. The [CI provenance](ci-validation.json)
records the run URLs, job IDs, and Windows artifact identity.

Run the same check:

```sh
dotnet run --file reproductions/Issue244.ReferenceCycle/reproduce.cs -- --fixed 1b248098100f2d9e8201460201827dbb3f843e9b
```

The checked-in observations, events, manifests, and assertions describe the
recorded Linux and Windows runs. They do not establish general correctness for
all assembly graphs or custom resolvers. The third-party caching resolver finding
remains open, and earlier captures are preserved unchanged.
