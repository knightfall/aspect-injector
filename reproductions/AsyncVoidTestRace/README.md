# Async void runtime test race

The Windows [.NET 8 CI failure](https://github.com/knightfall/aspect-injector/actions/runs/39854073070/job/120087573877)
at commit `212bacb837a232b3c27c2c94e14a9c5c47334172` is retained in
[before-windows-net8.txt](before-windows-net8.txt).

`Around.Instance_Tests.AdviceAround_Methods` expects the async-void exit before
the next operation's around-entry event. The log shows that the next operation
started first. Its helper waited a fixed 300 ms for a method containing a
200 ms asynchronous delay, which does not guarantee continuation completion.
The .NET 6 runtime suite and Linux jobs passed in that same run; Windows .NET 10
was skipped after the .NET 8 failure.

The test-only fix uses xUnit's existing `AsyncTestSyncContext` to track operation
completion and asynchronous failures. A separate context with no inner runner
context permits synchronous callers without blocking continuation dispatch.
The helper restores the caller's context in `finally` and fails after 30 seconds
if an operation never completes. It covers the instance/static fixture helpers
and the after-advice async-void test, which used the same fixed-wait pattern.
The async-void targets and event-order assertions remain unchanged.

Four new cases check completion after a 500 ms operation (longer than the old
wait), asynchronous failures before/after suspension, synchronous invocation
failures, and restoration of the original synchronization context. These checks
keep async-void coverage while preventing the next operation from starting early.

The local runtime suite passes all 160 cases on [.NET 6](after-net6.txt),
[.NET 8](after-net8.txt), and [.NET 10](after-net10.txt). Captured test output
omits preceding compiler warnings. This fixes test coordination; it does not
change weaving, resolver behavior, or the documented external-resolver finding.
The runtime project also builds successfully across all five targets, including
.NET Framework 4.6.2 and 4.8.1.

Run the runtime suites with:

```sh
dotnet test tests/AspectInjector.Tests.Runtime -c Release -f net6.0
dotnet test tests/AspectInjector.Tests.Runtime -c Release -f net8.0
dotnet test tests/AspectInjector.Tests.Runtime -c Release -f net10.0
```
