using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The performance page's content (PRD §45–§47), which is data rather than a window precisely so
/// that it can be checked here and so that <c>--host</c> and the desktop cannot disagree (PRD §58).
/// </summary>
[TestFixture]
public sealed class PerformanceReportTests {

  private static SystemSnapshot Snapshot() {
    var snapshot = new SystemSnapshot();
    snapshot.PrepareProcesses(0);
    snapshot.System.TotalMemoryBytes = Counter.Of(16ul * 1024 * 1024 * 1024);
    snapshot.System.AvailableMemoryBytes = Counter.Of(10ul * 1024 * 1024 * 1024);
    snapshot.System.CachedMemoryBytes = Counter.Of(4ul * 1024 * 1024 * 1024);
    snapshot.System.TotalSwapBytes = Counter.Of(2ul * 1024 * 1024 * 1024);
    snapshot.System.UsedSwapBytes = Counter.Of(0ul);
    snapshot.System.UptimeSeconds = 3661;
    snapshot.System.TotalThreads = 1234;
    snapshot.System.LoadAverage1 = 1.5;
    snapshot.System.LoadAverage5 = 1.25;
    snapshot.System.LoadAverage15 = 1;
    return snapshot;
  }

  private static HostInfo Host() => new() {
    HostName = "testbox",
    OperatingSystem = "Test Linux",
    OperatingSystemVersion = "9.9.9",
    Architecture = "X64",
    CpuModel = "Fixture Core X-9999",
    CpuVendor = "FixtureVendor",
    CpuBaseHertz = Counter.Of(3_400_000_000),
    CpuCurrentHertz = Counter.Of(1_700_000_000),
    Sockets = Counter.Of(2),
    PhysicalCores = Counter.Of(4),
    LogicalProcessors = Counter.Of(8),
    NumaNodes = Counter.Of(2),
    L1DataBytes = Counter.Of(48 * 1024),
    L2Bytes = Counter.Of(1280 * 1024),
    L3Bytes = Counter.Of(16ul * 1024 * 1024),
    MemoryTransfersPerSecond = Counter.NotPermitted,
    MemorySlotsUsed = Counter.NotPermitted,
    MemorySlotsTotal = Counter.NotPermitted,
  };

  private static string Value(IReadOnlyList<PerformanceSection> sections, string label) {
    foreach (var section in sections)
      foreach (var row in section.Rows)
        if (row.Label == label)
          return row.Value;

    Assert.Fail($"no row called '{label}'");
    return string.Empty;
  }

  [Test]
  public void TheSectionsAreSystemProcessorAndMemory() {
    var sections = PerformanceReport.Build(Host(), Snapshot());
    var titles = new List<string>();
    foreach (var section in sections)
      titles.Add(section.Title);

    Assert.That(titles, Is.EqualTo(new[] { "System", "Processor", "Memory" }));
    foreach (var section in sections)
      Assert.That(section.Rows, Is.Not.Empty, section.Title);
  }

  [Test]
  public void TheProcessorFactsAreRendered() {
    var sections = PerformanceReport.Build(Host(), Snapshot());

    Assert.That(Value(sections, "Model"), Is.EqualTo("Fixture Core X-9999"));
    Assert.That(Value(sections, "Base speed"), Is.EqualTo("3.40 GHz"));
    Assert.That(Value(sections, "Current speed"), Is.EqualTo("1.70 GHz"));
    Assert.That(Value(sections, "Sockets"), Is.EqualTo("2"));
    Assert.That(Value(sections, "Physical cores"), Is.EqualTo("4"));
    Assert.That(Value(sections, "Logical processors"), Is.EqualTo("8"));
    Assert.That(Value(sections, "L3"), Is.EqualTo("16.0M"));
  }

  [Test]
  public void ASpeedBelowAGigahertzIsShownInMegahertz() {
    var sections = PerformanceReport.Build(Host() with { CpuCurrentHertz = Counter.Of(800_000_000) }, Snapshot());
    Assert.That(Value(sections, "Current speed"), Is.EqualTo("800 MHz"));
  }

  [Test]
  public void MemoryInUseIsTotalMinusAvailable() {
    // 16 GiB total, 10 available — the figure Task Manager calls "in use", and not total minus free.
    Assert.That(Value(PerformanceReport.Build(Host(), Snapshot()), "In use"), Is.EqualTo("6.0G"));
  }

