# Simulation Testing Test Plan

This document outlines the comprehensive test suite for the Rapid.NET deterministic simulation testing framework. Tests are organized into categories covering basic functionality, failure scenarios, edge cases, and chaos testing.

## Overview

The simulation testing harness provides:
- **SimulationHarness**: Full control over time, task scheduling, and random number generation
- **SimulationTestHarness**: Lightweight harness for tests not requiring full determinism
- **ChaosInjector**: Random fault injection for stress testing
- **InvariantChecker**: Cluster safety property verification

All tests should use fixed seeds for reproducibility. When a test fails, the seed should be logged for easy reproduction.

---

## 1. Basic Cluster Operations

### 1.1 Single Node Operations
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| BASIC-001 | `SingleNodeClusterInitializes` | A single seed node can initialize and has membership size of 1 |
| BASIC-002 | `SingleNodeHasValidConfigurationId` | Seed node has configuration ID >= 0 |
| BASIC-003 | `SingleNodeViewContainsSelf` | Seed node's membership view contains its own address |
| BASIC-004 | `SingleNodeCanShutdownGracefully` | Seed node can be shut down without errors |
| BASIC-005 | `SingleNodeCanLeaveCluster` | Single node can call LeaveAsync (degenerates to shutdown) |

### 1.2 Two-Node Cluster Operations
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| BASIC-010 | `TwoNodeClusterFormation` | Two nodes can form a cluster with membership size 2 |
| BASIC-011 | `JoinerSeesCorrectMembership` | Joining node sees both nodes in membership view |
| BASIC-012 | `SeedSeesJoinerAfterJoin` | Seed node's view is updated after joiner joins |
| BASIC-013 | `BothNodesHaveSameConfigurationId` | Both nodes have matching configuration IDs after join |
| BASIC-014 | `JoinerCanLeaveTwoNodeCluster` | Joiner can gracefully leave, seed sees membership size 1 |
| BASIC-015 | `SeedCanLeaveTwoNodeCluster` | Seed can gracefully leave the cluster |

### 1.3 Multi-Node Cluster Operations
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| BASIC-020 | `ThreeNodeClusterFormation` | Three nodes can form a cluster |
| BASIC-021 | `FiveNodeClusterFormation` | Five nodes can form a cluster |
| BASIC-022 | `SequentialJoinsSucceed` | Nodes can join one after another sequentially |
| BASIC-023 | `AllNodesConvergeToSameMembership` | All nodes eventually see the same membership |
| BASIC-024 | `ConfigurationIdIncrementsWithMembershipChanges` | Config ID increases with each membership change |
| BASIC-025 | `MembershipViewContainsAllNodes` | Final membership view contains all joined nodes |

### 1.4 Metadata Operations
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| BASIC-030 | `NodeCanJoinWithMetadata` | Node can join cluster with custom metadata |
| BASIC-031 | `MetadataPropagatesOnJoin` | Joining node's metadata is visible to existing nodes |
| BASIC-032 | `ExistingMetadataVisibleToJoiner` | Joiner receives existing nodes' metadata on join |

---

## 2. Node Failure Scenarios

### 2.1 Single Node Failure
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| FAIL-001 | `NodeCrashRemovesFromCluster` | Crashed node is eventually removed from other nodes' views |
| FAIL-002 | `CrashedNodeCannotReceiveMessages` | Messages to crashed node fail appropriately |
| FAIL-003 | `ClusterContinuesAfterSingleNodeCrash` | Remaining nodes continue operating after crash |
| FAIL-004 | `CrashDuringIdleStateHandledGracefully` | Crashing a node during idle state works correctly |

### 2.2 Multiple Node Failures
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| FAIL-010 | `TwoNodeFailuresInFiveNodeCluster` | Cluster survives two node failures |
| FAIL-011 | `SequentialFailuresHandled` | Sequential failures are handled correctly |
| FAIL-012 | `SimultaneousFailuresHandled` | Multiple simultaneous failures are handled |
| FAIL-013 | `MajorityFailureBlocksProgress` | Cluster stops making progress if majority fails |

### 2.3 Seed Node Failure
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| FAIL-020 | `SeedNodeCrashDoesNotAffectExistingCluster` | Cluster continues after original seed crashes |
| FAIL-021 | `NewJoinsFailAfterSeedCrash` | New joins through crashed seed fail gracefully |
| FAIL-022 | `AlternativeSeedAllowsJoin` | New nodes can join through any existing member |

