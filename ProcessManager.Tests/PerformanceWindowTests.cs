using System.Drawing;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The system information window (PRD §45).
/// </summary>
/// <remarks>
/// Testable without a display for the same reason the tree binder is: the controls are owner-drawn
/// and their collections work unrealised. What is checked here is the thing a screenshot cannot
/// catch — that the page follows the machine. It was modal and painted once, so its numbers never
/// moved, and no picture of it would have looked wrong.
/// </remarks>
[TestFixture]
public sealed class PerformanceWindowTests {

  /// <summary>A machine with two disks and two interfaces, whose counters the test advances.</summary>
  private sealed class StubProbe : ISystemProbe {

    public ulong Ticks;
    public ulong DiskBytes;
    public int Disks = 2;

    public string Description => "stub";

    public int Cores = 4;

    public void Sample(SystemSnapshot snapshot) {
      snapshot.PrepareProcesses(0);

      // Core i spends i tenths of the second busy, so the per-core figures differ from each other
      // and from the machine's.
      var cores = snapshot.PrepareCores(this.Cores);
      for (var i = 0; i < this.Cores; ++i)
        cores[i] = new() {
          UserNs = this.Ticks / 10 * (ulong)i,
          IdleNs = this.Ticks / 10 * (ulong)(10 - i),
        };

      snapshot.TimestampTicks = (long)(this.Ticks += (ulong)System.Diagnostics.Stopwatch.Frequency);
      snapshot.System.TotalMemoryBytes = Counter.Of(16ul * 1024 * 1024 * 1024);
      snapshot.System.AvailableMemoryBytes = Counter.Of(8ul * 1024 * 1024 * 1024);
      snapshot.System.Cpu = new() { UserNs = this.Ticks, IdleNs = this.Ticks };

      var disks = snapshot.PrepareDisks(this.Disks);
      for (var i = 0; i < this.Disks; ++i)
        disks[i] = new() {
          Name = $"sd{(char)('a' + i)}",
          ReadBytes = Counter.Of(this.DiskBytes += 1024),
          WriteBytes = Counter.Of(0),
          ReadOperations = Counter.Of(0),
          WriteOperations = Counter.Of(0),
          BusyMilliseconds = Counter.Of(this.Ticks / 4),
        };

      var networks = snapshot.PrepareNetworks(1);
      networks[0] = new() {
        Name = "eth0",
        ReceivedBytes = Counter.Of(this.DiskBytes * 2),
        SentBytes = Counter.Of(this.DiskBytes),
        ReceivedPackets = Counter.Of(0),
        SentPackets = Counter.Of(0),
      };
    }

    public HostInfo DescribeHost() => new() { HostName = "stub", CpuModel = "Fixture CPU" };

    /// <summary>An adapter, when a test asks for one. Most do not, and a machine with none is the
    /// ordinary case this stub describes.</summary>
    public readonly List<GpuInfo> Gpus = [];

    public IReadOnlyList<GpuInfo> DescribeGpus() => this.Gpus;
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public DiskInfo DescribeDisk(string name) => new(name, "Fixture Disk", false, Counter.Of(1024));

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, "00:11:22:33:44:55", Counter.Of(1_000_000_000), "up", Counter.Of(1500), false);

