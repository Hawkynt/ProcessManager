using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The machine description (PRD §46, §47, §96), read from a recorded <c>/proc</c> and <c>/sys</c> so
/// it is checked on every CI leg rather than only on Linux.
/// </summary>
/// <remarks>
/// The fixture describes two sockets of two cores each with SMT — four physical cores and eight
/// logical processors. The three counts are deliberately all different, because a parser that
/// conflates any two of them passes a fixture where they agree.
/// </remarks>
[TestFixture]
public sealed class LinuxHostTests {

  private static string Fixtures
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  private static HostInfo Read() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      SysRoot = Path.Combine(Fixtures, "sys-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = 0,
    });

    return probe.DescribeHost();
  }

  [Test]
  public void TheProcessorIsNamedAndAttributed() {
    var host = Read();
    Assert.That(host.CpuModel, Is.EqualTo("Fixture Core(TM) X-9999 @ 3.40GHz"));
    Assert.That(host.CpuVendor, Is.EqualTo("FixtureVendor"));
  }

  [Test]
  public void SocketsCoresAndLogicalProcessorsAreThreeDifferentNumbers() {
    var host = Read();

    Assert.That(host.Sockets.Value, Is.EqualTo(2ul));
    // A core id is only unique within its socket — both sockets have a core 0, and counting the
    // bare ids would report two cores on an eight-thread machine.
    Assert.That(host.PhysicalCores.Value, Is.EqualTo(4ul));
    Assert.That(host.LogicalProcessors.Value, Is.EqualTo(8ul));
  }

  [Test]
  public void TheBaseSpeedComesFromTheKernelRatherThanTheMarketingName() {
    // base_frequency is exact; the "@ 3.40GHz" in the name is a fallback for kernels without it.
    Assert.That(Read().CpuBaseHertz.Value, Is.EqualTo(3_400_000_000ul));
  }

  [Test]
  public void TheCurrentSpeedIsAveragedAcrossTheLogicalProcessors() {
    // Every processor in the fixture reports 1700.5 MHz, so the mean is that.
    Assert.That(Read().CpuCurrentHertz.Value, Is.EqualTo(1_700_500_000ul));
  }

  [Test]
  public void EachCacheLevelIsReadSeparately() {
    var host = Read();

    // L1 has two caches of different sizes at the same level; picking either for both would be
    // wrong and would look right.
    Assert.That(host.L1DataBytes.Value, Is.EqualTo(48ul * 1024));
    Assert.That(host.L1InstructionBytes.Value, Is.EqualTo(32ul * 1024));
    Assert.That(host.L2Bytes.Value, Is.EqualTo(1280ul * 1024));
    Assert.That(host.L3Bytes.Value, Is.EqualTo(16384ul * 1024));
  }

  [Test]
  public void NumaNodesAreCounted() => Assert.That(Read().NumaNodes.Value, Is.EqualTo(2ul));

  [Test]
  public void AVirtualMachineIsNamedRatherThanReducedToABoolean() =>
    // "KVM Virtual Machine" is more use to somebody diagnosing a machine than "true".
    Assert.That(Read().Virtualisation, Is.EqualTo("KVM Virtual Machine"));

  [Test]
  public void TheKernelVersionIsRead() =>
    Assert.That(Read().OperatingSystemVersion, Is.EqualTo("6.12.0-fixture"));

  /// <summary>
  /// The firmware tables are root-only, so the module facts are refused rather than guessed. A zero
  /// would say the machine has no memory slots (PRD §72.3).
  /// </summary>
  [Test]
  public void TheFirmwareFactsAreRefusedRatherThanInvented() {
    var host = Read();

    Assert.That(host.MemoryTransfersPerSecond.HasValue, Is.False);
    Assert.That(host.MemoryTransfersPerSecond.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(host.MemorySlotsUsed.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(host.MemorySlotsTotal.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(host.MemoryFormFactor, Is.Null);
  }

  /// <summary>
  /// A machine that reports no topology at all — a container, or an architecture whose cpuinfo does
  /// not carry physical ids — must say so rather than claiming one socket and one core.
  /// </summary>
  [Test]
  public void AMachineThatReportsNoTopologySaysSoRatherThanGuessingOne() {
    using var probe = new LinuxProbe(new() {
      // A directory with no cpuinfo and no sys at all.
      ProcRoot = Path.Combine(Fixtures, "does-not-exist"),
      SysRoot = Path.Combine(Fixtures, "does-not-exist"),
      EffectiveUserId = 0,
    });

    var host = probe.DescribeHost();
    Assert.That(host.Sockets.HasValue, Is.False);
    Assert.That(host.PhysicalCores.HasValue, Is.False);
    Assert.That(host.LogicalProcessors.HasValue, Is.False);
    Assert.That(host.L3Bytes.HasValue, Is.False);
    Assert.That(host.CpuModel, Is.Null);
  }

  [Test]
  public void TheDescriptionIsReadOnceAndReused() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      SysRoot = Path.Combine(Fixtures, "sys-desktop"),
      EffectiveUserId = 0,
    });

    // Nothing in it changes between samples, and several of the reads walk directories.
    Assert.That(probe.DescribeHost(), Is.SameAs(probe.DescribeHost()));
  }

  /// <summary>
  /// The machine-wide thread count was set by the Windows probe and left at zero by the Linux one,
  /// so it read as a confident zero on every Linux machine (PRD §72.3).
  /// </summary>
  [Test]
  public void TheMachineWideThreadCountIsSummed() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      SysRoot = Path.Combine(Fixtures, "sys-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var expected = 0;
    foreach (var process in snapshot.Processes)
      expected += process.ThreadCount;

    Assert.That(snapshot.System.TotalThreads, Is.EqualTo(expected));
    Assert.That(snapshot.System.TotalThreads, Is.GreaterThan(0));
  }

}
