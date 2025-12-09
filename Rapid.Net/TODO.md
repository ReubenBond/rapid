* [ ] Modify MembershipService.LeaveAsync to accept a CancellationToken
* [ ] Respect SimulationHarness.TeardownCancellationToken in RunUntilIdleCore and RunUntilCore, so tests promptly exit when the teardown signal is triggered
* [ ] Investigate and fix this warning seen during test execution:
    [ServerTestHost.OnTaskSchedulerUnobservedTaskException] Unhandled exception: System.AggregateException: A Task's exception(s) were not observed either by Waiting on the Task or accessing its Exception property. As a result, the unobserved exception was rethrown by the finalizer thread. (The CancellationTokenSource has been disposed.)
    ---> System.ObjectDisposedException: The CancellationTokenSource has been disposed.
    at System.Threading.CancellationTokenSource.Cancel()
    at Rapid.Monitoring.PingPongFailureDetector.StopMonitoring() in C:\dev\rapid\Rapid.Net\src\Rapid.Core\Monitoring\PingPongFailureDetector.cs:line 99
    at Rapid.Monitoring.PingPongFailureDetector.ProbeOnceAsync() in C:\dev\rapid\Rapid.Net\src\Rapid.Core\Monitoring\PingPongFailureDetector.cs:line 94
    at Rapid.Monitoring.PingPongFailureDetector.ProbeAsync() in C:\dev\rapid\Rapid.Net\src\Rapid.Core\Monitoring\PingPongFailureDetector.cs:line 66
* [ ] Investigate and fix this warning seen during test execution:
    [ServerTestHost.OnTaskSchedulerUnobservedTaskException] Unhandled exception: System.AggregateException: A Task's exception(s) were not observed either by Waiting on the Task or accessing its Exception property. As a result, the unobserved exception was rethrown by the finalizer thread. (Test exception)
    ---> System.InvalidOperationException: Test exception
    at Rapid.Tests.SimulationTests.SubscriptionDetailTests.<>c.<CallbackExceptionDoesNotCrashService>b__19_0(ClusterStatusChange _) in C:\dev\rapid\Rapid.Net\tests\Rapid.Tests\Simulation\SubscriptionDetailTests.cs:line 493
    at Rapid.MembershipService.DecideViewChange(List`1 proposal) in C:\dev\rapid\Rapid.Net\src\Rapid.Core\MembershipService.cs:line 659
    at Rapid.MembershipService.<RegisterFastPaxosDecidedContinuation>b__96_0(Task`1 decision) in C:\dev\rapid\Rapid.Net\src\Rapid.Core\MembershipService.cs:line 934
* [ ] Implement the TODOs in MembershipService.cs
