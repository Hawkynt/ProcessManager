using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class SystemReplayHistoryTests {

  [Test]
  public void AtOrBeforeReturnsTheLastFrameThatAlreadyExisted() {
    var history = new SystemReplayHistory();
    var snapshot = Snapshot(42, "first");
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var start = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
    history.Add(snapshot, delta, start.Ticks);

    snapshot.Processes[0].Name = "second";
    history.Add(snapshot, delta, start.AddSeconds(2).Ticks);

    var frame = history.AtOrBefore(start.AddSeconds(1));

    Assert.That(frame, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(frame!.TimestampUtc, Is.EqualTo(start));
      Assert.That(frame.Processes, Has.Count.EqualTo(1));
      Assert.That(frame.Processes[0].Record.Name, Is.EqualTo("first"));
    });
  }

  [Test]
  public void HistoryIsBoundedAcrossHoursOfOneSecondSamples() {
    var history = new SystemReplayHistory();
    var snapshot = new SystemSnapshot();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var start = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);

    for (var second = 0; second <= 5 * 60 * 60; ++second)
      history.Add(snapshot, delta, start.AddSeconds(second).Ticks);

    Assert.Multiple(() => {
      Assert.That(history.Count, Is.LessThan(1_200));
      Assert.That(history.NewestUtc, Is.EqualTo(start.AddHours(5)));
      Assert.That(history.OldestUtc, Is.GreaterThanOrEqualTo(start.AddHours(1)));
    });
  }

  [Test]
  public void RequestBeforeRetentionClampsToOldestFrame() {
    var history = new SystemReplayHistory();
    var snapshot = new SystemSnapshot();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    history.Add(snapshot, delta, now.Ticks);

    Assert.That(history.AtOrBefore(now.AddDays(-1))?.TimestampUtc, Is.EqualTo(now));
  }

  private static SystemSnapshot Snapshot(int pid, string name) {
    var snapshot = new SystemSnapshot { Source = "test" };
    ref var process = ref snapshot.AppendProcess();
    process.Key = new ProcessKey(pid, 1);
    process.Name = name;
    return snapshot;
  }

}
