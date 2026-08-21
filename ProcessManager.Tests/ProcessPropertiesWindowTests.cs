using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// A process in a window of its own (PRD §26), and what happens to it when that process ends.
/// </summary>
[TestFixture]
public sealed class ProcessPropertiesWindowTests {

  private sealed class StubProbe : ISystemProbe {
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

    public void Dispose() { }
  }

  /// <param name="startTicks">Part of the identity: the same pid started twice is two processes.</param>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta, ProcessRow Row, ProcessKey Key) Machine(
    int pid = 4242,
    ulong startTicks = 100,
    string name = "editor"
  ) {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(pid, startTicks);
    records[0].Name = name;
    records[0].UserName = "alice";
    records[0].PrivateBytes = Counter.Of(1024);
    records[0].ThreadCount = 7;
    // Explicitly nobody-has-looked, not default(Counter): the whole point of the assertion below is
    // that an uncounted tally must not render as a tally of nought.
    records[0].HandleCount = Counter.NotSampledYet;
    // A machine with no per-process graphics accounting, said the way the probes say it — every one
    // of them, not the interesting-looking few. Left at default each is a confident zero, reason
    // None, "the value is present": the encode percentage alone was enough to make the delta report
    // that this process was using nought per cent of an engine, which is a measurement, not an
    // absence, and it made the tab look available on a machine with no graphics accounting at all.
    records[0].GpuBusyPercent = Counter.NotSupported;
    records[0].GpuEncodePercent = Counter.NotSupported;
    records[0].GpuDecodePercent = Counter.NotSupported;
    records[0].GpuGraphicsNs = Counter.NotSupported;
    records[0].GpuComputeNs = Counter.NotSupported;
    records[0].GpuCopyNs = Counter.NotSupported;
    records[0].GpuEncodeNs = Counter.NotSupported;
    records[0].GpuDecodeNs = Counter.NotSupported;
    records[0].GpuDedicatedBytes = Counter.NotSupported;
    records[0].GpuSharedBytes = Counter.NotSupported;
    records[0].GpuAdapterReason = UnknownReason.NotSupportedOnPlatform;
    records[0].ImagePath = "/usr/bin/" + name;
    records[0].CommandLine = "/usr/bin/" + name + " --file notes.txt";

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var row = new ProcessRow(records[0].Key);
    row.Update(in snapshot.Processes[0], delta, 0, Counter.NotSupported, currentUserId: 1000);
    return (snapshot, delta, row, records[0].Key);
  }

  /// <summary>An empty machine, for the "the process has gone" cases.</summary>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Nothing() {
    var snapshot = new SystemSnapshot();
    snapshot.PrepareProcesses(0);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    return (snapshot, delta);
  }

  [Test]
  public void TheWindowIsTitledForItsProcess() {
    var (_, _, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    Assert.That(window.Text, Is.EqualTo("editor (4242)"));
    Assert.That(window.Key, Is.EqualTo(key));
    Assert.That(window.Ended, Is.False);
  }

  [Test]
  public void ItFollowsItsProcessWhileThatProcessLives() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);
    Assert.That(window.Ended, Is.False);
    Assert.That(window.Text, Is.EqualTo("editor (4242)"));
  }

  /// <summary>
  /// The window stays open and says so rather than closing under somebody who is reading it — the
  /// lists keep whatever they last held, which is usually why it was open (PRD §86).
  /// </summary>
  [Test]
  public void WhenTheProcessEndsTheWindowSaysSoAndStays() {
    var (_, _, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    var (empty, emptyDelta) = Nothing();
    window.UpdateFromSample(empty, emptyDelta, null, Counter.NotSampledYet);

    Assert.That(window.Ended, Is.True);
    Assert.That(window.Text, Does.EndWith("— ended"));
  }

  /// <summary>
  /// The case the whole identity pair exists for: the pid is back, as somebody else's process. A
  /// window that followed the number would quietly start describing a stranger (PRD §72.2).
  /// </summary>
  [Test]
  public void ItDoesNotFollowARecycledPid() {
    var (_, _, row, key) = Machine(pid: 4242, startTicks: 100, name: "editor");
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    // Same pid, started later: a different process by every definition the engine uses.
    var (reused, reusedDelta, otherRow, _) = Machine(pid: 4242, startTicks: 900, name: "something-else");
    window.UpdateFromSample(reused, reusedDelta, otherRow, Counter.NotSampledYet);

    Assert.That(window.Ended, Is.True, "the original process is gone");
    Assert.That(window.Text, Does.StartWith("editor"), "and the window still names the one it was opened for");
  }

  [Test]
  public void OnceEndedItStopsAskingAboutThePid() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    var (empty, emptyDelta) = Nothing();
    window.UpdateFromSample(empty, emptyDelta, null, Counter.NotSampledYet);

    // Even handed the process back, it stays ended: the pid may be anybody's by now.
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);
    Assert.That(window.Ended, Is.True);
    Assert.That(window.Text, Does.EndWith("— ended"));
  }

  #region the pages of its own (PRD §26)

  /// <summary>
  /// The seam this window is built over: its pages go onto the pane's own tab strip, which it can
  /// only reach by knowing what the pane hands out. If that ever stops being a tab control the
  /// window would come up with the shared pane and none of §26's pages, and nothing but this would
  /// notice.
  /// </summary>
  [Test]
  public void EveryPageItPromisesIsOnTheStrip() {
    var (_, _, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    Assert.That(window.TabTitles, Is.SupersetOf(new[] { "General", "Performance", "CPU", "Memory", "I/O" }));
    // And the pane's own, which is the whole reason the pages were added to its strip rather than
    // to a second one.
    Assert.That(window.TabTitles, Is.SupersetOf(new[] { "Overview", "Threads", "Modules", "Handles" }));
  }

  [Test]
  public void TheGeneralPageDescribesTheProcess() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.GeneralText, Does.Contain("editor"));
    Assert.That(window.GeneralText, Does.Contain("4242"));
    Assert.That(window.GeneralText, Does.Contain("notes.txt"), "the command line is the point of the page");
    Assert.That(window.GeneralText, Does.Contain("Running for"));
  }

  /// <summary>
  /// A properties window that checked no signature must not read as one that checked and was happy.
  /// A blank row is exactly that, so the page says which it is (PRD §70).
  /// </summary>
  [Test]
  public void TheGeneralPageSaysNoSignatureWasChecked() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.GeneralText, Does.Contain("Signature"));
    Assert.That(window.GeneralText, Does.Contain("not read"));
  }

  /// <summary>
  /// Every resource §28 names is on the page. The sizes the plots end up at are a question only a
  /// photograph can answer — there is no display here — which is why the same text is written into
  /// the capture log, where a plot at nought by nought is visible without one (PRD §9.6).
  /// </summary>
  [Test]
  public void EveryGraphTheSpecificationNamesIsOnThePage() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    foreach (var caption in new[] { "CPU", "Memory", "Disk", "GPU", "Descriptors", "Threads" })
      Assert.That(window.PerformanceText, Does.Contain(caption), caption);
  }

  /// <summary>
  /// The trap this project keeps walking into. A dictionary miss leaves <c>default(Counter)</c>
  /// behind, whose reason is "the value is present" — so a window handed one would draw a graph of a
  /// process holding no descriptors at all, confidently, at zero.
  /// </summary>
  [Test]
  public void AnUncountedDescriptorTallyIsNotADescriptorTallyOfNought() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    foreach (var line in window.PerformanceText.Split('\n')) {
      if (!line.Contains("Descriptors", StringComparison.Ordinal))
        continue;

      Assert.That(line, Does.EndWith("…"), "nobody has counted them, and that is not the same as none");
    }
  }

  [Test]
  public void TheAxisTakesTheFourWindowsOfTheSpecification() {
    var (_, _, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    Assert.That(window.SpanSeconds, Is.EqualTo(60), "it opens on the shortest one");
    foreach (var seconds in new[] { 60, 300, 900, 3600 }) {
      window.SetSpan(seconds);
      Assert.That(window.SpanSeconds, Is.EqualTo(seconds));
    }
  }

  /// <summary>
  /// A machine whose driver says nothing about per-process graphics use keeps the tab and explains
  /// itself, which is what "disabled" means here.
  /// </summary>
  [Test]
  public void AnUnavailableTabStaysAndSaysWhyByDefault() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.Unavailable, Is.EqualTo(UnavailableTabs.Disabled));
    Assert.That(window.TabTitles, Does.Contain("GPU"));
  }

  /// <summary>And the other preference takes it off the strip, which answers the other question.</summary>
  [Test]
  public void AnUnavailableTabCanBeAskedToGoAway() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name, null, UnavailableTabs.Hidden);

    Assert.That(window.TabTitles, Does.Contain("GPU"), "until a sample says whether there is anything on it");
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.TabTitles, Does.Not.Contain("GPU"));
  }

  #endregion

}
