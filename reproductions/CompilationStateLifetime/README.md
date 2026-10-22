# Deferred compilation state hardening

This branch keeps the cache and callback changes removed from #244. The
[upstream scope review](../Issue244.ReferenceCycle/scope-review.md) found no
demonstrated connection to a reported upstream issue.

`AspectReader` caches extraction results per reader. Weak module and method-body
keys prevent static caches from retaining completed metadata graphs. Modification
callbacks are local to the weaving thread and restored when processing exits.
Short comments describe these lifetime boundaries in the implementation.

`CompilationStateTests.cs` preserves the four moved test cases: per-reader cache
isolation, callback restoration on success and failure, and callback isolation
between threads. With the #244 tests, this branch has 35 build-test cases.

These tests do not prove general concurrent weaving safety or measure memory
retention, throughput, or scale. The inner module collections and same-body
return-local creation still require serialized access. Performance and explicit
GC-lifetime tests remain work for a separate review before proposing this branch.