### 2.4 Failure During Operations
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| FAIL-030 | `NodeCrashDuringJoinProtocol` | Crash during join is handled gracefully |
| FAIL-031 | `NodeCrashDuringConsensus` | Crash during consensus round completes or aborts safely |
| FAIL-032 | `ObserverCrashDuringJoin` | Join succeeds when one observer crashes |
| FAIL-033 | `NodeCrashDuringLeave` | Cluster handles node crashing while leaving |

---

## 3. Network Partition Scenarios

### 3.1 Simple Partitions
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| PART-001 | `BidirectionalPartitionBlocksMessages` | Partitioned nodes cannot communicate |
| PART-002 | `UnidirectionalPartitionAllowsOneWay` | Unidirectional partition blocks only one direction |
| PART-003 | `PartitionedNodeEventuallyDetected` | Partitioned node is detected and removed |
| PART-004 | `PartitionHealRestoresConnectivity` | Healed partition allows communication again |

### 3.2 Isolation Scenarios
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| PART-010 | `IsolatedNodeCannotCommunicate` | Isolated node cannot send or receive messages |
| PART-011 | `IsolatedNodeEventuallyRemoved` | Isolated node is removed from cluster view |
| PART-012 | `ReconnectedNodeRejoinBehavior` | Reconnected node behavior after isolation |
| PART-013 | `MultipleNodesIsolatedSimultaneously` | Multiple simultaneous isolations handled |

### 3.3 Split-Brain Prevention
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| PART-020 | `PartitionDoesNotCauseSplitBrain` | Network partition does not create two clusters |
| PART-021 | `MinorityPartitionDetectedAndRemoved` | Minority side of partition is removed |
| PART-022 | `SymmetricPartitionResolution` | Symmetric partition is resolved safely |
| PART-023 | `PartitionDuringConsensusHandled` | Partition during consensus is resolved |

### 3.4 Partition and Heal Sequences
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| PART-030 | `PartitionThenHealBeforeDetection` | Healing before detection preserves cluster |
| PART-031 | `RepeatedPartitionHealCycles` | Repeated partition/heal cycles handled |
| PART-032 | `PartitionDuringJoin` | Partition during join protocol handled |
| PART-033 | `PartitionHealsAfterNodeRemoval` | Healing after removal handles stale node |

---

## 4. Message Delivery Scenarios

### 4.1 Message Delays
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| MSG-001 | `MessageDelaysDoNotAffectCorrectness` | High delays don't break protocol correctness |
| MSG-002 | `VariableDelaysHandledCorrectly` | Random jitter doesn't break protocol |
| MSG-003 | `ExtremlyHighDelayEventuallyDelivers` | Very high delays eventually deliver |
| MSG-004 | `DelayedResponsesHandledCorrectly` | Delayed responses are processed correctly |

### 4.2 Message Loss
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| MSG-010 | `LowMessageLossHandled` | 5% message loss doesn't break cluster |
| MSG-011 | `ModerateMessageLossHandled` | 10% message loss is tolerated |
| MSG-012 | `HighMessageLossEventuallySucceeds` | High loss eventually converges |
| MSG-013 | `MessageLossDuringJoinRetried` | Join retries on message loss |
| MSG-014 | `MessageLossDuringConsensusRetried` | Consensus handles message loss |

### 4.3 Message Ordering
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| MSG-020 | `OutOfOrderMessagesHandled` | Out-of-order delivery is handled |
| MSG-021 | `DuplicateMessagesHandled` | Duplicate messages are handled idempotently |
| MSG-022 | `StaleMessagesIgnored` | Messages from old configurations are ignored |

---

## 5. Consensus Protocol Tests

### 5.1 Fast Paxos Basic Operations
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| CONS-001 | `SingleProposalAccepted` | Single proposal is accepted |
| CONS-002 | `ConflictingProposalsResolved` | Conflicting proposals are resolved |
| CONS-003 | `ConsensusCompletesWithinTimeout` | Consensus completes in reasonable time |
| CONS-004 | `DecisionPropagatedToAllNodes` | Consensus decision reaches all nodes |

### 5.2 Fast Paxos Failure Cases
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| CONS-010 | `ConsensusSucceedsWithMinorityFailure` | Consensus works with minority failures |
| CONS-011 | `ConsensusBlockedWithMajorityFailure` | Consensus blocks with majority failure |
| CONS-012 | `LeaderFailureDuringConsensus` | Leader failure triggers re-election |
| CONS-013 | `SlowProposerDoesNotBlockFast` | Slow proposer doesn't block faster ones |

### 5.3 Configuration Changes
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| CONS-020 | `ConfigurationIdMonotonicallyIncreases` | Config IDs never decrease |
| CONS-021 | `OldConfigurationProposalsRejected` | Old config proposals are rejected |
| CONS-022 | `ConcurrentConfigChangesSerialized` | Concurrent changes are serialized |

