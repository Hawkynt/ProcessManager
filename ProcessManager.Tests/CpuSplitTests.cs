using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// User time and kernel time, per core and for the machine (PRD §46).
/// </summary>
/// <remarks>
/// The split that tells "my program is slow" from "the machine is in the kernel", which a single
/// utilisation figure cannot.
/// </remarks>
[TestFixture]
public sealed class CpuSplitTests {

  /// <summary>A second's worth of time, in nanoseconds, distributed as the test says.</summary>
  private static CpuTimes After(CpuTimes before, ulong user = 0, ulong kernel = 0, ulong irq = 0,
    ulong softIrq = 0, ulong idle = 0, ulong steal = 0, ulong nice = 0) {
    before.UserNs += user;
    before.NiceNs += nice;
    before.KernelNs += kernel;
    before.IrqNs += irq;
    before.SoftIrqNs += softIrq;
    before.IdleNs += idle;
    before.StealNs += steal;
    return before;
  }

  private const ulong _Tenth = 100_000_000;

  [Test]
  public void ACoreSplitsIntoUserAndKernel() {
    var before = default(CpuTimes);
    var after = After(before, user: 3 * _Tenth, kernel: 2 * _Tenth, idle: 5 * _Tenth);

    Assert.That(RateCalculator.UserPercent(before, after).Value, Is.EqualTo(30).Within(0.01));
    Assert.That(RateCalculator.KernelPercent(before, after).Value, Is.EqualTo(20).Within(0.01));
    Assert.That(RateCalculator.BusyPercent(before, after).Value, Is.EqualTo(50).Within(0.01));
  }

  /// <summary>
  /// Interrupt handling is the kernel running on this core. A saturated network adapter is mostly
  /// soft IRQ, and counting only system time would show a nearly idle kernel beside a busy core.
  /// </summary>
  [Test]
  public void InterruptTimeCountsAsKernelTime() {
    var before = default(CpuTimes);
    var after = After(before, irq: _Tenth, softIrq: 3 * _Tenth, idle: 6 * _Tenth);

    Assert.That(RateCalculator.KernelPercent(before, after).Value, Is.EqualTo(40).Within(0.01));
    Assert.That(RateCalculator.UserPercent(before, after).Value, Is.Zero);
  }

  [Test]
  public void NiceTimeCountsAsUserTime() {
    var before = default(CpuTimes);
    var after = After(before, user: _Tenth, nice: 2 * _Tenth, idle: 7 * _Tenth);

    Assert.That(RateCalculator.UserPercent(before, after).Value, Is.EqualTo(30).Within(0.01));
  }

  /// <summary>
  /// User and kernel deliberately do not add up to busy. Steal time is busy from this machine's
  /// point of view and belongs to neither, so a guest losing a third of its core to the hypervisor
  /// shows it as the gap rather than hidden inside one of the two.
  /// </summary>
  [Test]
  public void StealTimeIsBusyAndIsNeitherUserNorKernel() {
    var before = default(CpuTimes);
    var after = After(before, user: 2 * _Tenth, kernel: _Tenth, steal: 3 * _Tenth, idle: 4 * _Tenth);

    Assert.That(RateCalculator.UserPercent(before, after).Value, Is.EqualTo(20).Within(0.01));
    Assert.That(RateCalculator.KernelPercent(before, after).Value, Is.EqualTo(10).Within(0.01));
    Assert.That(RateCalculator.BusyPercent(before, after).Value, Is.EqualTo(60).Within(0.01));
  }

  /// <summary>
  /// I/O wait is not busy — nothing is running during it — and it is neither half of the split
  /// either.
  /// </summary>
  [Test]
  public void IoWaitIsNotBusyAndIsNeitherHalf() {
    var before = default(CpuTimes);
    var after = before;
    after.IoWaitNs += 5 * _Tenth;
    after.IdleNs += 5 * _Tenth;

    Assert.That(RateCalculator.BusyPercent(before, after).Value, Is.Zero);
    Assert.That(RateCalculator.KernelPercent(before, after).Value, Is.Zero);
    Assert.That(RateCalculator.UserPercent(before, after).Value, Is.Zero);
  }

  /// <summary>
  /// A counter that ran backwards means the core was hot-unplugged or the machine was suspended.
  /// The honest answer to that is that nothing can be computed — never a zero, which reads as idle.
  /// </summary>
  [Test]
  public void ACounterThatWentBackwardsIsNotAZero() {
    var before = default(CpuTimes);
    before.UserNs = 5 * _Tenth;
    before.IdleNs = 5 * _Tenth;

    var after = default(CpuTimes);
    after.IdleNs = _Tenth;

    Assert.That(RateCalculator.UserPercent(before, after).HasValue, Is.False);
    Assert.That(RateCalculator.UserPercent(before, after).Reason, Is.EqualTo(UnknownReason.CounterInvalid));
    Assert.That(RateCalculator.KernelPercent(before, after).HasValue, Is.False);
  }

