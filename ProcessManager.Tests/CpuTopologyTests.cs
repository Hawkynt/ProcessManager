using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// How the cores are arranged, and how the heat map groups them (PRD §46).
/// </summary>
/// <remarks>
/// The parser carries no platform attribute, so it runs on every CI leg — including the Windows one,
/// which has no <c>/sys</c> to read.
/// </remarks>
[TestFixture]
public sealed class CpuTopologyTests {

  #region the kernel's list notation

  [Test]
  public void ARangeIsInclusiveAtBothEnds() =>
    Assert.That(CpuList.Parse("0-3"), Is.EqualTo(new[] { 0, 1, 2, 3 }));

  [Test]
  public void RangesAndSinglesMix() =>
    Assert.That(CpuList.Parse("0-3,8,12-13"), Is.EqualTo(new[] { 0, 1, 2, 3, 8, 12, 13 }));

  [Test]
  public void TheResultIsSortedAndFreeOfDuplicates() =>
    Assert.That(CpuList.Parse("5,0-2,1,5"), Is.EqualTo(new[] { 0, 1, 2, 5 }));

  [Test]
  public void AnEmptyListIsAnEmptySetRatherThanAnError() {
    Assert.That(CpuList.Parse(string.Empty), Is.Empty);
    Assert.That(CpuList.Parse("\n"), Is.Empty);
  }

  /// <summary>
  /// This parses kernel files. A build that refuses to draw a heat map because one /sys file gained
  /// a field is worse than one that draws the cores it understood (PRD §73).
  /// </summary>
  [Test]
  public void RubbishIsSkippedRatherThanThrown() {
    Assert.That(() => CpuList.Parse("0-,x,,7-3,2"), Throws.Nothing);
    Assert.That(CpuList.Parse("0-,x,,7-3,2"), Is.EqualTo(new[] { 2 }));
  }

  /// <summary>A range covering four billion processors is a corrupted file, not a large machine.</summary>
  [Test]
  public void AnAbsurdRangeIsNotExpanded() =>
    Assert.That(CpuList.Parse("0-999999"), Is.Empty);

  #endregion

  #region grouping

  /// <summary>Eight performance cores with SMT, then eight efficiency cores without — an Alder Lake.</summary>
  private static CpuTopology Hybrid() {
    var cores = new List<CoreDescriptor>();
    for (var core = 0; core < 8; ++core) {
      cores.Add(new(core * 2, 0, core, CoreKind.Performance));
      cores.Add(new((core * 2) + 1, 0, core, CoreKind.Performance));
    }

    for (var core = 0; core < 8; ++core)
      cores.Add(new(16 + core, 0, 8 + core, CoreKind.Efficiency));

    return new(cores);
  }

  private static CpuTopology TwoSockets() {
    var cores = new List<CoreDescriptor>();
    for (var package = 0; package < 2; ++package)
      for (var core = 0; core < 4; ++core)
        cores.Add(new((package * 4) + core, package, core, CoreKind.Unknown));

    return new(cores);
  }

  [Test]
  public void AHybridMachineKnowsItIsOne() {
    Assert.That(Hybrid().IsHybrid, Is.True);
    Assert.That(TwoSockets().IsHybrid, Is.False);
  }

  /// <summary>
  /// A machine that says nothing about kinds is not hybrid. Unknown is not a third kind to be
  /// contrasted with the others — it is the absence of the answer.
  /// </summary>
  [Test]
  public void UnknownIsNotAKindThatMakesAMachineHybrid() {
    var mixed = new CpuTopology([
      new(0, 0, 0, CoreKind.Performance),
      new(1, 0, 1, CoreKind.Unknown),
    ]);

    Assert.That(mixed.IsHybrid, Is.False);
  }

  /// <summary>
  /// The order is the point: a grid in kernel enumeration order interleaves the two kinds on some
  /// machines and separates them on others, so the same silicon would not always look the same.
  /// </summary>
  [Test]
  public void PerformanceCoresComeBeforeEfficiencyCores() {
    var order = new List<CoreKind>();
    foreach (var core in Hybrid().Of(0))
      order.Add(core.Kind);

    var boundary = order.IndexOf(CoreKind.Efficiency);
    Assert.That(boundary, Is.EqualTo(16), "sixteen performance threads first");
    for (var i = boundary; i < order.Count; ++i)
      Assert.That(order[i], Is.EqualTo(CoreKind.Efficiency));
  }

  /// <summary>Saturating two SMT siblings is not twice the work of saturating one, so they sit together.</summary>
  [Test]
  public void SmtSiblingsAreAdjacent() {
    var members = Hybrid().Of(0);

    for (var i = 0; i < 16; i += 2)
      Assert.That(members[i].Core, Is.EqualTo(members[i + 1].Core), $"threads {i} and {i + 1}");
  }

  [Test]
  public void EachSocketIsItsOwnGroup() {
    var topology = TwoSockets();

    Assert.That(topology.Packages, Is.EqualTo(new[] { 0, 1 }));
    Assert.That(topology.Of(0), Has.Count.EqualTo(4));
    Assert.That(topology.Of(1), Has.Count.EqualTo(4));
    foreach (var core in topology.Of(1))
      Assert.That(core.Logical, Is.GreaterThanOrEqualTo(4));
  }

