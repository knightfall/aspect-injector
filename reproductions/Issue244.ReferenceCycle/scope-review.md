# Scope review: compilation caches and callbacks

The upstream review covered all 196 issues available on 22 October 2026 and the
closest comment threads. No reported issue establishes that the compilation
cache or modification callback changes are needed for #244.

| Upstream issue | Finding |
| --- | --- |
| [#244](https://github.com/pamidur/aspect-injector/issues/244) | Reports intermediate DLL/AppHost locking after upgrading to 2.9.0. Target and dependency disposal directly address file ownership. No cache/callback root cause is established. |
| [#225](https://github.com/pamidur/aspect-injector/issues/225), [#144](https://github.com/pamidur/aspect-injector/issues/144) | Concern application `MemoryCache` aspects and scoped service instances. These are runtime caches. |
| [#137](https://github.com/pamidur/aspect-injector/issues/137), [#162](https://github.com/pamidur/aspect-injector/issues/162), [#209](https://github.com/pamidur/aspect-injector/issues/209) | Request generated delegate/metadata caching for application performance. These do not concern weaver cache lifetime. |
| [#226](https://github.com/pamidur/aspect-injector/issues/226) | Reports a closed-file exception through an older custom tool. The maintainer requested a retry on 2.9.0-pre1; no reproducer establishes a link to these caches or callbacks. |
| [#232](https://github.com/pamidur/aspect-injector/issues/232) | Reports an intermittent missing singleton with parallel MSBuild. The maintainer requested a retry on 2.9.0-pre1; the cause is unconfirmed. |
| [#141](https://github.com/pamidur/aspect-injector/issues/141) | An older DLL/PDB lock report was closed without a reproducer. It does not establish a callback or weak-cache fix. |

The following hardening is deferred to
[`followup/compilation-state-lifetime`](https://github.com/knightfall/aspect-injector/tree/followup/compilation-state-lifetime):

- Per-reader `AspectReader` caching.
- Weak module/type and method-body caches.
- Thread-local modification callbacks and restoration after processing.
- Four cache/callback test cases, moved into `CompilationStateTests.cs`.

The active #244 branch retains explicit target/dependency disposal, resolver
ownership, temporary target registration for reference cycles, .NET 10 support,
and compatibility tests. Its build-test project now has 31 cases. Resolver-owned
assembly cache disposal stays in this fix because it releases open files; weak
metadata caching does not perform that disposal.

Historical proof captures and test logs remain unchanged. Their 35-case results
describe their recorded commits, which still included the four deferred cases.
The remaining third-party resolver finding documented in the reproduction
README also remains open. The deferred work has not been benchmarked and does
not establish general concurrent weaving safety.
