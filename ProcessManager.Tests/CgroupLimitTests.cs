using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What a cgroup allows a process (PRD §38).
/// </summary>
/// <remarks>
/// The answer to "why is this slow when the machine is idle". This machine has no capped cgroups on
/// it, so the limits that matter are exercised against a recorded hierarchy — the uncapped case is
/// cross-checked against the real one in the pull request.
/// </remarks>
[TestFixture]
public sealed class CgroupLimitTests {

  private static string Root => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "cgroup-limited");

  /// <summary>
  /// A cgroup that is capped in every way the kernel allows.
  /// </summary>
  /// <remarks>
  /// Read through the same entry point the probe uses, path and all, because the path handling is
  /// half the bug surface here: a cgroup path begins with a slash, and joining it to a root naively
  /// discards the root and reads from the filesystem root instead.
  /// </remarks>
  private static CgroupInfo Capped() {
    var info = Platform.Linux.CgroupReader.Read(Root, "/system.slice/capped.service");
    Assert.That(info, Is.Not.Null, "the fixture did not resolve");
    return info!;
  }

  private static CgroupInfo Free() {
    var info = Platform.Linux.CgroupReader.Read(Root, "/user.slice/free.scope");
    Assert.That(info, Is.Not.Null);
    return info!;
  }

  [Test]
  public void APathBeginningWithASlashIsStillRelativeToTheRoot() =>
    Assert.That(Capped().Path, Is.EqualTo("/system.slice/capped.service"));

  /// <summary>
  /// A quota is written as two microsecond figures and means nothing until they are divided.
  /// "Half a core" is the sentence somebody wants; "50000 100000" is not.
  /// </summary>
  [Test]
  public void AQuotaIsReportedAsANumberOfCores() =>
    Assert.That(Capped().CpuQuotaCores, Is.EqualTo(0.5).Within(0.0001));

  /// <summary>
  /// Unlimited is not a quantity. A caller that formatted a very large number would print something
  /// absurd, and "no limit" and "a limit of nine million terabytes" must not look alike (PRD §5.3).
  /// </summary>
  [Test]
  public void NoLimitIsNotAVeryLargeNumber() {
    var free = Free();

    Assert.That(free.CpuQuotaCores, Is.Null);
    Assert.That(free.MemoryMaxBytes.HasValue, Is.False);
    Assert.That(free.MemoryHighBytes.HasValue, Is.False);
    Assert.That(free.PidsMax.HasValue, Is.False);
  }

  /// <summary>
  /// A controller that is switched off and a controller set to <c>max</c> are different answers.
  /// </summary>
  /// <remarks>
  /// Both have no number to show, and reading them as the same thing said the machine could not
  /// answer when it had answered plainly. The first means the question does not apply to this group
  /// and an ancestor's limit governs it; the second means this group was deliberately left unbounded.
  /// Somebody chasing why a process hit a wall needs to know which (PRD §5.3).
  /// </remarks>
  [Test]
  public void NoControllerAndNoLimitAreNotTheSameAnswer() {
    var free = Free();

    // memory.max exists in this cgroup and reads "max".
    Assert.That(free.MemoryMaxBytes.HasValue, Is.False);
    Assert.That(free.MemoryMaxBytes.Reason, Is.EqualTo(UnknownReason.NoLimit));

    // cpu.stat does not exist here at all, because the controller is not enabled.
    Assert.That(free.ThrottledCount.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>And each renders as itself rather than as a shared blank.</summary>
  [Test]
  public void EachKindOfAbsenceRendersAsItself() {
    Assert.That(Humanize.Placeholder(UnknownReason.NoLimit), Is.Not.Empty);
    Assert.That(
      Humanize.Placeholder(UnknownReason.NoLimit),
      Is.Not.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  [Test]
  public void TheLimitsAndTheUsageAreBothRead() {
    var capped = Capped();

    Assert.That(capped.MemoryCurrentBytes.Value, Is.EqualTo(268435456ul));
    Assert.That(capped.MemoryMaxBytes.Value, Is.EqualTo(536870912ul));
    Assert.That(capped.MemoryHighBytes.Value, Is.EqualTo(402653184ul), "the soft cap is a different limit");
    Assert.That(capped.PidsCurrent.Value, Is.EqualTo(12ul));
    Assert.That(capped.PidsMax.Value, Is.EqualTo(64ul));
  }

  /// <summary>
  /// The number that turns "it is slow" into "it is being throttled", which is a different sentence
  /// and the one somebody can act on.
  /// </summary>
  [Test]
  public void ThrottlingIsCounted() =>
    Assert.That(Capped().ThrottledCount.Value, Is.EqualTo(137ul));

  /// <summary>
  /// A cgroup with a quota it never reaches reports nought throttles, which is a real nought. A
  /// cgroup whose CPU controller is not enabled has no such file, which is not.
  /// </summary>
  [Test]
  public void NoThrottleFileIsNotNoughtThrottles() {
    var free = Free();

    Assert.That(free.ThrottledCount.HasValue, Is.False);
    Assert.That(free.ThrottledCount.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>
  /// A limit file existing does not mean its controller is on. A delegated cgroup may have memory
  /// and not cpu, in which case the CPU limit is inherited from an ancestor rather than absent.
  /// </summary>
  [Test]
  public void TheEnabledControllersAreReported() {
    Assert.That(Capped().Controllers, Is.EqualTo(new[] { "cpu", "io", "memory", "pids" }));
    Assert.That(Capped().Has("cpu"), Is.True);
    Assert.That(Free().Has("cpu"), Is.False);
    Assert.That(Free().Has("memory"), Is.True);
  }

  /// <summary>Per-cgroup pressure is the same format as the machine's, and the same parser.</summary>
  [Test]
  public void PressureIsReadPerCgroup() {
    var capped = Capped();

    Assert.That(capped.CpuPressure.Some.Average10.Value, Is.EqualTo(12.5).Within(0.001));
    Assert.That(capped.CpuPressure.Full.Average10.Value, Is.EqualTo(4).Within(0.001));
    Assert.That(capped.IoPressure.Some.Average300.Value, Is.EqualTo(3).Within(0.001));
  }

  /// <summary>
  /// A cgroup with no pressure files — an older kernel, or one built without PSI — reports unknown
  /// rather than a machine under no pressure at all.
  /// </summary>
  [Test]
  public void ACgroupWithoutPressureFilesSaysSoRatherThanReadingZero() {
    var free = Free();

    Assert.That(free.CpuPressure.HasValue, Is.False);
    Assert.That(free.MemoryPressure.HasValue, Is.False);
  }

  [Test]
  public void ACgroupThatIsNotThereIsNullRatherThanAnError() {
    Assert.That(Platform.Linux.CgroupReader.Read(Root, "/nothing/here.scope"), Is.Null);
    Assert.That(Platform.Linux.CgroupReader.Read(Root, null), Is.Null);
    Assert.That(Platform.Linux.CgroupReader.Read(Root, string.Empty), Is.Null);
  }

}
