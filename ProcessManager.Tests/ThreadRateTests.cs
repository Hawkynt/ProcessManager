using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The per-thread CPU and context-switch rates, which need two readings of the same thread (PRD §29).
/// </summary>
/// <remarks>
/// No file access, so this runs on every CI leg. The interval is the monotonic clock's rather than a
/// number the test supplies, so the assertions are about which readings produce a rate at all and
/// about the ones that must not — not about a percentage the test would have to sleep to earn.
/// </remarks>
[TestFixture]
public sealed class ThreadRateTests {

  private static readonly ProcessKey _Key = new(1001, 100500);

  private static ThreadRecord Thread(int tid, ulong cpuNs, Counter switches, long started = 1) => new(
    tid,
    ProcessState.Running,
    Counter.Of(cpuNs),
    started,
    Counter.NotSupported,
    null,
    20,
    $"t{tid}",
    Counter.Of(cpuNs),
    Counter.Of(0ul),
    switches,
    0,
    null,
    Counter.NotSupported,
    Counter.NotSupported,
    0,
    SchedulingPolicy.Other,
    null,
    null,
    Counter.NotSupported,
    null,
    Counter.NotSupported,
    Counter.NotSupported,
    ThreadMode.Unknown,
    Counter.NotSupported,
    Counter.NotSupported
  );

  /// <summary>
  /// The first reading has no interval to divide by. A rate of zero would say the thread used no
  /// processor, which is a measurement nobody took (PRD §3.4).
  /// </summary>
  [Test]
  public void TheFirstReadingProducesNoRatesAtAll() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Thread(1, 1_000_000, Counter.Of(10ul))], 8);

    Assert.That(delta.HasPrevious, Is.False);
    Assert.That(delta.CpuPercent(0).HasValue, Is.False);
    Assert.That(delta.CpuPercent(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(delta.ContextSwitchesPerSecond(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  [Test]
  public void TheSecondReadingOfTheSameThreadProducesThem() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Thread(1, 1_000_000, Counter.Of(10ul))], 8);
    delta.Update(_Key, [Thread(1, 3_000_000, Counter.Of(40ul))], 8);

    Assert.That(delta.HasPrevious, Is.True);
    Assert.That(delta.CpuPercent(0).HasValue, Is.True);
    Assert.That(delta.CpuPercent(0).Value, Is.GreaterThan(0));
    Assert.That(delta.ContextSwitchesPerSecond(0).HasValue, Is.True);
    // The whole machine and one processor, from the same two readings: the per-core figure is the
    // normalized one multiplied by the core count, and the thread view shows the per-core one because
    // a thread cannot use more than one (PRD §3.2).
    Assert.That(delta.CpuPercentPerCore(0).Value, Is.EqualTo(delta.CpuPercent(0).Value * 8).Within(0.01).Percent);
  }

  /// <summary>
  /// A thread that was not in the previous reading has nothing to subtract. The dictionary lookup
  /// that misses leaves <c>default(Counter)</c> behind — a confident zero whose reason reads as
  /// "value present" — and charging a new thread with it would report a rate off the first sample.
  /// </summary>
  [Test]
  public void AThreadThatAppearedSinceTheLastReadingHasNoRateYet() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Thread(1, 1_000_000, Counter.Of(10ul))], 8);
    delta.Update(_Key, [Thread(1, 2_000_000, Counter.Of(20ul)), Thread(2, 9_000_000, Counter.Of(99ul))], 8);

    Assert.That(delta.CpuPercent(0).HasValue, Is.True);
    Assert.That(delta.CpuPercent(1).HasValue, Is.False, "thread 2 is new");
    Assert.That(delta.CpuPercent(1).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>
  /// Linux reuses thread ids as freely as it reuses process ids. A pool that ends a worker and starts
  /// another gets the same number back, and subtracting across the two would show a thread that has
  /// been busy since before it existed (PRD §8.2).
  /// </summary>
  [Test]
  public void AReusedThreadIdIsNotTheSameThread() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Thread(1, 5_000_000, Counter.Of(50ul), started: 100)], 8);
    delta.Update(_Key, [Thread(1, 0, Counter.Of(0ul), started: 900)], 8);

    Assert.That(delta.CpuPercent(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>
  /// Thread ids are unique inside a process and nowhere else, so a change of selection has to empty
  /// the history — otherwise one program's thread 7 is subtracted from another's.
  /// </summary>
  [Test]
  public void SelectingADifferentProcessForgetsWhatTheLastOneRead() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Thread(7, 5_000_000, Counter.Of(50ul))], 8);
    delta.Update(new(2002, 4242), [Thread(7, 9_000_000, Counter.Of(90ul))], 8);

    Assert.That(delta.HasPrevious, Is.False);
    Assert.That(delta.CpuPercent(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>
  /// A counter that has no value cannot produce a rate that does. A kernel with no schedstats reports
  /// no switch counts, and dividing "nothing" by an interval is still nothing (PRD §3.4).
  /// </summary>
  [Test]
  public void AnUnknownCounterProducesAnUnknownRateAndNotZero() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Thread(1, 1_000_000, Counter.NotSupported)], 8);
    delta.Update(_Key, [Thread(1, 2_000_000, Counter.NotSupported)], 8);

    Assert.That(delta.ContextSwitchesPerSecond(0).HasValue, Is.False);
    Assert.That(delta.ContextSwitchesPerSecond(0).Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(delta.CpuPercent(0).HasValue, Is.True, "the CPU counter was still readable");
  }

  /// <summary>
  /// A long-lived pool churns through ids all day. A history that only ever grew would be a leak with
  /// a slow fuse, so threads that are gone are dropped.
  /// </summary>
  [Test]
  public void ThreadsThatEndedAreForgotten() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Thread(1, 1_000_000, Counter.Of(1ul)), Thread(2, 1_000_000, Counter.Of(1ul))], 8);
    delta.Update(_Key, [Thread(1, 2_000_000, Counter.Of(2ul))], 8);
    // Thread 2 comes back with the same id and start time, which only matters if it was forgotten:
    // a remembered entry would give it a rate off a reading taken two updates ago.
    delta.Update(_Key, [Thread(1, 3_000_000, Counter.Of(3ul)), Thread(2, 1_000_000, Counter.Of(1ul))], 8);

    Assert.That(delta.CpuPercent(1).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  [Test]
  public void AskingAboutARowThatIsNotThereIsNotAnException() {
    var delta = new ThreadDelta();
    delta.Update(_Key, [Thread(1, 1_000_000, Counter.Of(1ul))], 8);

    Assert.That(delta.CpuPercent(9).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(delta.ContextSwitchesPerSecond(-1).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

}