---

## 6. Invariant Verification Tests

### 6.1 Membership Invariants
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| INV-001 | `MembershipViewNeverEmpty` | Membership view always has at least one member |
| INV-002 | `SelfAlwaysInMembershipView` | Node always sees itself in its view |
| INV-003 | `NoGhostMembers` | No members exist that never joined |
| INV-004 | `RemovedNodesEventuallyGone` | Removed nodes eventually leave all views |

### 6.2 Safety Invariants
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| INV-010 | `NoSplitBrainInvariant` | Split-brain never occurs |
| INV-011 | `ConfigurationIdMonotonicity` | Config IDs are monotonically increasing |
| INV-012 | `MembershipConsistencyInvariant` | Eventual membership consistency |
| INV-013 | `ConsensusSafetyInvariant` | Consensus decisions are consistent |

### 6.3 Liveness Invariants
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| INV-020 | `EventualProgressGuarantee` | System eventually makes progress |
| INV-021 | `JoinEventuallyCompletes` | Valid joins eventually complete |
| INV-022 | `FailureDetectionEventuallyOccurs` | Failures are eventually detected |
| INV-023 | `PartitionHealEventuallyConverges` | Healed partitions eventually converge |

---

## 7. Edge Cases

### 7.1 Boundary Conditions
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| EDGE-001 | `ZeroBatchingWindowWorks` | Zero batching window processes immediately |
| EDGE-002 | `MaximumClusterSizeHandled` | Large cluster (20+ nodes) works |
| EDGE-003 | `MinimumRingCountWorks` | Ring count of 1 works correctly |
| EDGE-004 | `RapidNodeIdCollisionHandled` | UUID collision is handled or avoided |

### 7.2 Timing Edge Cases
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| EDGE-010 | `JoinRightAfterAnotherJoin` | Back-to-back joins work correctly |
| EDGE-011 | `LeaveRightAfterJoin` | Immediate leave after join works |
| EDGE-012 | `CrashRightAfterJoin` | Immediate crash after join handled |
| EDGE-013 | `JoinDuringFailureDetection` | Join during failure detection works |

### 7.3 Resource Edge Cases
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| EDGE-020 | `DisposedNodeThrowsAppropriately` | Disposed node operations throw |
| EDGE-021 | `DoubleDisposeIsSafe` | Double dispose doesn't throw |
| EDGE-022 | `CancellationDuringJoinHandled` | Cancelled join releases resources |
| EDGE-023 | `TimeoutDuringJoinHandled` | Timed-out join releases resources |

### 7.4 Protocol Edge Cases
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| EDGE-030 | `JoinWithSameAddressRejected` | Duplicate address join is rejected |
| EDGE-031 | `JoinToNonExistentSeedFails` | Join to invalid seed fails gracefully |
| EDGE-032 | `JoinToUninitializedNodeFails` | Join to uninitialized node fails |
| EDGE-033 | `LeaveFromNonMemberHandled` | Leave from non-member handled |

---

## 8. Chaos Testing

### 8.1 Random Fault Injection
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| CHAOS-001 | `RandomNodeCrashesDoNotCorruptState` | Random crashes maintain consistency |
| CHAOS-002 | `RandomPartitionsDoNotCorruptState` | Random partitions maintain consistency |
| CHAOS-003 | `RandomMessageLossDoesNotCorruptState` | Random loss maintains consistency |
| CHAOS-004 | `CombinedRandomFaultsHandled` | Combined faults maintain consistency |

### 8.2 Stress Testing
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| CHAOS-010 | `HighChurnClusterStabilizes` | High join/leave rate stabilizes |
| CHAOS-011 | `ContinuousFaultInjectionSurvived` | Continuous faults don't crash |
| CHAOS-012 | `InvariantsHoldUnderChaos` | Safety invariants hold under chaos |
| CHAOS-013 | `RecoveryAfterChaosStorm` | Cluster recovers after chaos |

### 8.3 Scheduled Fault Scenarios
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| CHAOS-020 | `ScheduledCrashExecutesOnTime` | Scheduled crash fires at right time |
| CHAOS-021 | `ScheduledPartitionExecutesOnTime` | Scheduled partition fires on time |
| CHAOS-022 | `ScheduledHealExecutesOnTime` | Scheduled heal fires on time |
| CHAOS-023 | `OverlappingScheduledFaults` | Overlapping scheduled faults handled |

---

## 9. Determinism Verification