    public void Dispose() { }

  }

  private static (PerformanceWindow Window, StubProbe Probe, Sampler Sampler) Open() {
    var probe = new StubProbe();
    var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();
    return (new(probe, sampler), probe, sampler);
  }

  /// <summary>
  /// The page opens on whatever is under the greatest load (PRD §45.3).
  /// </summary>
  /// <remarks>
  /// The stub's processor is at fifty percent and its disks are at full active time, so the page
  /// opens on a disk — which is the point: it lands on what is busy rather than on the processor,
  /// where every previous version of this page always started.
  /// </remarks>
  [Test]
  public void ThePageOpensOnWhateverIsBusiest() {
    var probe = new StubProbe();
    var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();

    var window = new PerformanceWindow(probe, sampler, openOnBusiest: true);
    Assert.That(Titles(window)[SelectedIndex(window)], Is.EqualTo("Disk — sda"));
  }

  /// <summary>
  /// A battery at full charge is not a machine under load, and neither is a warm sensor chip. Both
  /// are percentages of exactly the right shape, and both used to win (PRD §45.3).
  /// </summary>
  [Test]
  public void AFullBatteryIsNotTheBusiestThingOnTheMachine() {
    var probe = new StubProbe();
    var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();

    var battery = new BatteryInfo(
      "BAT0",
      ChargeState.Full,
      "full",
      OnExternalPower: true,
      ChargePercent: Counter.Of(100),
      EnergyNowMicrowattHours: Counter.NotSupported,
      EnergyFullMicrowattHours: Counter.NotSupported,
      EnergyDesignMicrowattHours: Counter.NotSupported,
      PowerMicrowatts: Counter.NotSupported,
      VoltageMicrovolts: Counter.NotSupported,
      CycleCount: Counter.NotSupported,
      Technology: null,
      Manufacturer: null,
      Model: "Fixture Pack",
      Serial: null
    );

    var sections = Query.PerformanceReport.Build(
      probe.DescribeHost(),
      sampler.Current,
      sampler.Delta,
      probe.DescribeDisk,
      probe.DescribeInterface,
      describeBatteries: () => [battery]
    );

    foreach (var section in sections)
      if (section.Title.StartsWith("Battery", StringComparison.Ordinal)) {
        Assert.That(section.Primary.Value, Is.EqualTo(100));
        Assert.That(section.PrimaryIsLoad, Is.False, "a charge is not a load");
        return;
      }

    Assert.Fail("no battery section");
  }

  /// <summary>
  /// And does not, for somebody who keeps it on one resource and does not want it moved out from
  /// under them (PRD §45.3, §67).
  /// </summary>
  [Test]
  public void TurningThatOffOpensOnTheFirstResourceInstead() {
    var probe = new StubProbe();
    var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();

    var window = new PerformanceWindow(probe, sampler, openOnBusiest: false);
    Assert.That(SelectedIndex(window), Is.Zero);
    Assert.That(Titles(window)[0], Is.EqualTo("System"));
  }

  [Test]
  public void TheRailListsEveryResource() {
    var (window, _, _) = Open();

    var titles = Titles(window);
    Assert.That(titles, Does.Contain("Processor"));
    Assert.That(titles, Does.Contain("Memory"));
    Assert.That(titles, Does.Contain("Disk — sda"));
    Assert.That(titles, Does.Contain("Disk — sdb"));
    Assert.That(titles, Does.Contain("Network — eth0"));
  }

  /// <summary>
  /// The rail carries each resource's own reading, so it answers "which of these is busy" before
  /// anything is clicked.
  /// </summary>
  [Test]
  public void EachRailEntryCarriesItsOwnReading() {
    var (window, _, _) = Open();

    foreach (var entry in Entries(window))
      if (entry.StartsWith("Disk", StringComparison.Ordinal) || entry.StartsWith("Processor", StringComparison.Ordinal))
        Assert.That(entry, Does.Contain("%"), entry);
  }

  /// <summary>
  /// The bug this window shipped with: it was modal and drawn once, so the readings never changed
  /// while it was open. No screenshot of it would have looked wrong.
  /// </summary>
  [Test]
  public void TheReadingsFollowTheMachine() {
    var (window, probe, sampler) = Open();
    var before = Entries(window);

    // Two more samples with the disk counters climbing faster than before.
    probe.DiskBytes += 10 * 1024 * 1024;
    sampler.Sample();
    window.UpdateFromSample();
    var after = Entries(window);

    Assert.That(after, Is.Not.EqualTo(before), "the page must move with the machine");
  }

  /// <summary>
  /// A disk appearing renumbers the rail. The entry that was selected has to survive it, or the
  /// page jumps to another resource while somebody is reading this one.
  /// </summary>
  [Test]
  public void ADeviceAppearingDoesNotThrowAwayTheSelection() {
    var (window, probe, sampler) = Open();

    var index = Titles(window).IndexOf("Memory");
    Assert.That(index, Is.GreaterThanOrEqualTo(0));
    Select(window, index);

    probe.Disks = 3;
    sampler.Sample();
    window.UpdateFromSample();

    Assert.That(Titles(window), Does.Contain("Disk — sdc"), "the new disk is listed");
    Assert.That(Titles(window)[SelectedIndex(window)], Is.EqualTo("Memory"), "and the selection stayed put");
  }

  /// <summary>
  /// A machine with twenty cores would put twenty entries in the rail and bury the disks under them.
  /// The cores belong under the processor, where one checkbox switches between the whole and the
  /// parts (PRD §46).
  /// </summary>
  [Test]
  public void TheCoresAreNotTwentyEntriesInTheRail() {
    var (window, _, _) = Open();

    foreach (var title in Titles(window))
      Assert.That(title, Does.Not.StartWith("Core "), title);

    Assert.That(Titles(window), Does.Contain("Processor"), "one entry, not one per core");
  }

  /// <summary>The terminal has no checkbox, so the report still carries every core.</summary>
  [Test]
  public void TheReportStillCarriesEveryCoreForTheTerminal() {
    var (_, probe, sampler) = Open();

    var titles = new List<string>();
    foreach (var section in Query.PerformanceReport.Build(probe.DescribeHost(), sampler.Current, sampler.Delta))
      titles.Add(section.Title);

    Assert.That(titles, Does.Contain("Core 0"));
    Assert.That(titles, Does.Contain("Core 3"));
  }

  /// <summary>Ticking the box swaps one plot for a grid of them, and unticking puts it back.</summary>
  [Test]
  public void TheCheckboxSwapsTheWholeForTheParts() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Processor"));

    Assert.That(PlotsShowing(window), Is.EqualTo(1), "the machine as one plot");

    Toggle(window, true);
    Assert.That(PlotsShowing(window), Is.EqualTo(4), "one per core, and the whole one hidden");

    Toggle(window, false);
    Assert.That(PlotsShowing(window), Is.EqualTo(1));
  }

  /// <summary>The box belongs to the processor page and has no meaning on a disk.</summary>
  [Test]
  public void TheCheckboxOnlyAppearsWhereItMeansSomething() {
    var (window, _, _) = Open();

    Select(window, Titles(window).IndexOf("Processor"));
    Assert.That(Box(window).Visible, Is.True);

    Select(window, Titles(window).IndexOf("Disk — sda"));
    Assert.That(Box(window).Visible, Is.False);
  }

  /// <summary>
  /// Leaving it ticked and walking away from the processor must not leave a grid of core plots
  /// painted over a disk's page.
  /// </summary>
  [Test]
  public void TheCorePlotsGoAwayWithTheProcessorPage() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Processor"));
    Toggle(window, true);
    Assert.That(PlotsShowing(window), Is.EqualTo(4), "four cores");

    Select(window, Titles(window).IndexOf("Disk — sda"));

    // A disk's own two, and none of the processor's four left painted underneath.
    Assert.That(PlotsShowing(window), Is.EqualTo(2));
  }

  #region the rail carries its own history (PRD §45.1)

  /// <summary>
  /// A row of text answers "which of these is busy" and not "how long has it been", which is the
  /// second of the three questions the page exists to answer. The sparkline is that answer, and it
  /// has to be the same history the main graph draws or the two disagree.
  /// </summary>
  [Test]
  public void EveryRailRowCarriesTheHistoryItsGraphDraws() {
    var (window, _, _) = Open();

    var row = RowFor(window, "Processor");
    Assert.That(row.History, Is.Not.Null);
    Assert.That(row.History!.Count, Is.GreaterThan(0));
    Assert.That(row.Maximum, Is.EqualTo(100), "a percentage is on a fixed scale");
  }

  [Test]
  public void TheRailRowsCarryASecondReadingWhereThereIsOneWorthHaving() {
    var (window, _, _) = Open();

    Assert.That(RowFor(window, "Memory").Secondary, Does.Contain("/"), "used of installed");
    Assert.That(RowFor(window, "Processor").Primary, Does.Contain("%"));
  }

  /// <summary>A resource with nothing extra to say says nothing rather than repeating itself.</summary>
  [Test]
  public void ARowWithNoSecondReadingLeavesItEmpty() {
    var (window, _, _) = Open();

    Assert.That(RowFor(window, "System").Secondary, Is.Empty);
  }

  /// <summary>
  /// Each resource owns one colour across the whole window, so the eye can follow it from the rail
  /// to the graph (§45.5).
  /// </summary>
  [Test]
  public void ARowsColourIsItsResourcesColour() {
    var (window, _, _) = Open();

    Assert.That(RowFor(window, "Processor").Accent, Is.Not.EqualTo(RowFor(window, "Memory").Accent));
    Assert.That(RowFor(window, "Disk — sda").Accent, Is.EqualTo(RowFor(window, "Disk — sdb").Accent));
  }

  /// <summary>
  /// The rail's colour and the graph's colour used to be worked out separately and disagreed — a
  /// GPU's sparkline was orange and its graphs teal, which reads as two different resources.
  /// </summary>
  [Test]
  public void ASparklineAndItsGraphAgreeAboutTheResourcesColour() {
    var (window, _, _) = Open();

    Select(window, Titles(window).IndexOf("Processor"));
    var plot = PlotShowing(window);

    Assert.That(plot, Is.Not.Null);
    Assert.That(plot!.SeriesColours, Is.Not.Empty);
    Assert.That(plot.SeriesColours[0], Is.EqualTo(RowFor(window, "Processor").Accent));
  }

  /// <summary>
  /// The rail is 230 pixels wide and "GPU — NVIDIA RTX A5000 Laptop GPU" is not. The rail names the
  /// resource and the header names the hardware; one truncated string in the rail says less than the
  /// two of them do (§45.1).
  /// </summary>
  [Test]
  public void TheRailShowsAShortNameAndKeepsTheLongOneToItself() {
    var (window, _, _) = Open();

    Assert.That(Displayed(window), Does.Contain("Disk sda"));
    Assert.That(Titles(window), Does.Contain("Disk — sda"), "and the section is still identified in full");

    foreach (var shown in Displayed(window))
      Assert.That(shown.Length, Is.LessThanOrEqualTo(24), shown);
  }

  #endregion

  #region the page opens on what is busy (PRD §45.3)

  /// <summary>
  /// A page that always opens on the processor makes somebody find the busy resource themselves,
  /// which is the one thing the rail was supposed to save them.
  /// </summary>
  [Test]
  public void ThePageOpensOnWhateverIsUnderTheGreatestLoad() {
    var probe = new StubProbe();
    var sampler = new Sampler(probe);
    sampler.Sample();

    // The disks climb hard while the processor idles.
    probe.DiskBytes += 100 * 1024 * 1024;
    sampler.Sample();
    var window = new PerformanceWindow(probe, sampler);

    Assert.That(Titles(window)[SelectedIndex(window)], Does.StartWith("Disk"));
  }

  /// <summary>
  /// Bytes per second are not percent. Eleven thousand of the former is not busier than eleven of
  /// the latter — it is a different quantity wearing a larger number.
  /// </summary>
  [Test]
  public void AThroughputIsNeverMistakenForALoad() {
    var (window, _, _) = Open();

    Assert.That(Titles(window)[SelectedIndex(window)], Does.Not.StartWith("Network"));
  }

  #endregion

  #region the composition bar (PRD §14)

  [Test]
  public void TheMemoryPageCarriesACompositionBarAndNoOtherPageDoes() {
    var (window, _, _) = Open();

    Select(window, Titles(window).IndexOf("Memory"));
    Assert.That(Bar(window).Visible, Is.True);

    Select(window, Titles(window).IndexOf("Processor"));
    Assert.That(Bar(window).Visible, Is.False);
  }

  [Test]
  public void TheBarsBandsPartitionTheMachinesMemory() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Memory"));

    var composition = Bar(window).Composition;
    var sum = 0ul;
    foreach (var band in composition.Bands)
      sum += band.Bytes;

    Assert.That(composition.HasValue, Is.True);
    Assert.That(sum, Is.EqualTo(composition.TotalBytes));
  }

  /// <summary>
  /// Room for the bar is left whether or not the resource has one, so moving between two pages with
  /// the same number of graphs does not shuffle their numbers up and down by thirty pixels.
  /// </summary>
  [Test]
  public void RoomForTheBarIsLeftEvenOnPagesThatHaveNone() {
    var (window, _, _) = Open();

    // Both stack two graphs; only one of them has a bar.
    Select(window, Titles(window).IndexOf("Memory"));
    var withBar = HeadingNamed(window, "Live").Y;

    Select(window, Titles(window).IndexOf("Disk — sda"));
    Assert.That(Bar(window).Visible, Is.False);
    Assert.That(HeadingNamed(window, "Live").Y, Is.EqualTo(withBar));
  }

  #endregion

  #region two columns, not one list (PRD §45.1)

  [Test]
  public void TheLiveMeasurementsAndTheHardwareFactsAreSeparated() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Processor"));

    var live = LabelsAt(window, hardware: false);
    var hardware = LabelsAt(window, hardware: true);

    Assert.That(live, Does.Contain("Utilisation"));
    Assert.That(live, Does.Not.Contain("L3"), "a cache size is not a measurement");
    Assert.That(hardware, Does.Contain("L3"));
    Assert.That(hardware, Does.Not.Contain("Utilisation"));
  }

  /// <summary>The header says what this is on the left and what it actually is on the right.</summary>
  [Test]
  public void TheHeaderNamesTheResourceAndTheHardwareSeparately() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Disk — sda"));

    Assert.That(Heading(window).Text, Is.EqualTo("Disk"), "not the device — that is the model");
    Assert.That(Model(window).Text, Is.EqualTo("Fixture Disk"));
  }

  /// <summary>
  /// Moving from a section with fourteen rows to one with four must not leave the last ten painted
  /// under it.
  /// </summary>
  [Test]
  public void MovingToAShorterPageLeavesNothingBehind() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Processor"));
    Select(window, Titles(window).IndexOf("System"));

    Assert.That(LabelsAt(window, hardware: true), Does.Not.Contain("L3"));
    Assert.That(LabelsAt(window, hardware: false), Does.Not.Contain("Kernel time"));
  }

  #endregion

  [Test]
  public void SelectingAResourceShowsItsOwnFigures() {
    var (window, _, _) = Open();

    Select(window, Titles(window).IndexOf("Disk — sda"));
    var rows = Rows(window);

    Assert.That(rows, Does.Contain("Model"));
    Assert.That(rows, Does.Contain("Active time"));
    Assert.That(rows, Does.Not.Contain("L3"), "that belongs to the processor");
  }

  [Test]
  public void UpdatingManyTimesDoesNotThrow() {
    var (window, _, sampler) = Open();

    for (var i = 0; i < 20; ++i) {
      sampler.Sample();
      window.UpdateFromSample();
    }

    Assert.That(Titles(window), Is.Not.Empty);
  }

  #region every page draws something (PRD §45.1)

  /// <summary>
  /// Every graph on every page has a real size.
  /// </summary>
  /// <remarks>
  /// The bug this caught: the single plot — the one every page with one series uses, which is the
  /// processor, every disk and every adapter — was never given bounds, so it was laid out at nought
  /// by nought pixels. The page's whole top half was blank, and both the test suite and the capture
  /// log described it as one graph showing.
  /// </remarks>
  /// <summary>
  /// The top of a scale is labelled in the unit the series is measured in (PRD §45.4, §76).
  /// </summary>
  /// <remarks>
  /// Every ceiling that was not a percentage used to be printed as a quantity of bytes, so a card's
  /// power graph was labelled "130 B" two inches from a caption reading "15.6 W of 130.0 W". A test
  /// and not a screenshot, because the label is six characters in the corner of one plot of six.
  /// </remarks>
  [Test]
  public void AScaleIsLabelledInItsOwnUnit() {
    var probe = new StubProbe();
    probe.Gpus.Add(new(
      "card0",
      "Fixture GPU",
      "fixture",
      BusyPercent: Counter.Of(40),
      MemoryUsedBytes: Counter.Of(4ul * 1024 * 1024 * 1024),
      MemoryTotalBytes: Counter.Of(16ul * 1024 * 1024 * 1024),
      TemperatureMilliCelsius: Counter.Of(53_000),
      PowerMicrowatts: Counter.Of(15_600_000),
      PowerState: "D0",
      PowerLimitMicrowatts: Counter.Of(130_000_000),
      PowerCapMicrowatts: Counter.Unknown(UnknownReason.NotImplementedHere),
      MemoryBusyPercent: Counter.Of(10),
      CoreClockHertz: Counter.Unknown(UnknownReason.NotImplementedHere),
      MemoryClockHertz: Counter.Unknown(UnknownReason.NotImplementedHere),
      FanPercent: Counter.Unknown(UnknownReason.NotImplementedHere),
      FanRpm: Counter.Unknown(UnknownReason.NotImplementedHere),
      FanCount: Counter.Unknown(UnknownReason.NotImplementedHere),
      EncodePercent: Counter.Unknown(UnknownReason.NotImplementedHere),
      DecodePercent: Counter.Unknown(UnknownReason.NotImplementedHere)
    ));

    var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();
    var window = new PerformanceWindow(probe, sampler, openOnBusiest: false);
    Assert.That(window.Show("GPU — Fixture GPU"), Is.True);

    var labels = new List<string>();
    foreach (var control in window.Controls)
      if (control is HistoryPlot { Visible: true } plot)
        labels.Add(plot.ScaleLabel);

    Assert.That(labels, Does.Contain("100%"), "utilisation is a percentage");
    Assert.That(labels, Does.Contain("16.0G"), "the card's memory is a quantity of bytes");
    Assert.That(labels, Does.Contain("130.0 W"), "the power ceiling is watts, and was rendered as bytes");
    Assert.That(labels, Does.Contain("100 °C"), "a fixed hundred degrees is not a hundred percent");
  }

  [Test]
  public void EveryPagesGraphsHaveARealSize() {
    var (window, _, _) = Open();

    for (var i = 0; i < Rail(window).Items.Count; ++i) {
      // The two pages that measure nothing are meant to have none: see the test below.
      if (Titles(window)[i] is "System" or "Activity")
        continue;

      Select(window, i);
      var drawn = 0;
      foreach (var control in window.Controls) {
        if (control is not HistoryPlot { Visible: true } plot)
          continue;

        ++drawn;
        Assert.That(plot.Width, Is.GreaterThan(200), Titles(window)[i]);
        Assert.That(plot.Height, Is.GreaterThan(40), Titles(window)[i]);
      }

      Assert.That(drawn, Is.GreaterThan(0), $"{Titles(window)[i]} draws no graph at all");
    }
  }

  /// <summary>
  /// A section that measures nothing is a page of figures rather than a page with an empty graph on
  /// it, and it uses the room the graph would have taken (PRD §45.6).
  /// </summary>
  [Test]
  public void APageThatMeasuresNothingHasNoGraphAndNoGap() {
    var (window, _, _) = Open();

    Select(window, Titles(window).IndexOf("System"));
    Assert.That(PlotsShowing(window), Is.Zero);
    Assert.That(HeadingNamed(window, "Live").Y, Is.LessThan(200), "the figures start where the graph would have");
  }

  #endregion

  #region the fourth level, collapsed (PRD §45.2)

  [Test]
  public void TheEngineeringFiguresAreHiddenUntilTheyAreAskedFor() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Memory"));

    Assert.That(window.DiagnosticsOpen, Is.False);
    Assert.That(Rows(window), Does.Not.Contain("Page tables"));

    window.ToggleDiagnostics();
    Assert.That(Rows(window), Does.Contain("Page tables"));

    window.ToggleDiagnostics();
    Assert.That(Rows(window), Does.Not.Contain("Page tables"), "and closing it leaves nothing behind");
  }

  /// <summary>
  /// Compact is not merely smaller: it opens the fourth level, because somebody who asked for
  /// density asked to see more at once (PRD §45.7).
  /// </summary>
  [Test]
  public void CompactShowsMoreRatherThanTheSameThingSmaller() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Memory"));
    var comfortable = HeadingNamed(window, "Live").Y;

    window.SetDensity(compact: true);

    Assert.That(window.IsCompact, Is.True);
    Assert.That(window.DiagnosticsOpen, Is.True);
    Assert.That(HeadingNamed(window, "Live").Y, Is.Not.EqualTo(comfortable), "the rows tightened");
  }

  /// <summary>
  /// Nothing is dropped off the bottom of a column without a word.
  /// </summary>
  /// <remarks>
  /// The page used to hold twelve rows a column and simply stop: a memory page with fifteen live
  /// figures showed twelve of them, and the three it did not show looked exactly like three figures
  /// the machine had never reported. No screenshot of that is wrong-looking.
  /// </remarks>
  [Test]
  public void EveryLiveFigureIsOnThePage() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Memory"));

    var shown = Rows(window);
    foreach (var section in Query.PerformanceReport.Build(new() { HostName = "stub" }, Sampled()))
      if (section.Title == "Memory")
        foreach (var row in section.Rows)
          if (row.Level != Query.PerformanceRowLevel.Diagnostic)
            Assert.That(shown, Does.Contain(row.Label), row.Label);
  }

  private static SystemSnapshot Sampled() {
    var probe = new StubProbe();
    var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();
    return sampler.Current;
  }

  #endregion

  #region pausing and the span (PRD §45.4)

  /// <summary>
  /// Pause freezes the drawing without clearing the history or stopping collection, and says so.
  /// </summary>
  [Test]
  public void PauseFreezesTheDrawingAndNotTheCollection() {
    var (window, _, sampler) = Open();
    Select(window, Titles(window).IndexOf("Processor"));

    window.TogglePause();
    Assert.That(window.Paused, Is.True);

    var before = RowFor(window, "Processor").History!.Count;
    for (var i = 0; i < 3; ++i) {
      sampler.Sample();
      window.UpdateFromSample();
    }

    Assert.That(RowFor(window, "Processor").History!.Count, Is.GreaterThan(before), "collection carried on");

    var plot = PlotShowing(window)!;
    Assert.That(plot.Paused, Is.True, "and the plot says so rather than looking broken");
    Assert.That(plot.SkipNewest, Is.EqualTo(3), "the drawing stayed on the second it was paused on");

    window.TogglePause();
    Assert.That(PlotShowing(window)!.SkipNewest, Is.Zero);
  }

  /// <summary>
  /// One page, one time axis: the span reaches the rail's sparklines as well as the graphs, or the
  /// two draw different minutes of the same machine (PRD §45.1).
  /// </summary>
  [Test]
  public void TheSpanReachesTheSparklinesToo() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Processor"));

    window.SpanSeconds = 300;

    Assert.That(PlotShowing(window)!.SpanSeconds, Is.EqualTo(300));
    Assert.That(Rail(window).Samples, Is.EqualTo(300), "at a sample a second");
  }

  /// <summary>
  /// The copy §45.8 asks for is the figures, not a screenshot of them.
  /// </summary>
  [Test]
  public void TheCurrentValuesCopyAsText() {
    var (window, _, _) = Open();
    Select(window, Titles(window).IndexOf("Memory"));

    var text = window.CurrentValuesText();

    Assert.That(text, Does.StartWith("Memory"));
    Assert.That(text, Does.Contain("In use"));
    Assert.That(window.DiagnosticsText(), Does.Contain("Processor"), "and the whole machine is one call away");
  }

  #endregion

  #region reaching into the window

  private static List<string> Entries(PerformanceWindow window) {
    var rail = Rail(window);
    var entries = new List<string>();
    foreach (var item in rail.Items)
      entries.Add(item?.ToString() ?? string.Empty);

    return entries;
  }

  /// <summary>
  /// What each rail row is <em>about</em>, which is the section's full title — not what the row
  /// displays, which is deliberately shorter (§45.1).
  /// </summary>
  private static List<string> Titles(PerformanceWindow window) {
    var titles = new List<string>();
    foreach (var item in Rail(window).Items)
      if (item is ResourceRow row)
        titles.Add(row.Key);
      else {
        var entry = item?.ToString() ?? string.Empty;
        var gap = entry.IndexOf("   ", StringComparison.Ordinal);
        titles.Add(gap < 0 ? entry : entry[..gap]);
      }

    return titles;
  }

  /// <summary>What each rail row actually shows.</summary>
  private static List<string> Displayed(PerformanceWindow window) {
    var shown = new List<string>();
    foreach (var item in Rail(window).Items)
      if (item is ResourceRow row)
        shown.Add(row.Title);

    return shown;
  }

  private static CompositionBar Bar(PerformanceWindow window) {
    foreach (var control in window.Controls)
      if (control is CompositionBar bar)
        return bar;

    Assert.Fail("the window has no composition bar");
    return null!;
  }

  private static ResourceRail Rail(PerformanceWindow window) {
    foreach (var control in window.Controls)
      if (control is ResourceRail rail)
        return rail;

    Assert.Fail("the window has no rail");
    return null!;
  }

  private static ResourceRow RowFor(PerformanceWindow window, string title) {
    foreach (var item in Rail(window).Items)
      if (item is ResourceRow row && row.Key == title)
        return row;

    Assert.Fail($"no rail row for {title}");
    return null!;
  }

  private static int SelectedIndex(PerformanceWindow window) => Rail(window).SelectedIndex;

  /// <summary>
  /// The per-core switch, found by what it says rather than by being the first of its kind: the page
  /// has other boxes on it now, and an ordinal here would keep passing while reading another one.
  /// </summary>
  private static NativeForms.CheckBox Box(PerformanceWindow window) {
    foreach (var control in window.Controls)
      if (control is NativeForms.CheckBox box && box.Text.StartsWith("Per logical processor", StringComparison.Ordinal))
        return box;

    Assert.Fail("the window has no per-core box");
    return null!;
  }

  private static void Toggle(PerformanceWindow window, bool on) => Box(window).Checked = on;

  private static HistoryPlot? PlotShowing(PerformanceWindow window) {
    foreach (var control in window.Controls)
      if (control is HistoryPlot { Visible: true } plot)
        return plot;

    return null;
  }

  private static int PlotsShowing(PerformanceWindow window) {
    var showing = 0;
    foreach (var control in window.Controls)
      if (control is HistoryPlot { Visible: true })
        ++showing;

    return showing;
  }

  private static void Select(PerformanceWindow window, int index) => Rail(window).SelectedIndex = index;

  private static NativeForms.Label Heading(PerformanceWindow window) => LabelAt(window, 0);

  private static NativeForms.Label Model(PerformanceWindow window) => LabelAt(window, 1);

  private static NativeForms.Label LabelAt(PerformanceWindow window, int index) {
    var seen = 0;
    foreach (var control in window.Controls)
      if (control is NativeForms.Label label && seen++ == index)
        return label;

    Assert.Fail($"no label {index}");
    return null!;
  }

  /// <summary>
  /// The labels in one of the two statistic columns, told apart by which side of the page they are
  /// on — which is the only thing a reader can go by either.
  /// </summary>
  private static List<string> LabelsAt(PerformanceWindow window, bool hardware) {
    // The two column headings are the only fixed points, and they move with the layout — a magic
    // number here would keep passing while quietly reading the wrong column.
    var live = HeadingNamed(window, "Live");
    var specs = HeadingNamed(window, "Hardware");
    var labels = new List<string>();
    foreach (var control in window.Controls) {
      if (control is not NativeForms.Label label || label.Text.Length == 0 || label.Bounds.Y <= live.Y)
        continue;

      var column = specs is { Width: > 0 } && label.Bounds.X >= specs.X;
      if (column == hardware)
        labels.Add(label.Text);
    }

    return labels;
  }

  private static Rectangle HeadingNamed(PerformanceWindow window, string text) {
    foreach (var control in window.Controls)
      if (control is NativeForms.Label label && label.Text == text)
        return label.Bounds;

    return default;
  }

  /// <summary>The labels of the figures currently shown, blank ones left out.</summary>
  private static List<string> Rows(PerformanceWindow window) {
    var rows = new List<string>();
    foreach (var control in window.Controls)
      if (control is NativeForms.Label label && label.Text.Length > 0)
        rows.Add(label.Text);

    return rows;
  }

  #endregion

}