  [Test]
  public void NoTimePassingIsNotAZeroEither() {
    var before = default(CpuTimes);
    before.UserNs = _Tenth;

    Assert.That(RateCalculator.KernelPercent(before, before).HasValue, Is.False);
    Assert.That(RateCalculator.UserPercent(before, before).HasValue, Is.False);
  }

  #region through the delta and onto the page

  private static (SystemSnapshot Before, SystemSnapshot After) Machine(int cores) {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    before.PrepareProcesses(0);
    var first = before.PrepareCores(cores);
    for (var i = 0; i < cores; ++i)
      first[i] = default;

    var after = new SystemSnapshot { TimestampTicks = System.Diagnostics.Stopwatch.Frequency };
    after.PrepareProcesses(0);
    var second = after.PrepareCores(cores);
    for (var i = 0; i < cores; ++i)
      // Core i spends i tenths in the kernel and one tenth in user code; the rest is idle.
      second[i] = After(default, user: _Tenth, kernel: (ulong)i * _Tenth, idle: (ulong)(9 - i) * _Tenth);

    after.System.Cpu = After(default, user: 2 * _Tenth, kernel: _Tenth, idle: 7 * _Tenth);
    return (before, after);
  }

  [Test]
  public void EveryCoreCarriesItsOwnSplit() {
    var (before, after) = Machine(4);
    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);

    Assert.That(delta.PerCoreCount, Is.EqualTo(4));
    for (var core = 0; core < 4; ++core) {
      Assert.That(delta.PerCoreKernelPercent(core).Value, Is.EqualTo(core * 10).Within(0.01), $"core {core}");
      Assert.That(delta.PerCoreUserPercent(core).Value, Is.EqualTo(10).Within(0.01), $"core {core}");
    }
  }

  [Test]
  public void TheMachineCarriesItToo() {
    var (before, after) = Machine(2);
    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);

    Assert.That(delta.SystemUserPercent.Value, Is.EqualTo(20).Within(0.01));
    Assert.That(delta.SystemKernelPercent.Value, Is.EqualTo(10).Within(0.01));
  }

  /// <summary>The first sample has nothing to compare against, and says so rather than reading zero.</summary>
  [Test]
  public void TheFirstSampleHasNoSplitAtAll() {
    var (_, after) = Machine(2);
    var delta = new SnapshotDelta();
    delta.Update(null, after, CpuPercentMode.Normalized);

    Assert.That(delta.SystemKernelPercent.HasValue, Is.False);
    Assert.That(delta.SystemKernelPercent.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(delta.PerCoreKernelPercent(0).HasValue, Is.False);
  }

  [Test]
  public void EveryCoreGetsItsOwnSectionOnThePage() {
    var (before, after) = Machine(3);
    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);

    var titles = new List<string>();
    PerformanceSection? core1 = null;
    foreach (var section in PerformanceReport.Build(new(), after, delta)) {
      titles.Add(section.Title);
      if (section.Title == "Core 1")
        core1 = section;
    }

    Assert.That(titles, Does.Contain("Core 0"));
    Assert.That(titles, Does.Contain("Core 2"));
    Assert.That(core1, Is.Not.Null);
    Assert.That(core1!.Value.Secondary.Value, Is.EqualTo(10).Within(0.01), "the kernel series comes with it");
    Assert.That(core1.Value.SecondaryLabel, Is.EqualTo("kernel"));
    Assert.That(core1.Value.HasSecondary, Is.True);
  }

  [Test]
  public void TheProcessorSectionCarriesTheSplitAsRowsAndAsASeries() {
    var (before, after) = Machine(2);
    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);

    foreach (var section in PerformanceReport.Build(new(), after, delta)) {
      if (section.Title != "Processor")
        continue;

      Assert.That(section.HasSecondary, Is.True);
      Assert.That(section.Secondary.Value, Is.EqualTo(10).Within(0.01));

      var labels = new List<string>();
      foreach (var row in section.Rows)
        labels.Add(row.Label);

      Assert.That(labels, Does.Contain("User time"));
      Assert.That(labels, Does.Contain("Kernel time"));
      return;
    }

    Assert.Fail("no Processor section");
  }

  /// <summary>Everything that is not a processor has one series, and must not claim otherwise.</summary>
  [Test]
  public void NothingElseClaimsASecondSeries() {
    var (before, after) = Machine(2);
    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);

    foreach (var section in PerformanceReport.Build(new(), after, delta)) {
      if (section.Title == "Processor" || section.Title.StartsWith("Core ", StringComparison.Ordinal))
        continue;

      Assert.That(section.HasSecondary, Is.False, section.Title);
    }
  }

  #endregion

}