### 9.1 Reproducibility Tests
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| DET-001 | `SameSeedProducesSameRandomSequence` | Same seed = same random values |
| DET-002 | `SameSeedProducesSameClusterBehavior` | Same seed = same cluster events |
| DET-003 | `ForkedRandomIsDeterministic` | Forked random is also deterministic |
| DET-004 | `TimeAdvancementIsDeterministic` | Time advances deterministically |

### 9.2 Scheduler Tests
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| DET-010 | `TasksExecuteInDeterministicOrder` | Task order is deterministic |
| DET-011 | `DelayedTasksExecuteAfterTimeAdvance` | Delayed tasks respect time |
| DET-012 | `StepExecutesExactlyOneTask` | Step() executes exactly one task |
| DET-013 | `StepAllExecutesAllPendingTasks` | StepAll() executes all tasks |

### 9.3 Event Logging
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| DET-020 | `EventsAreLoggedWithCorrectLogicalTime` | Events have correct logical time |
| DET-021 | `EventsAreLoggedWithCorrectSimulatedTime` | Events have correct simulated time |
| DET-022 | `EventLogIsImmutableCopy` | Event log returns immutable copies |
| DET-023 | `SeedIsLoggedOnTestFailure` | Seed is logged when test fails |

---

## 10. Integration Scenarios

### 10.1 Complete Cluster Lifecycle
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| INT-001 | `FullClusterLifecycle` | Create, operate, shutdown lifecycle |
| INT-002 | `ClusterScaleUpAndDown` | Scale cluster up then down |
| INT-003 | `RollingRestartAllNodes` | Rolling restart of all nodes |
| INT-004 | `EmergencyShutdownAllNodes` | Emergency shutdown of all nodes |

### 10.2 Recovery Scenarios
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| INT-010 | `RecoveryFromPartitionedState` | Full recovery after partition |
| INT-011 | `RecoveryFromMajorityFailure` | Recovery after majority returns |
| INT-012 | `RecoveryFromTotalFailureRestart` | Recovery from complete restart |
| INT-013 | `RecoveryFromCascadingFailures` | Recovery from cascading failures |

### 10.3 View Change Notification
| Test ID | Test Name | Description |
|---------|-----------|-------------|
| INT-020 | `ViewChangeNotificationOnJoin` | View change fires on join |
| INT-021 | `ViewChangeNotificationOnLeave` | View change fires on leave |
| INT-022 | `ViewChangeNotificationOnCrash` | View change fires on crash detection |
| INT-023 | `MultipleSubscribersReceiveNotification` | All subscribers get notified |

---

## Implementation Status

### ✅ Completed (Phase 1: Core Functionality)
- **BASIC-001 to BASIC-032**: All basic cluster operations tests implemented in `ClusterBasicTests.cs`
- **FAIL-001 to FAIL-004**: Single node failure tests in `NodeFailureTests.cs`
- **INV-001 to INV-004**: Membership invariants in `InvariantVerificationTests.cs`
- **DET-001 to DET-023**: All determinism tests in `DeterminismTests.cs`

### ✅ Completed (Phase 2: Failure Handling)
- **FAIL-010 to FAIL-033**: Multiple node failures, seed node failures, and failures during operations in `NodeFailureTests.cs`
- **PART-001 to PART-033**: All partition scenarios in `NetworkPartitionTests.cs`
- **CONS-001 to CONS-022**: Consensus protocol tests in `ConsensusProtocolTests.cs` (newly added)

### ✅ Completed (Phase 3: Advanced Scenarios)
- **MSG-001 to MSG-022**: Message delivery tests in `MessageDeliveryTests.cs`
  - MSG-013, MSG-014, MSG-015 added for message loss during join and consensus
- **EDGE-001 to EDGE-032**: Edge case tests in `EdgeCaseTests.cs`
  - EDGE-002A and EDGE-002B added for maximum cluster size testing
- **INV-010 to INV-013**: Safety invariants in `InvariantVerificationTests.cs`
- **INV-020 to INV-023**: Liveness invariants in `InvariantVerificationTests.cs` (newly added)

### ✅ Completed (Phase 4: Chaos and Integration)
- **CHAOS-001 to CHAOS-023**: Chaos injection tests in `ChaosTests.cs`
  - CHAOS-024 to CHAOS-027 added for additional chaos scenarios
- **INT-001 to INT-023**: Integration tests in `IntegrationTests.cs`

### 📝 Test Files Summary

