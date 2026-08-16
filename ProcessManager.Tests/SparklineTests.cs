using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The eighth-block ramp the terminal draws its in-row history with (PRD §11).
/// </summary>
[TestFixture]
public sealed class BlockSparklineTests {

  private static HistoryRing<Rate> Ring(params double?[] values) {
    var ring = new HistoryRing<Rate>(64);
    foreach (var value in values)
      ring.Add(value is { } number ? Rate.Of(number) : Rate.Gap);

    return ring;
  }

  [Test]
  public void AFullReadingIsAFullBlockAndNothingIsASpace() {
    var text = BlockSparkline.Render(2, Ring(0, 100), 100, unicode: true);
    Assert.That(text, Is.EqualTo(" █"));
  }

  [Test]
  public void ASmallButNonZeroReadingStillGetsAMark() {
    // A process using half a percent must not look identical to one using none — that is the whole
    // difference a sparkline exists to show.
    var text = BlockSparkline.Render(1, Ring(0.5), 100, unicode: true);
    Assert.That(text, Is.EqualTo("▁"));
  }

  [Test]
  public void AGapIsASpaceRatherThanAZero() {
    var text = BlockSparkline.Render(3, Ring(100, null, 100), 100, unicode: true);
    Assert.That(text, Is.EqualTo("█ █"));
  }

  [Test]
  public void TheNewestSampleIsAtTheRight() {
    // The same direction as every other plot in the program, so "now" is always in one place.
    var text = BlockSparkline.Render(4, Ring(100, 0), 100, unicode: true);
    Assert.That(text, Is.EqualTo("  █ "), "two samples, right-aligned, newest last");
  }

  [Test]
  public void MoreSamplesThanColumnsKeepsTheNewest() {
    var text = BlockSparkline.Render(2, Ring(100, 100, 0, 0), 100, unicode: true);
    Assert.That(text, Is.EqualTo("  "), "the two newest were both zero");
  }

  [Test]
  public void AValueAboveTheScaleIsClampedRatherThanOverflowing() {
    var text = BlockSparkline.Render(1, Ring(400), 100, unicode: true);
    Assert.That(text, Is.EqualTo("█"));
  }

  [Test]
  public void TheAsciiRampIsUsedWhenTheTerminalCannotDoBlocks() {
    var text = BlockSparkline.Render(2, Ring(0, 100), 100, unicode: false);
    Assert.That(text, Is.EqualTo(" #"));
    Assert.That(text, Does.Not.Contain("█"));
  }

  [Test]
  public void NoHistoryIsBlankRatherThanAThrow() {
    Assert.That(BlockSparkline.Render(5, null, 100, unicode: true), Is.EqualTo("     "));
    Assert.That(BlockSparkline.Render(0, null, 100, unicode: true), Is.Empty);
  }

  [Test]
  public void AScaleOfZeroDoesNotDivide() {
    Assert.That(() => BlockSparkline.Render(4, Ring(1, 2), 0, unicode: true), Throws.Nothing);
  }

}

/// <summary>
/// The per-row history rings and the scales they share (PRD §3.3, §7.2).
/// </summary>
[TestFixture]
public sealed class ProcessHistoryTests {

  private static (SystemSnapshot Snapshot, SnapshotDelta Delta, ProcessView View) Build(
    params (int Pid, ulong PrivateBytes)[] processes
  ) {
    var snapshot = new SystemSnapshot { TimestampTicks = 0 };
    snapshot.System.CoreCount = 1;
    snapshot.System.TotalMemoryBytes = Counter.Of(16UL * 1024 * 1024 * 1024);
    var buffer = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      buffer[i] = default;
      buffer[i].Key = new(processes[i].Pid, 1000ul);
      buffer[i].Name = $"p{processes[i].Pid}";
      buffer[i].CpuTimeNs = Counter.Of(0ul);
      buffer[i].PrivateBytes = Counter.Of(processes[i].PrivateBytes);
    }

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var view = new ProcessView { SortColumn = ProcessColumn.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);
    return (snapshot, delta, view);
  }

  [Test]
  public void OnlyTheRowsOnScreenAreTracked() {
    // PRD §3.3: history for the processes somebody is looking at, not for all thousand of them.
    var (snapshot, delta, view) = Build((1, 1024), (2, 2048), (3, 4096), (4, 8192));
    var history = new ProcessHistory();

    history.Update(snapshot, delta, view, first: 0, count: 2);

    Assert.That(history.Count, Is.EqualTo(2));
    Assert.That(history.Get(new(1, 1000), HistorySeries.Cpu), Is.Not.Null);
    Assert.That(history.Get(new(4, 1000), HistorySeries.Cpu), Is.Null, "off screen, so not tracked");
  }

  [Test]
  public void ARowThatLeavesTheScreenIsForgottenAfterAGrace() {
    var (snapshot, delta, view) = Build((1, 1024), (2, 2048));
    var history = new ProcessHistory();
    history.Update(snapshot, delta, view, 0, 2);
    Assert.That(history.Count, Is.EqualTo(2));

    // Scrolling one row away and back should not throw the history away, so it takes several
    // samples of absence before an entry goes.
    for (var i = 0; i < 3; ++i)
      history.Update(snapshot, delta, view, 0, 1);

    Assert.That(history.Count, Is.EqualTo(2), "still inside the grace period");

    for (var i = 0; i < 4; ++i)
      history.Update(snapshot, delta, view, 0, 1);

    Assert.That(history.Count, Is.EqualTo(1), "and now it is gone");
  }

  [Test]
  public void TheMemoryScaleFollowsTheLargestRowRatherThanTheMachine() {
    // Scaled to the machine, every ordinary process is one pixel tall and the column says nothing.
    var (snapshot, delta, view) = Build((1, 100UL * 1024 * 1024), (2, 400UL * 1024 * 1024));
    var history = new ProcessHistory();
    history.Update(snapshot, delta, view, 0, 2);

    Assert.That(history.MemoryScale, Is.EqualTo(400d * 1024 * 1024).Within(1));
  }

  [Test]
  public void TheScaleHasAFloorSoAnIdleMachineIsNotAmplified() {
    var (snapshot, delta, view) = Build((1, 1024));
    var history = new ProcessHistory();
    history.Update(snapshot, delta, view, 0, 1);

    Assert.That(history.MemoryScale, Is.GreaterThanOrEqualTo(32d * 1024 * 1024));
    Assert.That(history.CpuScale, Is.GreaterThanOrEqualTo(5));
    Assert.That(history.IoScale, Is.GreaterThanOrEqualTo(64 * 1024));
  }

  [Test]
  public void TheScaleDecaysRatherThanSnappingBack() {
    // A scale that dropped the moment a spike ended would make every other row jump.
    var (busy, busyDelta, busyView) = Build((1, 4096UL * 1024 * 1024));
    var history = new ProcessHistory();
    history.Update(busy, busyDelta, busyView, 0, 1);
    var peak = history.MemoryScale;

    var (quiet, quietDelta, quietView) = Build((1, 1024));
    history.Update(quiet, quietDelta, quietView, 0, 1);

    Assert.That(history.MemoryScale, Is.LessThan(peak));
    Assert.That(history.MemoryScale, Is.GreaterThan(peak * 0.5), "decayed, not reset");
  }

}
