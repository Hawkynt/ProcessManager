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