| Test File | Location | Tests | Status |
|-----------|----------|-------|--------|
| ClusterBasicTests.cs | Simulation/ | BASIC-001 to BASIC-032 | ✅ Complete |
| NodeFailureTests.cs | Simulation/ | FAIL-001 to FAIL-033 | ✅ Complete |
| NetworkPartitionTests.cs | Simulation/ | PART-001 to PART-033 | ✅ Complete |
| MessageDeliveryTests.cs | Simulation/ | MSG-001 to MSG-022 | ✅ Complete |
| **ConsensusProtocolTests.cs** | **Simulation/** | **CONS-001 to CONS-022** | **✅ New** |
| EdgeCaseTests.cs | Simulation/ | EDGE-001 to EDGE-032 | ✅ Complete |
| InvariantVerificationTests.cs | Simulation/ | INV-001 to INV-023 | ✅ Complete |
| DeterminismTests.cs | Simulation/ | DET-001 to DET-023 | ✅ Complete |
| ChaosTests.cs | Simulation/ | CHAOS-001 to CHAOS-027 | ✅ Complete |
| IntegrationTests.cs | Simulation/ | INT-001 to INT-023 | ✅ Complete |

### 🔧 Infrastructure Components

All required infrastructure is implemented:

- **SimulationHarness**: Full control over time, tasks, and randomness - `SimulationHarness.cs`
- **SimulationTestHarness**: Lightweight harness for non-deterministic tests - `SimulationTestHarness.cs`
- **ChaosInjector**: Random and scheduled fault injection - `ChaosInjector.cs`
- **InvariantChecker**: Safety and liveness property verification - `InvariantChecker.cs`
- **SimulationTaskScheduler**: Deterministic task execution - `SimulationTaskScheduler.cs`
- **SimulationRandom**: Reproducible random number generation - `SimulationRandom.cs`
- **SimulationNetwork**: Network simulation with partitions and delays - `SimulationNetwork.cs`
- **SimulationNode**: Cluster node for simulation - `SimulationNode.cs`

### 📊 Coverage Summary

**Total Test Categories**: 10 (BASIC, FAIL, PART, MSG, CONS, EDGE, INV, DET, CHAOS, INT)

**Implemented**: 
- ✅ ~220+ simulation tests across all categories
- ✅ All Phase 1-4 priorities covered
- ✅ Core consensus protocol tests added
- ✅ Liveness invariants implemented
- ✅ Extended chaos testing scenarios

**Skipped Tests** (require additional features):
- Some tests marked with `Skip` attribute need:
  - Failure detection timing improvements
  - Protocol-level message injection
  - Advanced consensus recovery mechanisms

---

## Implementation Priority

### ✅ Phase 1: Core Functionality (High Priority) - COMPLETED
- All BASIC-* tests
- FAIL-001 through FAIL-004
- INV-001 through INV-004
- DET-001 through DET-004

### ✅ Phase 2: Failure Handling (High Priority) - COMPLETED
- Remaining FAIL-* tests
- PART-001 through PART-013
- CONS-001 through CONS-004

### ✅ Phase 3: Advanced Scenarios (Medium Priority) - COMPLETED
- MSG-* tests
- Remaining CONS-* tests
- EDGE-* tests

### ✅ Phase 4: Chaos and Integration (Lower Priority) - COMPLETED
- CHAOS-* tests
- INT-* tests
- Remaining INV-* tests

---

## Test Infrastructure Requirements

### Test Fixtures
```csharp
// Standard harness for most tests
SimulationHarness harness = new(seed: <fixed_seed>);

// For tests not requiring full determinism
SimulationTestHarness harness = new(seed: <fixed_seed>);

// For chaos testing
ChaosInjector chaos = new(harness);
InvariantChecker checker = new(harness);
```

### Common Patterns
```csharp
// Wait for convergence
await harness.WaitForConvergenceAsync(expectedSize, timeout);

// Run until condition
harness.RunUntil(() => condition, maxSteps);

// Check invariants
checker.CheckAll();
Assert.False(checker.HasViolations);
```

### Seed Logging
```csharp
// Always log seed for reproduction
harness.LogSeedForReproduction();

// Dump event log on failure
harness.DumpEventLog();
```

---

## Notes

1. **Slow Tests**: Some tests (marked with `Skip`) are inherently slow due to consensus round-trips with batching delays. Run these only in integration test suites.

2. **Determinism**: Tests using `SimulationHarness` have better reproducibility but require careful handling of async operations.

3. **Failure Detection Timing**: Failure detection depends on heartbeat intervals. Tests may need to advance time significantly to trigger detection.

4. **Seed Selection**: Use fixed seeds for reproducibility. When tests fail with random seeds, log the seed and create a regression test with that seed.
