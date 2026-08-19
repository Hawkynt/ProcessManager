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

    public void Sample(SystemSnapshot snapshot) {
      snapshot.PrepareProcesses(0);
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

  private static List<string> Titles(PerformanceWindow window) {
    var titles = new List<string>();
    foreach (var entry in Entries(window)) {
      var gap = entry.IndexOf("   ", StringComparison.Ordinal);
      titles.Add(gap < 0 ? entry : entry[..gap]);
    }

    return titles;
  }

  private static NativeForms.ListBox Rail(PerformanceWindow window) {
    foreach (var control in window.Controls)
      if (control is NativeForms.ListBox rail)
        return rail;

    Assert.Fail("the window has no rail");
    return null!;
  }

  private static int SelectedIndex(PerformanceWindow window) => Rail(window).SelectedIndex;

  private static void Select(PerformanceWindow window, int index) => Rail(window).SelectedIndex = index;

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
