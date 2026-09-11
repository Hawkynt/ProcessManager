using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class SystemPlaybackHistoryTests {

  [Test]
  public void SeekUsesFloorSemanticsAndDoesNotCrossIntoTheFuture() {
    var history = new SystemPlaybackHistory();
    var snapshot = Snapshot(42, "first");
    var delta = Delta(snapshot);
    var start = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Utc);

    history.Add(snapshot, delta, start.Ticks);
    snapshot.ProcessBuffer[0].Name = "second";
    history.Add(snapshot, delta, start.AddSeconds(2).Ticks);

    Assert.That(history.TryAtOrBefore(start.AddSeconds(1), out var frame), Is.True);
    Assert.Multiple(() => {
      Assert.That(frame.TimestampUtc, Is.EqualTo(start));
      Assert.That(frame.Processes.Length, Is.EqualTo(1));
      Assert.That(frame.Processes[0].Name, Is.EqualTo("first"));
    });
  }

  [Test]
  public void RequestBeforeRetentionClampsToTheOldestFrame() {
    var history = new SystemPlaybackHistory();
    var snapshot = Snapshot(7, "oldest");
    var delta = Delta(snapshot);
    var when = new DateTime(2026, 9, 6, 11, 0, 0, DateTimeKind.Utc);
    history.Add(snapshot, delta, when.Ticks);

    Assert.That(history.TryAtOrBefore(when.AddDays(-1), out var frame), Is.True);
    Assert.That(frame.TimestampUtc, Is.EqualTo(when));
  }

  [Test]
  public void HistoricalRowsKeepIdentityAndMemoryAfterTheLiveBufferChanges() {
    var history = new SystemPlaybackHistory();
    var snapshot = Snapshot(23, "compiler", privateBytes: 123_456, workingSetBytes: 98_765);
    var delta = Delta(snapshot);
    var first = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    history.Add(snapshot, delta, first.Ticks);

    ref var live = ref snapshot.ProcessBuffer[0];
    live.Key = new ProcessKey(23, 999);
    live.Name = "reused-pid";
    live.PrivateBytes = Counter.Of(777);
    live.WorkingSetBytes = Counter.NotPermitted;
    history.Add(snapshot, delta, first.AddSeconds(2).Ticks);

    Assert.That(history.TryAtOrBefore(first, out var frame), Is.True);
    var old = frame.Processes[0];
    Assert.Multiple(() => {
      Assert.That(old.Key, Is.EqualTo(new ProcessKey(23, 1)));
      Assert.That(old.Name, Is.EqualTo("compiler"));
      Assert.That(old.PrivateBytes, Is.EqualTo(Counter.Of(123_456)));
      Assert.That(old.WorkingSetBytes, Is.EqualTo(Counter.Of(98_765)));
    });
  }

  [Test]
  public void UnknownReasonsSurvivePlaybackInsteadOfTurningIntoZero() {
    var history = new SystemPlaybackHistory();
    var snapshot = Snapshot(5, "hidden");
    ref var process = ref snapshot.ProcessBuffer[0];
    process.PrivateBytes = Counter.NotPermitted;
    process.WorkingSetBytes = Counter.NotSupported;
    var delta = Delta(snapshot);
    var when = new DateTime(2026, 9, 6, 13, 0, 0, DateTimeKind.Utc);
    history.Add(snapshot, delta, when.Ticks);

    Assert.That(history.TryNewest(out var frame), Is.True);
    var retained = frame.Processes[0];
    Assert.Multiple(() => {
      Assert.That(retained.PrivateBytes.Reason, Is.EqualTo(UnknownReason.NotPermitted));
      Assert.That(retained.WorkingSetBytes.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
      Assert.That(retained.CpuPercent.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    });
  }

  [Test]
  public void RowStorageIsBoundedForAThousandProcessMachine() {
    var history = new SystemPlaybackHistory();
    var snapshot = new SystemSnapshot { Source = "test" };
    for (var pid = 1; pid <= 1000; ++pid) {
      ref var process = ref snapshot.AppendProcess();
      process.Key = new ProcessKey(pid, (ulong)pid);
      process.Name = "worker";
      process.PrivateBytes = Counter.Of((ulong)pid * 4096);
      process.WorkingSetBytes = Counter.Of((ulong)pid * 2048);
    }

    var delta = Delta(snapshot);
    history.Add(snapshot, delta, new DateTime(2026, 9, 6, 14, 0, 0, DateTimeKind.Utc).Ticks);

    Assert.Multiple(() => {
      Assert.That(history.ProcessCapacity, Is.GreaterThanOrEqualTo(1000));
      Assert.That(history.RetainedProcessSlotCapacity, Is.LessThanOrEqualTo(SystemPlaybackHistory.MaxRetainedProcessSlots));
    });
  }

  [Test]
  public void SteadyStateAddsAllocateNoManagedObjects() {
    var history = new SystemPlaybackHistory();
    var snapshot = Snapshot(1, "steady");
    var delta = Delta(snapshot);
    var ticks = new DateTime(2026, 9, 6, 15, 0, 0, DateTimeKind.Utc).Ticks;

    history.Add(snapshot, delta, ticks);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 1; i <= 100; ++i)
      history.Add(snapshot, delta, ticks + (i * TimeSpan.TicksPerSecond));
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    Assert.That(allocated, Is.Zero);
  }

  private static SnapshotDelta Delta(SystemSnapshot snapshot) {
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    return delta;
  }

  private static SystemSnapshot Snapshot(int pid, string name, ulong privateBytes = 0, ulong workingSetBytes = 0) {
    var snapshot = new SystemSnapshot { Source = "test" };
    ref var process = ref snapshot.AppendProcess();
    process.Key = new ProcessKey(pid, 1);
    process.Name = name;
    process.PrivateBytes = Counter.Of(privateBytes);
    process.WorkingSetBytes = Counter.Of(workingSetBytes);
    return snapshot;
  }

}
