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

    Select(window, Titles(window).IndexOf("Memory"));

    Assert.That(PlotsShowing(window), Is.EqualTo(1));
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

  private static NativeForms.CheckBox Box(PerformanceWindow window) {
    foreach (var control in window.Controls)
      if (control is NativeForms.CheckBox box)
        return box;

    Assert.Fail("the window has no per-core box");
    return null!;
  }

  private static void Toggle(PerformanceWindow window, bool on) => Box(window).Checked = on;

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
    var middle = 230 + 24 + 330;
    var labels = new List<string>();
    foreach (var control in window.Controls)
      if (control is NativeForms.Label label && label.Text.Length > 0 && label.Bounds.Y > 280
          && (label.Bounds.X >= middle) == hardware)
        labels.Add(label.Text);

    return labels;
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
