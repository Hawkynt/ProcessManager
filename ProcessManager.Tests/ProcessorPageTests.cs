using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The processor page's figures beyond utilisation (PRD §46).
/// </summary>
/// <remarks>
/// The machine's own bookkeeping — switches, interrupts, deferred work, descriptors — and where each
/// logical processor sits. Every one of them is a counter that is a confident zero if it is left to
/// default, which is why each is checked for the reason it carries as well as for its value.
/// </remarks>
[TestFixture]
public sealed class ProcessorPageTests {

  private const ulong _Tenth = 100_000_000;

  private static readonly long _Second = System.Diagnostics.Stopwatch.Frequency;

  /// <summary>Two samples a second apart, with the machine-wide counters moving as asked.</summary>
  private static (SystemSnapshot Before, SystemSnapshot After) Machine(
    int cores = 4,
    ulong switches = 0,
    ulong interrupts = 0,
    ulong softInterrupts = 0,
    ulong irqNs = 0,
    ulong softIrqNs = 0
  ) {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    before.PrepareProcesses(0);
    var first = before.PrepareCores(cores);
    for (var i = 0; i < cores; ++i)
      first[i] = default;

    before.System = SystemCounters.Unread;
    before.System.ContextSwitches = Counter.Of(0);
    before.System.Interrupts = Counter.Of(0);
    before.System.SoftInterrupts = Counter.Of(0);

    var after = new SystemSnapshot { TimestampTicks = _Second };
    after.PrepareProcesses(0);
    var second = after.PrepareCores(cores);
    for (var i = 0; i < cores; ++i)
      // Core i is busy i tenths of the interval, all of it in user code.
      second[i] = new() { UserNs = (ulong)i * _Tenth, IdleNs = (ulong)(10 - i) * _Tenth };

    after.System = SystemCounters.Unread;
    after.System.ContextSwitches = Counter.Of(switches);
    after.System.Interrupts = Counter.Of(interrupts);
    after.System.SoftInterrupts = Counter.Of(softInterrupts);
    after.System.OpenDescriptors = Counter.Of(4321);
    after.System.DescriptorLimit = Counter.Of(9_223_372_036_854_775_807);
    after.System.Cpu = new() {
      UserNs = 2 * _Tenth,
      KernelNs = _Tenth,
      IrqNs = irqNs,
      SoftIrqNs = softIrqNs,
      IdleNs = (10 * _Tenth) - (3 * _Tenth) - irqNs - softIrqNs,
    };

    return (before, after);
  }

