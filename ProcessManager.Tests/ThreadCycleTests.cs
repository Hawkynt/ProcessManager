using System.Runtime.InteropServices;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Windows;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The three thread readings a handle is needed for: cycles, the ideal processor and the TEB
/// (PRD §29).
/// </summary>
/// <remarks>
/// <para>
/// All three sat as unticked boxes under a heading that said the engine enumerates threads on both
/// platforms, and the reason each was missing was different. Cycles are countable on Linux through
/// <c>perf_event_open</c> and nobody has opened one; an ideal processor is a Windows idea the Linux
/// scheduler has no reading for; and a TEB is a Windows structure with no Linux counterpart at all.
/// Three absences and three different sentences, which is the whole of §72.3 in one row.
/// </para>
/// <para>
/// The Windows path costs a handle per thread, so it runs for the threads of the one process
/// somebody has open and never over the table (§5.4).
/// </para>
/// </remarks>
[TestFixture]
public sealed class ThreadCycleTests {

  private static ThreadRecord Sample(Counter cycles, ulong cpuNs = 1_000_000_000ul) => new(
    Tid: 7,
    State: ProcessState.Running,
    CpuTimeNs: Counter.Of(cpuNs),
    StartTimeUtcTicks: 1,
    StartAddress: Counter.NotSupported,
    StartSymbol: null,
    Priority: 20,
    Name: "worker",
    UserTimeNs: Counter.Of(cpuNs),
    KernelTimeNs: Counter.Of(0ul),
    ContextSwitches: Counter.Of(1ul),
    LastCpu: 0,
    WaitReason: null,
    VoluntaryContextSwitches: Counter.NotSupported,
    InvoluntaryContextSwitches: Counter.NotSupported,
    BasePriority: 0,
    Policy: SchedulingPolicy.Other,
    Affinity: null,
    StartModule: null,
    InstructionPointer: Counter.NotSupported,
    InstructionModule: null,
    StackPointer: Counter.NotSupported,
    StackBytes: Counter.NotSupported,
    Mode: ThreadMode.Unknown,
    SyscallNumber: Counter.NotSupported,
    QueuedNs: Counter.NotSupported,
    Cycles: cycles,
    IdealProcessor: Counter.NotSupported,
    TebBase: Counter.NotSupported
  );

  private static readonly ProcessKey _Key = new(1001, 100500);