  /// <summary>
  /// If available is somehow larger than total, the subtraction must not wrap: these are unsigned,
  /// and a wrapped answer would read as sixteen exabytes in use.
  /// </summary>
  [Test]
  public void AnImpossiblePairDoesNotWrapAround() {
    var snapshot = Snapshot();
    snapshot.System.AvailableMemoryBytes = Counter.Of(32ul * 1024 * 1024 * 1024);

    Assert.That(Value(PerformanceReport.Build(Host(), snapshot), "In use"), Is.EqualTo("0 B"));
  }

  [Test]
  public void AnUnreadableTotalMakesTheDerivedFigureUnknownRatherThanZero() {
    var snapshot = Snapshot();
    snapshot.System.TotalMemoryBytes = Counter.NotPermitted;

    Assert.That(
      Value(PerformanceReport.Build(Host(), snapshot), "In use"),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotPermitted))
    );
  }

  [Test]
  public void TheFirmwareFactsSayWhyRatherThanReadingZero() {
    var sections = PerformanceReport.Build(Host(), Snapshot());
    var refused = Humanize.Placeholder(UnknownReason.NotPermitted);

    Assert.That(Value(sections, "Speed"), Is.EqualTo(refused));
    Assert.That(Value(sections, "Slots used"), Is.EqualTo(refused));
    Assert.That(Value(sections, "Form factor"), Is.EqualTo(refused));
  }

  /// <summary>
  /// Utilisation is a rate. Before a second sample it must say so rather than reading 0.0 %, which
  /// would be a claim that the machine is idle (PRD §72.3).
  /// </summary>
  [Test]
  public void UtilisationWithNoSecondSampleSaysSoRatherThanReadingZero() {
    var sections = PerformanceReport.Build(Host(), Snapshot());
    Assert.That(Value(sections, "Utilisation"), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)));
  }

  [Test]
  public void UtilisationIsShownOnceThereIsADelta() {
    var snapshot = Snapshot();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var sections = PerformanceReport.Build(Host(), snapshot, delta);
    Assert.That(Value(sections, "Utilisation"), Is.Not.Empty);
  }

  [Test]
  public void UptimeIsHumanReadable() {
    Assert.That(Value(PerformanceReport.Build(Host(), Snapshot()), "Uptime"), Is.EqualTo("01:01:01"));

    var snapshot = Snapshot();
    snapshot.System.UptimeSeconds = 90061;
    Assert.That(Value(PerformanceReport.Build(Host(), snapshot), "Uptime"), Is.EqualTo("1d 01:01:01"));
  }

  /// <summary>A row with nothing to say is left out rather than shown empty.</summary>
  [Test]
  public void RowsThatDoNotApplyAreAbsentRatherThanBlank() {
    var physical = PerformanceReport.Build(Host(), Snapshot());
    foreach (var section in physical)
      foreach (var row in section.Rows)
        Assert.That(row.Label, Is.Not.EqualTo("Virtualised"), "a physical machine says nothing");

    var virtualised = PerformanceReport.Build(Host() with { Virtualisation = "KVM" }, Snapshot());
    Assert.That(Value(virtualised, "Virtualised"), Is.EqualTo("KVM"));
  }

  [Test]
  public void LoadAverageIsOmittedOnAMachineThatDoesNotPublishIt() {
    var snapshot = Snapshot();
    snapshot.System.LoadAverage1 = 0;
    snapshot.System.LoadAverage5 = 0;
    snapshot.System.LoadAverage15 = 0;

    foreach (var section in PerformanceReport.Build(Host(), snapshot))
      foreach (var row in section.Rows)
        Assert.That(row.Label, Is.Not.EqualTo("Load average"), "three zeros would be a lie about the machine");
  }

  [Test]
  public void AHostThatKnowsNothingStillProducesEveryRow() {
    // A macOS-shaped future, or a container with no cpuinfo: the page must render, saying so.
    var sections = PerformanceReport.Build(new(), new SystemSnapshot());
    foreach (var section in sections)
      foreach (var row in section.Rows)
        Assert.That(row.Value, Is.Not.Null, row.Label);
  }

}