  /// <summary>
  /// A container, or an architecture that publishes no topology. The map falls back to a flat row,
  /// which is what the bar strip always drew — and never to "this machine has no cores".
  /// </summary>
  [Test]
  public void AMachineThatPublishesNoTopologyHasNoSocketsRatherThanOne() {
    Assert.That(CpuTopology.Empty.Packages, Is.Empty);
    Assert.That(CpuTopology.Empty.IsHybrid, Is.False);

    var unnumbered = new CpuTopology([new(0, -1, -1, CoreKind.Unknown), new(1, -1, -1, CoreKind.Unknown)]);
    Assert.That(unnumbered.Packages, Is.Empty);
  }

  [Test]
  public void AskingAboutASocketThatIsNotThereIsEmptyRatherThanAnError() =>
    Assert.That(Hybrid().Of(7), Is.Empty);

  #endregion

  #region read off a real /sys, recorded

  /// <summary>
  /// An Alder Lake shape, from files: six performance cores with SMT and eight efficiency cores
  /// without. This machine is not hybrid, so nothing but a fixture exercises the path that matters
  /// most — and the two PMU directories the kernel exposes are the whole of the detection.
  /// </summary>
  private static CpuTopology FromFixture() {
    using var probe = new Platform.Linux.LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      SysRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sys-hybrid"),
      EffectiveUserId = 0,
    });

    return probe.DescribeTopology();
  }

  [Test]
  public void AHybridMachinesTwoKindsAreReadOffItsPmuDirectories() {
    var topology = FromFixture();

    Assert.That(topology.Cores, Has.Count.EqualTo(20));
    Assert.That(topology.IsHybrid, Is.True);

    var performance = 0;
    var efficiency = 0;
    foreach (var core in topology.Cores)
      if (core.Kind == CoreKind.Performance)
        ++performance;
      else if (core.Kind == CoreKind.Efficiency)
        ++efficiency;

    Assert.That(performance, Is.EqualTo(12), "six cores, two threads each");
    Assert.That(efficiency, Is.EqualTo(8), "eight cores, one thread each");
  }

  [Test]
  public void TheFixturesCoresAreGroupedFastestFirstWithSiblingsTogether() {
    var members = FromFixture().Of(0);

    Assert.That(members[0].Kind, Is.EqualTo(CoreKind.Performance));
    Assert.That(members[^1].Kind, Is.EqualTo(CoreKind.Efficiency));
    Assert.That(members[0].Core, Is.EqualTo(members[1].Core), "SMT siblings");
    Assert.That(members[0].Logical, Is.Zero);
    Assert.That(members[1].Logical, Is.EqualTo(1));
  }

  /// <summary>
  /// A machine with no <c>cpu_core</c>/<c>cpu_atom</c> directories is not hybrid, and every core is
  /// unknown rather than being guessed at from clock speeds.
  /// </summary>
  [Test]
  public void AMachineWithNoPmuDirectoriesGuessesNothing() {
    using var probe = new Platform.Linux.LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      SysRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sys-desktop"),
      EffectiveUserId = 0,
    });

    var topology = probe.DescribeTopology();
    Assert.That(topology.IsHybrid, Is.False);
    foreach (var core in topology.Cores)
      Assert.That(core.Kind, Is.EqualTo(CoreKind.Unknown));
  }

  #endregion

  #region big.LITTLE, which has no PMU directories at all (PRD §46)

  private static CpuTopology FromArmFixture() {
    using var probe = new Platform.Linux.LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      SysRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sys-biglittle"),
      EffectiveUserId = 0,
    });

    return probe.DescribeTopology();
  }

  /// <summary>
  /// An RK3399 shape: four A53s at capacity 467 and two A72s at 1024. The hybrid PMU directories are
  /// Intel's and this machine has none, so the kind comes from the number the scheduler itself uses.
  /// </summary>
  [Test]
  public void ABigLittleMachinesTwoKindsComeFromTheSchedulersOwnCapacities() {
    var topology = FromArmFixture();

    Assert.That(topology.Cores, Has.Count.EqualTo(6));
    Assert.That(topology.IsHybrid, Is.True);

    foreach (var core in topology.Cores)
      Assert.That(
        core.Kind,
        Is.EqualTo(core.Logical >= 4 ? CoreKind.Performance : CoreKind.Efficiency),
        $"cpu{core.Logical}"
      );
  }

  /// <summary>
  /// And the map draws the fast ones first, which on this board is the half the kernel enumerates
  /// last: a grid in enumeration order would put the two A72s at the end.
  /// </summary>
  [Test]
  public void TheBigCoresAreDrawnBeforeTheLittleOnes() {
    var members = FromArmFixture().Of(0);

    Assert.That(members[0].Logical, Is.EqualTo(4));
    Assert.That(members[1].Logical, Is.EqualTo(5));
    Assert.That(members[^1].Logical, Is.EqualTo(3));
  }

  /// <summary>
  /// The machine this was written on publishes <c>cpu_capacity</c> too, and every processor reports
  /// 1024. One capacity is a machine whose cores are all alike, and calling them all performance
  /// cores would put a distinction on the page the silicon does not have (PRD §5.3).
  /// </summary>
  [Test]
  public void OneCapacityForEveryProcessorIsNotAHybridMachine() {
    using var probe = new Platform.Linux.LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      SysRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sys-flat-capacity"),
      EffectiveUserId = 0,
    });

    var topology = probe.DescribeTopology();
    Assert.That(topology.Cores, Has.Count.EqualTo(2));
    Assert.That(topology.IsHybrid, Is.False);
    foreach (var core in topology.Cores)
      Assert.That(core.Kind, Is.EqualTo(CoreKind.Unknown));
  }

  #endregion

}