  /// <summary>
  /// One reading is no rate. The first sample of anything says how much a thread has done since it
  /// started, and dividing that by a made-up interval is the fabrication §3.4 exists to prevent.
  /// </summary>
  [Test]
  public void OneReadingIsNoCycleRate() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Sample(Counter.Of(1_000ul))], 8);

    Assert.That(delta.CyclesPerSecond(0).HasValue, Is.False);
    Assert.That(delta.CyclesPerSecond(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>Two readings make one, over the interval the monotonic clock actually measured.</summary>
  [Test]
  public void TwoReadingsMakeARate() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Sample(Counter.Of(1_000_000ul))], 8);
    Thread.Sleep(30);
    delta.Update(_Key, [Sample(Counter.Of(3_000_000ul))], 8);

    var rate = delta.CyclesPerSecond(0);
    Assert.That(rate.HasValue, Is.True, "two readings and an interval");

    // Two million cycles over something like a thirtieth of a second. The bound is loose on purpose:
    // this asserts that a difference was divided by a real interval, not that a shared machine
    // scheduled the test promptly.
    Assert.That(rate.Value, Is.GreaterThan(0));
    Assert.That(rate.Value, Is.LessThan(2_000_000_000d));
  }

  /// <summary>
  /// The reason travels. A thread whose cycles nobody counted has a rate nobody counted, carrying the
  /// same reason — not a nought, and not a bare "unknown" that has forgotten why.
  /// </summary>
  [Test]
  public void AnUncountedCycleCounterMakesAnUncountedRate() {
    var delta = new ThreadDelta();
    var unknown = Counter.Unknown(UnknownReason.NotImplementedHere);
    delta.Update(_Key, [Sample(unknown)], 8);
    Thread.Sleep(5);
    delta.Update(_Key, [Sample(unknown)], 8);

    var rate = delta.CyclesPerSecond(0);
    Assert.That(rate.HasValue, Is.False);
    Assert.That(rate.Reason, Is.EqualTo(UnknownReason.NotImplementedHere));
  }

  /// <summary>
  /// An index past the end is unsampled rather than a crash or a zero, as every other rate on this
  /// class already is — the thread list shrinks between a draw and a click.
  /// </summary>
  [Test]
  public void APositionThatIsNotThereIsUnsampled() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Sample(Counter.Of(1ul))], 8);

    Assert.That(delta.CyclesPerSecond(9).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(delta.CyclesPerSecond(-1).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>
  /// The bulk Windows query does not carry any of the three, and says so as "not sampled yet" rather
  /// than as "not supported": the readings exist and this particular call is not the one that takes
  /// them. Replayed from a synthesised buffer, so it runs on every CI leg.
  /// </summary>
  [Test]
  public void TheBulkQueryLeavesTheThreeToTheHandlePass() {
    var entrySize = Marshal.SizeOf<NtStructures.SystemProcessInformation>();
    var threadSize = Marshal.SizeOf<NtStructures.SystemThreadInformation>();
    var buffer = new byte[entrySize + threadSize];
    var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

    try {
      var entry = new NtStructures.SystemProcessInformation {
        NextEntryOffset = 0,
        NumberOfThreads = 1,
        CreateTime = 133_100_000_000_000_000L,
        UniqueProcessId = 1234,
      };

      var thread = new NtStructures.SystemThreadInformation {
        Priority = 8,
        BasePriority = 8,
        ClientId = new() { UniqueProcess = 1234, UniqueThread = 7 },
      };

      MemoryMarshal.Write(buffer.AsSpan(), in entry);
      MemoryMarshal.Write(buffer.AsSpan(entrySize), in thread);

      var threads = SystemProcessInformationReader.ReadThreads(buffer, new(1234, 133_100_000_000_000_000UL));
      Assert.That(threads, Has.Count.EqualTo(1));

      Assert.Multiple(() => {
        Assert.That(threads[0].Cycles.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
        Assert.That(threads[0].IdealProcessor.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
        Assert.That(threads[0].TebBase.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
      });
    } finally {
      handle.Free();
    }
  }

  /// <summary>
  /// And on Windows the pass runs and answers for this program's own threads, which is the one
  /// process a test can be certain it may open a handle on.
  /// </summary>
  /// <remarks>
  /// The assertion is deliberately not "the number is plausible" — a cycle count has no plausible
  /// range and an ideal processor is whatever the scheduler picked. It is that every one of the three
  /// is either a reading or a stated reason, because the failure this guards against is the pass not
  /// running at all and leaving "not sampled yet" behind forever, which reads as a bug in the
  /// sampler rather than as a missing feature.
  /// </remarks>
  [Test]
  [Platform("Win", Reason = "opens a thread handle")]
  [System.Runtime.Versioning.SupportedOSPlatform("windows")]
  public void OnWindowsTheHandlePassAnswersForOurOwnThreads() {
    using var probe = new WindowsProbe(new());
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var us = Environment.ProcessId;
    var key = default(ProcessKey);
    foreach (var process in snapshot.Processes)
      if (process.Pid == us)
        key = process.Key;

    Assert.That(key.Pid, Is.EqualTo(us), "this program is in its own snapshot");

    var threads = probe.GetThreads(key);
    Assert.That(threads, Is.Not.Empty, "a running process has at least one thread");

    Assert.Multiple(() => {
      foreach (var thread in threads) {
        Assert.That(
          thread.Cycles.Reason,
          Is.Not.EqualTo(UnknownReason.NotSampledYet),
          $"thread {thread.Tid}: the cycle pass never ran"
        );

        Assert.That(
          thread.IdealProcessor.Reason,
          Is.Not.EqualTo(UnknownReason.NotSampledYet),
          $"thread {thread.Tid}: the ideal-processor pass never ran"
        );

        Assert.That(
          thread.TebBase.Reason,
          Is.Not.EqualTo(UnknownReason.NotSampledYet),
          $"thread {thread.Tid}: the TEB pass never ran"
        );
      }
    });

    // Our own threads are ours to open, so at least one of them really did produce a cycle count.
    // Without this the test above passes on a machine that refused every handle, which is exactly the
    // outcome it is supposed to distinguish from success.
    var counted = 0;
    foreach (var thread in threads)
      if (thread.Cycles.HasValue)
        ++counted;

    Assert.That(counted, Is.GreaterThan(0), "no thread of our own gave up a cycle count");
  }

}