  private static SnapshotDelta DeltaOf(SystemSnapshot before, SystemSnapshot after) {
    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);
    return delta;
  }

  /// <summary>
  /// One row of one section, by name.
  /// </summary>
  /// <remarks>
  /// Scoped to a section rather than searched across all of them: the activity list carries a
  /// context-switch rate of its own (§51), and a search over the whole report would check the wrong
  /// row and pass while the processor page said nothing at all.
  /// </remarks>
  private static string Value(IReadOnlyList<PerformanceSection> sections, string label, string title = "Processor") {
    foreach (var row in SectionOf(sections, title).Rows)
      if (row.Label == label)
        return row.Value;

    Assert.Fail($"no row called '{label}' in '{title}'");
    return string.Empty;
  }

  private static PerformanceSection SectionOf(IReadOnlyList<PerformanceSection> sections, string title) {
    foreach (var section in sections)
      if (section.Title == title)
        return section;

    Assert.Fail($"no section called '{title}'");
    return default;
  }

  [Test]
  public void TheThreeRateCountersCarryTheirTotalsBesideThem() {
    var (before, after) = Machine(switches: 12_000, interrupts: 8_000, softInterrupts: 2_500);
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after));

    Assert.That(Value(sections, "Context switches"), Is.EqualTo("12.0k/s  (12.0k since boot)"));
    Assert.That(Value(sections, "Interrupts"), Is.EqualTo("8.0k/s  (8.0k since boot)"));
    Assert.That(Value(sections, "Soft interrupts (deferred)"), Is.EqualTo("2.5k/s  (2.5k since boot)"));
  }

  /// <summary>
  /// They are diagnostics rather than status: §46 puts them in the collapsed block and not beside
  /// the utilisation, because nobody opens this page to find out how often the machine changed its
  /// mind.
  /// </summary>
  [Test]
  public void TheRateCountersAreLevelFourRatherThanBesideTheUtilisation() {
    var (before, after) = Machine(switches: 10);
    var processor = SectionOf(PerformanceReport.Build(new(), after, DeltaOf(before, after)), "Processor");

    foreach (var row in processor.Rows)
      if (row.Label is "Context switches" or "Interrupts" or "Soft interrupts (deferred)" or "System calls")
        Assert.That(row.IsDiagnostic, Is.True, row.Label);
  }

  /// <summary>
  /// The interrupt shares are inside the kernel figure already; they are broken out to answer
  /// whether the kernel is in there for a device or for a process.
  /// </summary>
  [Test]
  public void InterruptTimeIsSplitIntoHardAndDeferred() {
    var (before, after) = Machine(irqNs: _Tenth, softIrqNs: 2 * _Tenth);
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after));

    Assert.That(Value(sections, "Interrupt time"), Is.EqualTo("10.0 % hard, 20.0 % deferred"));
  }

  /// <summary>
  /// Linux keeps no machine-wide system-call counter, and the honest answer is that this system does
  /// not report it — not a zero, and not the context-switch count wearing another label (PRD §5.3).
  /// </summary>
  [Test]
  public void SystemCallsAreRefusedRatherThanApproximated() {
    var (before, after) = Machine(switches: 50_000);
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after));

    Assert.That(Value(sections, "System calls"), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)));
  }

  [Test]
  public void TheMachineWideDescriptorCountIsTheHandleCount() {
    var (before, after) = Machine();
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after));

    Assert.That(Value(sections, "Descriptors"), Is.EqualTo("4321"));
  }

  /// <summary>
  /// <c>fs.file-max</c> is derived from memory and is routinely nine quintillion. Printed as a number
  /// it reads as a typographical error beside a five-figure count.
  /// </summary>
  [Test]
  public void AnUnreachableDescriptorCeilingIsSaidInWords() {
    var (before, after) = Machine();
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after));
    Assert.That(Value(sections, "Descriptor limit"), Is.EqualTo("no practical limit"));

    after.System.DescriptorLimit = Counter.Of(65_536);
    Assert.That(
      Value(PerformanceReport.Build(new(), after, DeltaOf(before, after)), "Descriptor limit"),
      Is.EqualTo("65536")
    );
  }

  /// <summary>
  /// A probe that has not read the file says so. A zero here would report a machine with nothing
  /// open, which is not a state a running machine can be in (PRD §72.3).
  /// </summary>
  [Test]
  public void AnUnreadDescriptorCountIsUnknownRatherThanZero() {
    var (before, after) = Machine();
    after.System.OpenDescriptors = Counter.NotSupported;

    Assert.That(
      Value(PerformanceReport.Build(new(), after, DeltaOf(before, after)), "Descriptors"),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  #region frequency scaling (PRD §46)

  [Test]
  public void TheSpeedRangeAndItsGovernorAreHardwareFacts() {
    var host = new HostInfo {
      CpuMinimumHertz = Counter.Of(800_000_000),
      CpuMaximumHertz = Counter.Of(5_000_000_000),
      CpuGovernor = "powersave",
      CpuScalingDriver = "intel_pstate",
    };

    var processor = SectionOf(PerformanceReport.Build(host, new SystemSnapshot()), "Processor");
    var seen = 0;
    foreach (var row in processor.Rows)
      switch (row.Label) {
        case "Speed range":
          Assert.That(row.Value, Is.EqualTo("800 MHz – 5.00 GHz"));
          Assert.That(row.IsHardware, Is.True);
          ++seen;
          break;

        case "Governor":
          // The driver beside the governor, because the same word means different things under
          // different drivers and the pair is what makes either readable.
          Assert.That(row.Value, Is.EqualTo("powersave  (intel_pstate)"));
          Assert.That(row.IsHardware, Is.True);
          ++seen;
          break;

        default: break;
      }

    Assert.That(seen, Is.EqualTo(2));
  }

  /// <summary>
  /// A machine whose clock its host owns has no policy at all, and two unknown rows would claim it
  /// had one nobody could read.
  /// </summary>
  [Test]
  public void AMachineWithNoCpufreqGetsNoScalingRows() {
    var processor = SectionOf(PerformanceReport.Build(new(), new SystemSnapshot()), "Processor");
    foreach (var row in processor.Rows)
      Assert.That(row.Label, Is.Not.EqualTo("Speed range").And.Not.EqualTo("Governor"));
  }

  #endregion

  #region where a core sits (PRD §46)

  private static CpuTopology TwoNodes() => new([
    new(0, 0, 0, CoreKind.Performance, 0),
    new(1, 0, 0, CoreKind.Performance, 0),
    new(2, 1, 1, CoreKind.Efficiency, 1),
    new(3, 1, 1, CoreKind.Efficiency, 1),
  ]);

  [Test]
  public void ACoreSaysWhereItSits() {
    var (before, after) = Machine();
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after), topology: TwoNodes());

    Assert.That(Value(sections, "Socket", "Core 1"), Is.EqualTo("0"));
    Assert.That(Value(sections, "Physical core", "Core 1"), Is.EqualTo("0"));
    // The other half of the same physical core: saturating both does not do twice the work.
    Assert.That(Value(sections, "SMT sibling", "Core 1"), Is.EqualTo("0"));
    Assert.That(Value(sections, "Kind", "Core 1"), Is.EqualTo("performance"));
    Assert.That(Value(sections, "NUMA node", "Core 1"), Is.EqualTo("0"));
  }

  [Test]
  public void ACoreOnAMachineWithNoTopologyOnlySaysWhatItIsDoing() {
    var (before, after) = Machine();
    var core = SectionOf(PerformanceReport.Build(new(), after, DeltaOf(before, after)), "Core 1");

    foreach (var row in core.Rows)
      Assert.That(row.Label, Is.AnyOf("Logical processor", "Utilisation", "User time", "Kernel time"));
  }

  [Test]
  public void AHybridPartCountsItsTwoKindsOfCore() {
    var (before, after) = Machine();
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after), topology: TwoNodes());

    Assert.That(Value(sections, "Core kinds"), Is.EqualTo("2 performance, 2 efficiency"));
  }

  [Test]
  public void APartWithOneKindOfCoreSaysNothingAboutKinds() {
    var (before, after) = Machine();
    CpuTopology flat = new([
      new(0, 0, 0, CoreKind.Unknown),
      new(1, 0, 1, CoreKind.Unknown),
    ]);

    var processor = SectionOf(PerformanceReport.Build(new(), after, DeltaOf(before, after), topology: flat), "Processor");
    foreach (var row in processor.Rows)
      Assert.That(row.Label, Is.Not.EqualTo("Core kinds"));
  }

  #endregion

  #region NUMA nodes (PRD §46)

  [Test]
  public void EachNodeIsTheMeanOfItsOwnProcessors() {
    // Cores 0 and 1 are on node 0 at 0 % and 10 %; cores 2 and 3 on node 1 at 20 % and 30 %.
    var (before, after) = Machine();
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after), topology: TwoNodes());

    var first = SectionOf(sections, "Node 0");
    var second = SectionOf(sections, "Node 1");
    Assert.That(first.Primary.Value, Is.EqualTo(5).Within(0.01));
    Assert.That(second.Primary.Value, Is.EqualTo(25).Within(0.01));
    Assert.That(first.PartOf, Is.EqualTo(PerformanceReport.NodeGroup));
    Assert.That(second.PrimaryMaximum, Is.EqualTo(100));
  }

  [Test]
  public void ANodeNamesTheProcessorsSomebodyWouldPinTo() {
    var (before, after) = Machine();
    var sections = PerformanceReport.Build(new(), after, DeltaOf(before, after), topology: TwoNodes());
    Assert.That(Value(sections, "Processors", "Node 1"), Is.EqualTo("2-3"));
  }

  /// <summary>
  /// "Node 0 is the whole machine" is not a distribution, and a per-node view of it would be a
  /// second copy of the processor's own graph (§47 refuses the same row for the same reason).
  /// </summary>
  [Test]
  public void AMachineWithOneNodeGetsNoNodeSections() {
    var (before, after) = Machine();
    CpuTopology single = new([
      new(0, 0, 0, CoreKind.Unknown, 0),
      new(1, 0, 1, CoreKind.Unknown, 0),
    ]);

    foreach (var section in PerformanceReport.Build(new(), after, DeltaOf(before, after), topology: single))
      Assert.That(section.PartOf, Is.Not.EqualTo(PerformanceReport.NodeGroup));
  }

  /// <summary>
  /// A node whose processors have not been sampled has no utilisation. Averaging no readings into a
  /// nought would draw an idle node (PRD §72.3).
  /// </summary>
  [Test]
  public void ANodeWithNothingSampledIsUnknownRatherThanIdle() {
    var after = new SystemSnapshot { TimestampTicks = _Second };
    after.PrepareProcesses(0);
    after.PrepareCores(4);

    var delta = new SnapshotDelta();
    delta.Update(null, after, CpuPercentMode.Normalized);

    foreach (var section in PerformanceReport.Build(new(), after, delta, topology: TwoNodes()))
      if (section.PartOf == PerformanceReport.NodeGroup)
        Assert.That(section.Primary.HasValue, Is.False, section.Title);
  }

  #endregion

}
