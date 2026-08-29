using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The two tabs §26 said were "one call away rather than a feature away" (PRD §26, §35, §63).
/// </summary>
/// <remarks>
/// <para>
/// Strings scans the image <b>on disk</b> and never the process's memory. §25.5 records why: both
/// <c>process_vm_readv</c> and <c>/proc/[pid]/mem</c> are governed by <c>PTRACE_MODE_ATTACH</c>,
/// which Yama declines by default for anything this program did not start, and a memory
/// reverse-engineering suite is §4's first non-goal.
/// </para>
/// <para>
/// Timeline filters the one shared ring rather than recording its own. Two logs of the same machine
/// would have two bounds, two start times and two chances to disagree about what happened.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PropertiesStringsAndTimelineTests {

  /// <summary>A probe that answers nothing: none of this reads the machine.</summary>
  private sealed class SilentProbe : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public MemoryMapReading GetMemoryRegions(ProcessKey key) => MemoryMapReading.NotImplemented;
    public ProcessSecurity? DescribeSecurity(ProcessKey key) => null;
    public CgroupInfo? DescribeCgroup(ProcessKey key) => null;
    public ImageInfo? DescribeImage(ProcessKey key) => null;
    public void Dispose() { }
  }

  /// <summary>One process, optionally with an image on disk to scan.</summary>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta, ProcessRow Row, ProcessKey Key) Machine(
    string? imagePath = null
  ) {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(4242, 99);
    records[0].Name = "subject";
    records[0].ImagePath = imagePath;
    records[0].HandleCount = Counter.NotSampledYet;

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var row = new ProcessRow(records[0].Key);
    row.Update(in snapshot.Processes[0], delta, 0, Counter.NotSupported, currentUserId: 1000);
    return (snapshot, delta, row, records[0].Key);
  }

  private static ProcessPropertiesWindow Window(out ProcessKey key) {
    key = new(4242, 99);
    return new(new SilentProbe(), key, "subject");
  }

  [Test]
  public void BothTabsArePresent() {
    var window = Window(out _);

    Assert.That(window.TabTitles, Does.Contain("Strings"));
    Assert.That(window.TabTitles, Does.Contain("Timeline"));
  }

  /// <summary>
  /// A process with no readable image gets a sentence rather than an empty table. An empty list and
  /// a list this user may not read look identical from here, and only one of the two is worth acting
  /// on (PRD §5.3, §72.3).
  /// </summary>
  [Test]
  public void AnImageNobodyCanNameSaysSoRatherThanShowingNothing() {
    var window = Window(out _);
    window.ShowPage("Strings");

    Assert.That(window.StringsHeading, Does.Contain("no image path").IgnoreCase);
  }

  /// <summary>
  /// And a real file is scanned. Scanned once: the bytes of a file on disk do not change under a
  /// process that has it mapped, and if the file is replaced the mapping is still the old one — which
  /// is the Modules page's thing to say, not this one's.
  /// </summary>
  [Test]
  public void ARealImageIsScannedAndTheRunsAppear() {
    var directory = Path.Combine(Path.GetTempPath(), $"procman-strings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "subject.bin");
    try {
      File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(
        "\0\0\0HelloFromTheImage\0\0AnotherReadableRun\0"
      ));

      var (snapshot, delta, row, key) = Machine(path);
      var window = new ProcessPropertiesWindow(new SilentProbe(), key, "subject");
      window.UpdateFromSample(snapshot, delta, row, Counter.NotSupported);
      window.ShowPage("Strings");

      var text = string.Join("\n", window.StringsRows);
      Assert.That(window.StringsRows, Is.Not.Empty, "a file with text in it has runs");
      Assert.That(text, Does.Contain("HelloFromTheImage"));
      Assert.That(text, Does.Contain("AnotherReadableRun"));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>
  /// With no ring the page says there is nothing recording, rather than showing an empty list that
  /// reads as "nothing has happened".
  /// </summary>
  [Test]
  public void WithNoRingTheTimelineSaysThereIsNone() {
    var window = Window(out _);
    window.ShowPage("Timeline");

    Assert.That(window.TimelineHeading, Does.Contain("nothing is recording").IgnoreCase);
  }

  /// <summary>
  /// An empty ring is a different sentence again: something is recording and this process has been
  /// quiet. Three states and three sentences, because the first two are indistinguishable from a
  /// blank table and mean opposite things.
  /// </summary>
  [Test]
  public void AnEmptyRingSaysNothingHasBeenRecordedYet() {
    var window = Window(out _);
    window.Timeline = new();
    window.ShowPage("Timeline");

    Assert.That(window.TimelineHeading, Does.Contain("recorded yet").IgnoreCase);
    Assert.That(window.TimelineRows, Is.Empty);
  }

  /// <summary>Only this process's entries, and the count says how many of the whole it is.</summary>
  [Test]
  public void OnlyTheEventsAboutThisProcessAppear() {
    var window = Window(out var key);
    var log = new EventLog();
    log.Record(1000, EventCategory.Lifecycle, "subject started", key.Pid);
    log.Record(2000, EventCategory.Lifecycle, "something else started", key.Pid + 1);
    log.Record(3000, EventCategory.Threshold, "subject went over 80%", key.Pid);
    window.Timeline = log;
    window.ShowPage("Timeline");

    Assert.That(window.TimelineRows, Has.Count.EqualTo(2));
    Assert.That(window.TimelineHeading, Does.Contain("2 of 3"));
    Assert.That(string.Join("\n", window.TimelineRows), Does.Not.Contain("something else"));
  }

  /// <summary>
  /// Newest first. A page opened because something has just happened should not need scrolling to
  /// the bottom to find it.
  /// </summary>
  [Test]
  public void TheNewestEventIsAtTheTop() {
    var window = Window(out var key);
    var log = new EventLog();
    log.Record(1000, EventCategory.Lifecycle, "the older one", key.Pid);
    log.Record(2000, EventCategory.Threshold, "the newer one", key.Pid);
    window.Timeline = log;
    window.ShowPage("Timeline");

    Assert.That(window.TimelineRows[0], Does.Contain("the newer one"));
  }

  /// <summary>
  /// A process with events recorded about others, and none of its own, is told that rather than
  /// being shown a table indistinguishable from a broken recorder.
  /// </summary>
  [Test]
  public void AQuietProcessAmongNoisyOnesIsToldWhichItIs() {
    var window = Window(out var key);
    var log = new EventLog();
    log.Record(1000, EventCategory.Lifecycle, "somebody else", key.Pid + 1);
    window.Timeline = log;
    window.ShowPage("Timeline");

    Assert.That(window.TimelineRows, Is.Empty);
    Assert.That(window.TimelineHeading, Does.Contain("about others"));
  }

  /// <summary>The page refreshes on the tick, or it would freeze the moment it was opened.</summary>
  [Test]
  public void TheTimelineFollowsTheTick() {
    var window = Window(out var key);
    var log = new EventLog();
    window.Timeline = log;
    window.ShowPage("Timeline");
    Assert.That(window.TimelineRows, Is.Empty);

    log.Record(1000, EventCategory.Lifecycle, "it happened", key.Pid);
    var (snapshot, delta, row, _) = Machine();
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSupported);

    Assert.That(window.TimelineRows, Has.Count.EqualTo(1));
  }

}
