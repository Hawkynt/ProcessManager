using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The arithmetic every displayed number goes through, and the cases that break it (PRD §9.3).
/// </summary>
[TestFixture]
public sealed class CounterTests {

  [Test]
  public void ADifferenceIsTheDifference() {
    Assert.That(Counter.Of(150ul).Since(Counter.Of(100ul)).Value, Is.EqualTo(50ul));
  }

  [Test]
  public void ACounterThatWentBackwardsIsInvalidRatherThanNegative() {
    // Wraparound, a reset, or the pid being reused between two samples. Any of them, and the answer
    // is that we do not know — never a huge number from unsigned subtraction.
    var delta = Counter.Of(10ul).Since(Counter.Of(100ul));
    Assert.That(delta.HasValue, Is.False);
    Assert.That(delta.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  [Test]
  public void AnUnknownPredecessorPoisonsTheDifference() {
    var delta = Counter.Of(100ul).Since(Counter.NotPermitted);
    Assert.That(delta.HasValue, Is.False);
    Assert.That(delta.Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  [Test]
  public void AnUnknownCurrentValuePoisonsTheDifference() {
    var delta = Counter.NotPermitted.Since(Counter.Of(100ul));
    Assert.That(delta.Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  [Test]
  public void ANegativeSignedReadingIsRefused() {
    Assert.That(Counter.Of(-1L).Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  [Test]
  public void ReadingAValueThatIsNotThereThrowsRatherThanReturningZero() {
    // The whole point of §3.4: a caller that forgets to check must fail loudly, not quietly show 0.
    Assert.Throws<InvalidOperationException>(() => _ = Counter.NotPermitted.Value);
  }

}

[TestFixture]
public sealed class RateCalculatorTests {

  private const double _OneSecondNs = 1_000_000_000d;

  [Test]
  public void OneCoreFullyBusyIsOneHundredPerCoreAndOneSixteenthNormalized() {
    var previous = Counter.Of(0ul);
    var current = Counter.Of((ulong)_OneSecondNs);

    var perCore = RateCalculator.CpuPercent(previous, current, _OneSecondNs, 16, CpuPercentMode.PerCore);
    var normalized = RateCalculator.CpuPercent(previous, current, _OneSecondNs, 16, CpuPercentMode.Normalized);

    Assert.That(perCore.Value, Is.EqualTo(100).Within(0.001));
    Assert.That(normalized.Value, Is.EqualTo(6.25).Within(0.001));
  }

  [Test]
  public void EightThreadsBusyExceedOneHundredPerCoreAndAreNotClamped() {
    // A process using eight cores reads 800% in htop's convention. Clamping it to 100 would hide
    // exactly the process worth finding.
    var rate = RateCalculator.CpuPercent(
      Counter.Of(0ul), Counter.Of((ulong)(_OneSecondNs * 8)), _OneSecondNs, 16, CpuPercentMode.PerCore
    );

    Assert.That(rate.Value, Is.EqualTo(800).Within(0.001));
  }

  [Test]
  public void AZeroIntervalIsNotADivision() {
    var rate = RateCalculator.CpuPercent(Counter.Of(0ul), Counter.Of(1000ul), 0, 4, CpuPercentMode.PerCore);
    Assert.That(rate.HasValue, Is.False);
    Assert.That(rate.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  [Test]
  public void AClockThatWentBackwardsIsNotAnInterval() {
    // An NTP step or a resume from suspend. ElapsedNanoseconds yields NaN and every rate built on it
    // has to refuse rather than produce an infinity.
    var elapsed = RateCalculator.ElapsedNanoseconds(1000, 500);
    Assert.That(double.IsNaN(elapsed));
    Assert.That(RateCalculator.PerSecond(Counter.Of(0ul), Counter.Of(10ul), elapsed).HasValue, Is.False);
  }

  [Test]
  public void BusyPercentIgnoresIdleAndNeedsNoWallClock() {
    var previous = new CpuTimes { UserNs = 0, KernelNs = 0, IdleNs = 0 };
    var current = new CpuTimes { UserNs = 300, KernelNs = 200, IdleNs = 500 };
    Assert.That(RateCalculator.BusyPercent(previous, current).Value, Is.EqualTo(50).Within(0.001));
  }

  [Test]
  public void ACoreWhoseTotalDidNotMoveHasNoBusyPercent() {
    var times = new CpuTimes { UserNs = 10, IdleNs = 10 };
    Assert.That(RateCalculator.BusyPercent(times, times).HasValue, Is.False);
  }

}

[TestFixture]
public sealed class SnapshotDeltaTests {

  [Test]
  public void TheFirstSampleHasNoRatesAndNothingIsNew() {
    var snapshot = Build(1_000_000, (1, 100, 0));
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.PerCore);

    Assert.That(delta.HasPrevious, Is.False);
    Assert.That(delta.CpuPercent(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    // Everything would flash green on start-up otherwise.
    Assert.That(delta.IsNew(0), Is.False);
  }

  [Test]
  public void APidReusedByAnotherProgramIsAnExitAndAStartRatherThanADelta() {
    // The case the whole identity pair exists for: same pid, different start time, and a CPU counter
    // that went backwards. Matching on the number alone would report a wild negative rate for a
    // process that never ran.
    var before = Build(0, (500, 9_000_000_000, 111));
    var after = Build(Stopwatch.Frequency, (500, 5_000_000, 222));

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.PerCore);

    Assert.That(delta.IsNew(0), Is.True, "the pid now belongs to a different process");
    Assert.That(delta.CpuPercent(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(delta.Exited, Has.Count.EqualTo(1));
    Assert.That(delta.Exited[0].StartTicks, Is.EqualTo(111ul));
  }

  #region how long a process counts as new (PRD §87)

  /// <summary>
  /// The flash used to last exactly one sample, so how long anybody saw it was decided by the
  /// refresh rate: gone before an eye could land on it at a quarter-second tick, and ten seconds
  /// long at ten. It is a duration now, and the duration is what is honoured.
  /// </summary>
  [Test]
  public void ANewProcessStaysNewForTheWholeHighlightAndNoLonger() {
    var delta = new SnapshotDelta { NewHighlightSeconds = 2 };
    var first = Build(0, (1, 0, 1));
    delta.Update(null, first, CpuPercentMode.PerCore);

    // Quarter-second samples: it appears in the second one and must still be new in the fifth.
    var quarter = Stopwatch.Frequency / 4;
    var arrives = Build(quarter, (1, 0, 1), (2, 0, 2));
    delta.Update(first, arrives, CpuPercentMode.PerCore);
    Assert.That(delta.IsNew(1), Is.True, "the sample it arrived in");

    var previous = arrives;
    for (var tick = 2; tick <= 8; ++tick) {
      var next = Build(quarter * tick, (1, 0, 1), (2, 0, 2));
      delta.Update(previous, next, CpuPercentMode.PerCore);
      previous = next;
      Assert.That(delta.IsNew(1), Is.True, $"still inside the two seconds at tick {tick}");
    }

    // Nine quarters is two and a quarter seconds after the first sample, which is two seconds after
    // it arrived: the window has closed.
    var after = Build(quarter * 9, (1, 0, 1), (2, 0, 2));
    delta.Update(previous, after, CpuPercentMode.PerCore);
    Assert.That(delta.IsNew(1), Is.False);
  }

  /// <summary>Nought is the off switch, which is §12's "optionally highlighted" from this end.</summary>
  [Test]
  public void AHighlightOfNothingNeverMarksAnythingNew() {
    var delta = new SnapshotDelta { NewHighlightSeconds = 0 };
    var before = Build(0, (1, 0, 1));
    var after = Build(Stopwatch.Frequency, (1, 0, 1), (2, 0, 2));

    delta.Update(before, after, CpuPercentMode.PerCore);

    Assert.That(delta.IsNew(1), Is.False);
    // The count is not a highlight and is still true: the event log and the notifications are built
    // on it, and switching a colour off must not switch a record off.
    Assert.That(delta.StartedCount, Is.EqualTo(1));
    Assert.That(delta.AppearedThisSample(1), Is.True);
  }

  /// <summary>
  /// "Should this row still be drawn as new" and "did something just happen" are two questions, and
  /// only the second may move a view: following the first would pin a table that scrolls to new
  /// processes onto one row for the whole highlight (PRD §87).
  /// </summary>
  [Test]
  public void ArrivingIsOneSampleEvenWhileTheHighlightLasts() {
    var delta = new SnapshotDelta { NewHighlightSeconds = 10 };
    var first = Build(0, (1, 0, 1));
    var arrives = Build(Stopwatch.Frequency, (1, 0, 1), (2, 0, 2));
    var later = Build(2 * Stopwatch.Frequency, (1, 0, 1), (2, 0, 2));

    delta.Update(null, first, CpuPercentMode.PerCore);
    delta.Update(first, arrives, CpuPercentMode.PerCore);
    Assert.That(delta.AppearedThisSample(1), Is.True);

    delta.Update(arrives, later, CpuPercentMode.PerCore);
    Assert.That(delta.IsNew(1), Is.True, "ten seconds of highlight is not over");
    Assert.That(delta.AppearedThisSample(1), Is.False, "but it arrived a sample ago");
  }

  /// <summary>
  /// The start times are swept rather than accumulated. A machine that forks steadily would
  /// otherwise gain an entry per process for the life of the program.
  /// </summary>
  [Test]
  public void TheHighlightRemembersOnlyWhatIsStillInsideIt() {
    var delta = new SnapshotDelta { NewHighlightSeconds = 1 };
    var previous = Build(0, (1, 0, 1));
    delta.Update(null, previous, CpuPercentMode.PerCore);

    // A different process starts and ends every half second, for fifty of them.
    for (var tick = 1; tick <= 50; ++tick) {
      var next = Build(Stopwatch.Frequency * tick / 2, (1, 0, 1), (100 + tick, 0, (ulong)(100 + tick)));
      delta.Update(previous, next, CpuPercentMode.PerCore);
      previous = next;
    }

    // One second of highlight at two samples a second: what is remembered is the couple still inside
    // their window, not the fifty that have been through.
    Assert.That(delta.RememberedStartsCount, Is.LessThanOrEqualTo(3));
  }

  /// <summary>Nothing is new against no previous sample, whatever the window says.</summary>
  [Test]
  public void AWholeTableIsNotNewOnStartUp() {
    var delta = new SnapshotDelta { NewHighlightSeconds = 30 };
    var first = Build(0, (1, 0, 1), (2, 0, 2));
    var second = Build(Stopwatch.Frequency, (1, 0, 1), (2, 0, 2));

    delta.Update(null, first, CpuPercentMode.PerCore);
    Assert.That(delta.IsNew(0), Is.False);

    delta.Update(first, second, CpuPercentMode.PerCore);
    Assert.That(delta.IsNew(0), Is.False, "a process that was there on the first sample never arrived");
    Assert.That(delta.IsNew(1), Is.False);
  }

  /// <summary>A duration nobody could honour is refused rather than taken.</summary>
  [Test]
  public void AnImpossibleHighlightIsNoHighlight() {
    Assert.That(new SnapshotDelta { NewHighlightSeconds = -1 }.NewHighlightSeconds, Is.Zero);
    Assert.That(new SnapshotDelta { NewHighlightSeconds = double.NaN }.NewHighlightSeconds, Is.Zero);
    Assert.That(new SnapshotDelta { NewHighlightSeconds = double.PositiveInfinity }.NewHighlightSeconds, Is.Zero);
    Assert.That(new SnapshotDelta { NewHighlightSeconds = 1e9 }.NewHighlightSeconds, Is.EqualTo(3600));
  }

  #endregion

  [Test]
  public void AProcessThatVanishedIsReportedExactlyOnce() {
    var before = Build(0, (1, 0, 1), (2, 0, 2));
    var after = Build(Stopwatch.Frequency, (1, 0, 1));

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.PerCore);

    Assert.That(delta.Exited, Has.Count.EqualTo(1));
    Assert.That(delta.Exited[0].Pid, Is.EqualTo(2));
    Assert.That(delta.StartedCount, Is.Zero);
  }

  [Test]
  public void ASurvivingProcessGetsARate() {
    var before = Build(0, (7, 0, 42));
    var after = Build(Stopwatch.Frequency, (7, 1_000_000_000, 42));

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.PerCore);

    Assert.That(delta.IsNew(0), Is.False);
    Assert.That(delta.CpuPercent(0).Value, Is.EqualTo(100).Within(1));
  }

  #region who started it (PRD §14)

  /// <summary>
  /// Builds a table with the parentage the caller names, and resolves the parent names over it.
  /// </summary>
  private static SystemSnapshot Family(params (int Pid, int ParentPid, string Name)[] processes) {
    var snapshot = new SystemSnapshot();
    var buffer = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      buffer[i] = default;
      buffer[i].Key = new(processes[i].Pid, 1000);
      buffer[i].ParentPid = processes[i].ParentPid;
      buffer[i].Name = processes[i].Name;
    }

    snapshot.ResolveParentNames();
    return snapshot;
  }

  private static string? ParentOf(SystemSnapshot snapshot, int pid) {
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process.ParentName;

    Assert.Fail($"no process {pid}");
    return null;
  }

  [Test]
  public void AProcessIsNamedAfterItsParent() {
    var snapshot = Family((1, 0, "systemd"), (100, 1, "bash"), (200, 100, "vim"));

    Assert.That(ParentOf(snapshot, 100), Is.EqualTo("systemd"));
    Assert.That(ParentOf(snapshot, 200), Is.EqualTo("bash"));
  }

  /// <summary>
  /// A parent that is not in the sample has exited and its child has been reparented. That is a fact
  /// about the tree, not a gap in what could be read, and inventing a name for it would be worse
  /// than leaving it empty.
  /// </summary>
  [Test]
  public void AReparentedProcessHasNoParentName() {
    var snapshot = Family((1, 0, "systemd"), (200, 4242, "orphan"));

    Assert.That(ParentOf(snapshot, 200), Is.Null);
    Assert.That(ParentOf(snapshot, 1), Is.Null, "pid 1 has no parent to name");
  }

  /// <summary>
  /// /proc reports pid 1 as its own parent inside a container. Naming it after itself would read as
  /// though it had forked itself.
  /// </summary>
  [Test]
  public void AProcessThatIsItsOwnParentIsNotNamedAfterItself() {
    var snapshot = Family((1, 1, "init"));

    Assert.That(ParentOf(snapshot, 1), Is.Null);
  }

  /// <summary>
  /// The parent's own name instance, not a copy: this runs for every process on every sample, and a
  /// string allocated per row would be several hundred allocations a second against a budget of
  /// none (PRD §4).
  /// </summary>
  [Test]
  public void TheParentNameIsTheParentsOwnInstance() {
    var snapshot = Family((1, 0, "systemd"), (100, 1, "bash"));

    string? parentOwnName = null;
    foreach (var process in snapshot.Processes)
      if (process.Pid == 1)
        parentOwnName = process.Name;

    Assert.That(ParentOf(snapshot, 100), Is.SameAs(parentOwnName));
  }

  /// <summary>Resolving twice over the same table must not change what it says.</summary>
  [Test]
  public void ResolvingAgainSaysTheSameThing() {
    var snapshot = Family((1, 0, "systemd"), (100, 1, "bash"));
    snapshot.ResolveParentNames();

    Assert.That(ParentOf(snapshot, 100), Is.EqualTo("systemd"));
  }

  #endregion

  private static SystemSnapshot Build(long timestampTicks, params (int Pid, ulong CpuNs, ulong StartTicks)[] processes) {
    var snapshot = new SystemSnapshot { TimestampTicks = timestampTicks };
    snapshot.System.CoreCount = 1;
    var buffer = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      buffer[i] = default;
      buffer[i].Key = new(processes[i].Pid, processes[i].StartTicks);
      buffer[i].Name = $"p{processes[i].Pid}";
      buffer[i].CpuTimeNs = Counter.Of(processes[i].CpuNs);
      buffer[i].ReadBytes = Counter.Of(0ul);
      buffer[i].WriteBytes = Counter.Of(0ul);
    }

    return snapshot;
  }

  private static class Stopwatch {
    public static long Frequency => System.Diagnostics.Stopwatch.Frequency;
  }

}

[TestFixture]
public sealed class HistoryRingTests {

  [Test]
  public void ItKeepsTheNewestAndDropsTheOldest() {
    var ring = new HistoryRing<Rate>(3);
    for (var i = 1; i <= 5; ++i)
      ring.Add(Rate.Of(i));

    Assert.That(ring.Count, Is.EqualTo(3));
    Assert.That(ring[0].Value, Is.EqualTo(3));
    Assert.That(ring[2].Value, Is.EqualTo(5));
  }

  [Test]
  public void AGapIsStoredRatherThanSmoothedAway() {
    var ring = new HistoryRing<Rate>(4);
    ring.Add(Rate.Of(10));
    ring.Add(Rate.Gap);
    ring.Add(Rate.Of(20));

    Assert.That(ring[1].HasValue, Is.False);
    Assert.That(ring[2].Value, Is.EqualTo(20));
  }

  [Test]
  public void CopyingTheNewestFillsFromTheEnd() {
    var ring = new HistoryRing<Rate>(10);
    for (var i = 0; i < 8; ++i)
      ring.Add(Rate.Of(i));

    Span<Rate> destination = stackalloc Rate[3];
    var written = ring.CopyNewestTo(destination);

    Assert.That(written, Is.EqualTo(3));
    Assert.That(destination[0].Value, Is.EqualTo(5));
    Assert.That(destination[2].Value, Is.EqualTo(7));
  }

}
