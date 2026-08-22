using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
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
    /// <summary>What the unit-file walk of §41 produced, or nothing on a machine with no systemd.</summary>
    public IReadOnlyList<ServiceRecord> Services { get; init; } = [];

    public IReadOnlyList<ServiceRecord> GetServices() => this.Services;
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    /// <summary>
    /// What the on-demand pages of §34, §36 and §38 get.
    /// </summary>
    /// <remarks>
    /// The defaults are what a platform that reads none of them honestly knows, which is the case
    /// most of these tests are about: a page must say which kind of nothing it is looking at rather
    /// than coming up empty (PRD §5.3).
    /// </remarks>
    public MemoryMapReading Map { get; init; } = MemoryMapReading.NotImplemented;

    public ProcessSecurity? Security { get; init; }

    public CgroupInfo? Cgroup { get; init; }

    public ImageInfo? Image { get; init; }

    public MemoryMapReading GetMemoryRegions(ProcessKey key) => this.Map;

    public ProcessSecurity? DescribeSecurity(ProcessKey key) => this.Security;

    public CgroupInfo? DescribeCgroup(ProcessKey key) => this.Cgroup;

    public ImageInfo? DescribeImage(ProcessKey key) => this.Image;

    public void Dispose() { }
  }

  /// <param name="startTicks">Part of the identity: the same pid started twice is two processes.</param>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta, ProcessRow Row, ProcessKey Key) Machine(
    int pid = 4242,
    ulong startTicks = 100,
    string name = "editor",
    string? cgroup = null
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
    records[0].ContainerPath = cgroup;

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
    // The three §26 asked for and did not have: the address space, what confines the process, and
    // the ceilings that belong to its group rather than to it (PRD §34, §36, §38).
    Assert.That(window.TabTitles, Is.SupersetOf(new[] { "Memory map", "Security", "cgroup" }));
    // And which service the process belongs to, which §41 had only in the command line (PRD §26).
    Assert.That(window.TabTitles, Does.Contain("Services"));
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
  /// <remarks>
  /// The package and the digest are the same silence and get the same treatment: a row saying the
  /// question has not been asked, and where the button that asks it is. Each costs the size of the
  /// image or a walk of every installed package, so neither is paid for on opening (PRD §5.2, §5.4,
  /// §27).
  /// </remarks>
  [Test]
  public void TheGeneralPageSaysWhichQuestionsAboutTheImageItHasNotAsked() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.Multiple(() => {
      foreach (var (label, said) in (ReadOnlySpan<(string, string)>)[
        ("Signature", "not checked"),
        ("Package", "not looked up"),
        ("Image hash", "not computed"),
      ]) {
        Assert.That(window.GeneralText, Does.Contain(label), label);
        Assert.That(window.GeneralText, Does.Contain(said), label);
      }

      // And each names the way to the answer rather than merely refusing.
      Assert.That(window.GeneralText, Does.Contain("File properties…"));
    });
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
  /// And every one of them is a field the catalogue says an hour is kept of.
  /// </summary>
  /// <remarks>
  /// The page draws six plots over eight series and the catalogue declares which fields are kept
  /// per process (PRD §5.1). Two statements about one thing, so a test holds them to each other: a
  /// seventh plot added without declaring its field — or a field declared historical that nothing
  /// keeps — fails here rather than leaving the catalogue describing a program that no longer
  /// exists.
  /// </remarks>
  [Test]
  public void EveryPlottedSeriesIsAFieldTheCatalogueKeepsPerProcess() {
    var declared = new List<ProcessField>();
    foreach (var descriptor in FieldRegistry.All)
      if (descriptor.History.HasFlag(FieldHistory.Process))
        declared.Add(descriptor.Id);

    Assert.That(ProcessPropertiesWindow.PlottedFields, Is.EquivalentTo(declared));
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

    foreach (var raw in window.PerformanceText.Split('\n')) {
      // Trimmed, because the text is built with AppendLine: on Windows that ends every line with a
      // carriage return, and splitting on the newline alone leaves it on the end of the placeholder.
      // The assertion below is about the last visible character, not the last byte.
      var line = raw.TrimEnd();
      if (!line.Contains("Descriptors", StringComparison.Ordinal))
        continue;

      // A placeholder — this stub cannot count them at all — and never a number. Which placeholder
      // is the probe's business; that it is one rather than a nought is this window's.
      Assert.That(
        line.EndsWith("…", StringComparison.Ordinal) || line.EndsWith("n/a", StringComparison.Ordinal),
        Is.True,
        $"nobody counted them, and that is not the same as none: {line}"
      );
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
  /// A window with a few samples in it and its graphs given a size, so the cursor has somewhere to
  /// land. An unsized plot is a plot nothing can be pointed at, and the readings would be empty for
  /// a reason that has nothing to do with the gesture.
  /// </summary>
  private static ProcessPropertiesWindow WindowWithHistory(int samples = 30) {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    foreach (var plot in window.PerformancePlots)
      plot.Bounds = new(0, 0, 240, 96);

    for (var i = 0; i < samples; ++i)
      window.UpdateFromSample(snapshot, delta, row, Counter.Of(42));

    return window;
  }

  /// <summary>
  /// Pointing at a graph reports what it was doing at that moment, and says which moment (PRD §28).
  /// </summary>
  [Test]
  public void PointingAtAGraphReportsTheReadingAndTheMomentItIsFrom() {
    var window = WindowWithHistory();
    var plot = window.PerformancePlots[0];

    plot.PointAt(plot.Width - 1);
    Assert.That(plot.HoverText, Does.Contain("now"), "the newest sample is the right-hand edge");

    plot.PointAt(plot.Width / 2);
    Assert.That(plot.HoverText, Does.Contain("s ago"), "and everything left of it is older");

    // The footer echoes it, so the gesture is discoverable on a page of six graphs.
    Assert.That(window.PerformanceFooter, Does.Contain(plot.Caption));
    Assert.That(window.PerformanceFooter, Does.Contain("s ago"));
  }

  /// <summary>
  /// And the arrow keys reach the same readings, which is the whole of "keyboard-accessible point
  /// inspection" — including the footer, which followed the mouse only and went on reporting
  /// wherever the pointer had last been while the keyboard moved the cursor (PRD §28, §45.9).
  /// </summary>
  [Test]
  public void TheArrowKeysWalkTheCursorAndTheFooterFollowsThem() {
    var window = WindowWithHistory();
    var plot = window.PerformancePlots[0];

    Assert.That(plot.TabStop, Is.True, "Tab cannot reach a graph it does not stop at");

    plot.MoveCursor(-1);
    Assert.That(plot.HoverText, Is.Not.Empty, "the first arrow key starts at the newest sample");
    var first = window.PerformanceFooter;
    Assert.That(first, Does.Contain(plot.Caption));

    for (var i = 0; i < 20; ++i)
      plot.MoveCursor(-1);

    Assert.That(window.PerformanceFooter, Is.Not.EqualTo(first), "the footer did not follow the keyboard");
    Assert.That(window.PerformanceFooter, Does.Contain("s ago"));
  }

  /// <summary>
  /// Past the history the machine has, the cursor reports an absence rather than a nought: a graph
  /// that has been running for ten minutes has nothing to say about the fifty before them, and
  /// drawing that as idle is the same lie as any other confident zero (PRD §72.3).
  /// </summary>
  [Test]
  public void APointBeforeTheHistoryStartsIsNotAReadingOfNought() {
    var window = WindowWithHistory(samples: 3);
    window.SetSpan(3600);
    var plot = window.PerformancePlots[0];

    plot.PointAt(0);
    Assert.That(plot.HoverText, Does.Not.Contain("0.0 %"));
    Assert.That(plot.HoverText, Does.Contain("…").Or.Contain("n/a"));
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

  #region the Security page (PRD §36)

  [Test]
  public void TheSecurityPageShowsWhoTheProcessIsAndWhatItMay() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(
      new StubProbe {
        Security = new("unconfined", UnknownReason.None, [new(998, "wheel"), new(1000, null)], UnknownReason.None),
        Image = new(
          "/usr/bin/editor",
          "x86-64",
          HeaderRead: true,
          Bits: 64,
          IsPositionIndependent: true,
          Interpreter: "/lib64/ld-linux-x86-64.so.2",
          SizeBytes: Counter.Of(1024ul),
          ModifiedUtc: null,
          WorkingDirectory: "/home/alice",
          Namespaces: [new("pid", "4026531836"), new("net", "4026531833")]
        ),
      },
      key,
      row.Name
    );

    // Shown first: nothing is collected for a tab nobody has opened, which is the discipline the
    // pane's own tabs follow and the reason the label and the group list are not read on every tick
    // of every open window (PRD §5.4).
    window.ShowPage("Security");
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.Multiple(() => {
      Assert.That(window.SecurityText, Does.Contain("alice"));
      Assert.That(window.SecurityText, Does.Contain("unconfined"), "what AppArmor says is an answer, not a blank");
      // The number always and the name where this machine's own file has one, which is what a group
      // from a directory service does not (PRD §5.3).
      Assert.That(window.SecurityText, Does.Contain("wheel (998)"));
      Assert.That(window.SecurityText, Does.Contain("1000"));
      // Where a container actually is: two processes sharing an inode share that namespace, which is
      // a harder fact than a cgroup path anybody may write (PRD §14).
      Assert.That(window.SecurityText, Does.Contain("Namespace, pid"));
      Assert.That(window.SecurityText, Does.Contain("4026531836"));
    });
  }

  /// <summary>
  /// A kernel with no security module fails the read outright rather than producing an empty file,
  /// so a blank row here would read as a process nothing is confining — which is a claim, and one
  /// this program must never make out of an absence (PRD §70, §72.3).
  /// </summary>
  [Test]
  public void NoSecurityModuleIsSaidRatherThanLeftBlank() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(
      new StubProbe { Security = new(null, UnknownReason.NotSupportedOnPlatform, [], UnknownReason.None) },
      key,
      row.Name
    );

    window.ShowPage("Security");
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.Multiple(() => {
      Assert.That(window.SecurityText, Does.Contain("Security module"));
      Assert.That(window.SecurityText, Does.Contain("no SELinux or AppArmor"));
      // And a process in no supplementary group says so, which every kernel thread is.
      Assert.That(window.SecurityText, Does.Contain("Supplementary groups: none"));
    });
  }

  [Test]
  public void ARefusedLabelIsNotReportedAsNoLabel() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(
      new StubProbe { Security = new(null, UnknownReason.NotPermitted, [], UnknownReason.NotPermitted) },
      key,
      row.Name
    );

    window.ShowPage("Security");
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.SecurityText, Does.Contain("not readable as this user"));
    Assert.That(window.SecurityText, Does.Not.Contain("no SELinux"));
  }

  #endregion

  #region the cgroup page (PRD §38)

  [Test]
  public void TheCgroupPageSaysWhatTheGroupAllows() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(
      new StubProbe {
        Cgroup = new(
          "/system.slice/indexer.service",
          ["cpu", "memory", "pids"],
          MemoryCurrentBytes: Counter.Of(64ul * 1024 * 1024),
          MemoryMaxBytes: Counter.Of(256ul * 1024 * 1024),
          // What memory.high reads when the controller is on and nobody set a soft cap: the literal
          // word "max", which the reader turns into NoLimit. NotSupported is what an absent file
          // means, and a cgroup with the memory controller enabled always has the file — so a stub
          // saying NotSupported here was describing a state the kernel does not produce.
          MemoryHighBytes: Counter.Unknown(UnknownReason.NoLimit),
          PidsCurrent: Counter.Of(12),
          PidsMax: Counter.Of(100),
          CpuQuotaCores: 0.5,
          ThrottledCount: Counter.Of(37),
          CpuPressure: PressureReading.Unknown,
          MemoryPressure: PressureReading.Unknown,
          IoPressure: PressureReading.Unknown,
          Freezer: new(Supported: true, Frozen: true)
        ),
      },
      key,
      row.Name
    );

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.Multiple(() => {
      Assert.That(window.CgroupText, Does.Contain("/system.slice/indexer.service"));
      // A quota as a number of cores, because "0.5 cores" is a sentence and "50000 100000" is not.
      Assert.That(window.CgroupText, Does.Contain("0.5 cores"));
      Assert.That(window.CgroupText, Does.Contain("37"), "how often it has actually been held back");
      // Unlimited is not a quantity, and it must not look like one.
      Assert.That(window.CgroupText, Does.Contain("Memory, soft cap: no limit"));
      // Nothing in a process table will say this: there is no process state for frozen.
      Assert.That(window.CgroupText, Does.Contain("Frozen: yes"));
      // pids.current counts threads. A row headed "processes" would have been wrong by whatever
      // factor the group's processes happen to be threaded (PRD §5.3).
      Assert.That(window.CgroupText, Does.Contain("Tasks: 12"));
      Assert.That(window.CgroupText, Does.Contain("a task is a thread"));
      Assert.That(window.CgroupText, Does.Not.Contain("Processes:"));
    });
  }

  /// <summary>
  /// A controller that is off is not a limit that is absent.
  /// </summary>
  /// <remarks>
  /// The reader cannot tell a missing limit file from the literal word <c>max</c> — both arrive as no
  /// value — so where the controller is switched off, "no limit" would be an outright false
  /// statement: an ancestor's quota still applies, and that is the case somebody opens this page to
  /// find (PRD §38).
  /// </remarks>
  [Test]
  public void ADisabledControllerIsNotReportedAsNoLimit() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(
      new StubProbe {
        Cgroup = new(
          "/user.slice/user-1000.slice/session.scope",
          ["memory"],
          MemoryCurrentBytes: Counter.Of(1024ul),
          // The memory controller is the one that is on here, so its files exist and read "max".
          MemoryMaxBytes: Counter.Unknown(UnknownReason.NoLimit),
          MemoryHighBytes: Counter.Unknown(UnknownReason.NoLimit),
          PidsCurrent: Counter.Of(3),
          PidsMax: Counter.NotSupported,
          CpuQuotaCores: null,
          ThrottledCount: Counter.NotSupported,
          CpuPressure: PressureReading.Unknown,
          MemoryPressure: PressureReading.Unknown,
          IoPressure: PressureReading.Unknown,
          Freezer: new(Supported: false, Frozen: false)
        ),
      },
      key,
      row.Name
    );

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.Multiple(() => {
      Assert.That(window.CgroupText, Does.Contain("the cpu controller is not enabled here"));
      Assert.That(window.CgroupText, Does.Contain("the pids controller is not enabled here"));
      // The one that is enabled still reports the absence of a limit as an absence of a limit.
      Assert.That(window.CgroupText, Does.Contain("Memory, hard cap: no limit"));
      Assert.That(window.CgroupText, Does.Contain("this kernel's cgroups have no freezer"));
    });
  }

  [Test]
  public void AProcessInNoReadableCgroupIsToldWhy() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.CgroupText, Does.Contain("cgroup v1"));
    Assert.That(window.TabTitles, Does.Contain("cgroup"), "disabled is the default, so the tab stays and explains");
  }

  [Test]
  public void TheCgroupTabCanBeAskedToGoAway() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name, null, UnavailableTabs.Hidden);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.TabTitles, Does.Not.Contain("cgroup"));
  }

  #endregion

  #region the Services page (PRD §41)

  private static ServiceRecord Unit(
    string name = "indexer.service",
    int mainPid = 4242,
    ServiceState state = ServiceState.Running,
    bool? enabled = true,
    bool masked = false
  ) => new(
    name,
    "Indexes things",
    state,
    enabled,
    masked,
    mainPid,
    "/usr/bin/indexer --daemon",
    "/usr/lib/systemd/system/" + name,
    "on-failure"
  );

  /// <summary>Opens the window on the Services page, which is what fills it.</summary>
  private static ProcessPropertiesWindow OnServices(StubProbe probe, string? cgroup) {
    var (snapshot, delta, row, key) = Machine(cgroup: cgroup);
    var window = new ProcessPropertiesWindow(probe, key, row.Name);
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);
    window.ShowPage("Services");
    return window;
  }

  [Test]
  public void TheServicePageSaysWhatTheUnitFileSays() {
    var window = OnServices(new() { Services = [Unit()] }, "/system.slice/indexer.service");

    Assert.Multiple(() => {
      Assert.That(window.ServicesText, Does.Contain("Service: indexer.service"));
      Assert.That(window.ServicesText, Does.Contain("Indexes things"));
      Assert.That(window.ServicesText, Does.Contain("State: running"));
      Assert.That(window.ServicesText, Does.Contain("Starts at boot: yes"));
      Assert.That(window.ServicesText, Does.Contain("/usr/lib/systemd/system/indexer.service"));
      Assert.That(window.ServicesText, Does.Contain("on-failure"));
    });
  }

  /// <summary>
  /// The distinction the page is worth opening for. A unit's main process is the one systemd watches
  /// and restarts; everything else in the cgroup is a child it will take down with it.
  /// </summary>
  [Test]
  public void ItSaysWhetherThisProcessIsTheOneSystemdWatches() {
    var main = OnServices(new() { Services = [Unit(mainPid: 4242)] }, "/system.slice/indexer.service");
    var child = OnServices(new() { Services = [Unit(mainPid: 11)] }, "/system.slice/indexer.service");

    Assert.That(main.ServicesText, Does.Contain("4242 — this process"));
    Assert.That(child.ServicesText, Does.Contain("not the one systemd watches"));
  }

  /// <summary>
  /// The innermost unit, which is the whole subtlety of the join. A desktop application sits inside
  /// its user's session manager, which is itself a unit, and naming the outer one would report every
  /// program somebody starts as belonging to the manager that started it.
  /// </summary>
  [Test]
  public void TheInnermostUnitWinsOverTheSessionManagerAroundIt() {
    var window = OnServices(
      new() { Services = [Unit("app-firefox.scope"), Unit("user@1000.service")] },
      "/user.slice/user-1000.slice/user@1000.service/app.slice/app-firefox.scope"
    );

    Assert.That(window.ServicesText, Does.Contain("app-firefox.scope"));
    Assert.That(window.ServicesText, Does.Not.Contain("user@1000.service"));
  }

  /// <summary>
  /// Most of a desktop is in no unit at all, and that is a finding about the process rather than a
  /// hole — so the page says it, names the cgroup it looked in, and keeps its tab.
  /// </summary>
  [Test]
  public void AProcessUnderNoUnitIsToldSoAndKeepsItsTab() {
    var window = OnServices(new() { Services = [Unit()] }, "/user.slice/user-1000.slice");

    Assert.That(window.ServicesText, Does.Contain("under no systemd unit"));
    Assert.That(window.ServicesText, Does.Contain("/user.slice/user-1000.slice"));
    Assert.That(window.TabTitles, Does.Contain("Services"));
  }

  /// <summary>
  /// A slice is a unit to systemd and is deliberately not one here: it holds no processes of its own,
  /// so naming it as the owner would name a container rather than an owner (PRD §40).
  /// </summary>
  [Test]
  public void ASliceIsNotAnOwner() {
    var window = OnServices(new() { Services = [Unit("user.slice")] }, "/user.slice");

    Assert.That(window.ServicesText, Does.Contain("under no systemd unit"));
  }

  /// <summary>
  /// The cgroup names a unit the walk of the unit files did not produce — a transient scope, made at
  /// runtime and never written to disk. The name is still the truth about the process, so it is
  /// reported and the absence of a file is explained.
  /// </summary>
  [Test]
  public void AUnitWithNoFileOnDiskIsStillNamed() {
    var window = OnServices(new() { Services = [Unit()] }, "/user.slice/session-3.scope");

    Assert.That(window.ServicesText, Does.Contain("session-3.scope"));
    Assert.That(window.ServicesText, Does.Contain("transient"));
  }

  /// <summary>
  /// Three answers and not two. A unit started only by a socket or a timer is neither enabled nor
  /// disabled in the sense the row means, and "no" would be wrong about a service that starts every
  /// time (PRD §72.3).
  /// </summary>
  [Test]
  public void NeitherEnabledNorDisabledIsItsOwnAnswer() {
    var window = OnServices(new() { Services = [Unit(enabled: null)] }, "/system.slice/indexer.service");

    Assert.That(window.ServicesText, Does.Contain("neither"));
    Assert.That(window.ServicesText, Does.Contain("socket"));
  }

  /// <summary>
  /// Masked is its own row rather than a shade of disabled: a masked unit can never run whatever else
  /// is configured, and it is the setting people forget they made.
  /// </summary>
  [Test]
  public void MaskedIsSaidSeparatelyFromDisabled() {
    var window = OnServices(new() { Services = [Unit(enabled: false, masked: true)] }, "/system.slice/indexer.service");

    Assert.That(window.ServicesText, Does.Contain("Starts at boot: no"));
    Assert.That(window.ServicesText, Does.Contain("Masked: yes"));
  }

  /// <summary>
  /// No service manager this build reads is a fact about the machine and not about the process, which
  /// is the one case the tab may go — and the only case, so that "this process is in no unit" never
  /// looks like "this build cannot tell you".
  /// </summary>
  [Test]
  public void AMachineWithNoServiceManagerSaysSoAndMayHideTheTab() {
    var shown = OnServices(new(), "/system.slice/indexer.service");
    Assert.That(shown.ServicesText, Does.Contain("Only systemd is read"));
    Assert.That(shown.TabTitles, Does.Contain("Services"), "disabled is the default");

    var (snapshot, delta, row, key) = Machine(cgroup: "/system.slice/indexer.service");
    var hidden = new ProcessPropertiesWindow(new StubProbe(), key, row.Name, null, UnavailableTabs.Hidden);
    hidden.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);
    hidden.ShowPage("Services");

    Assert.That(hidden.TabTitles, Does.Not.Contain("Services"));
  }

  /// <summary>
  /// The page is read once and kept, so it must not be read before there is anything to read it by.
  /// Opened inside the first tick it had no cgroup yet and would have latched "its cgroup could not
  /// be read" for the rest of the window's life.
  /// </summary>
  [Test]
  public void OpeningItBeforeTheFirstSampleDoesNotLatchTheWrongAnswer() {
    var (snapshot, delta, row, key) = Machine(cgroup: "/system.slice/indexer.service");
    var probe = new StubProbe { Services = [Unit()] };
    var window = new ProcessPropertiesWindow(probe, key, row.Name);

    // Quicker than the tick, which is a thing a person can be.
    window.ShowPage("Services");
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.ServicesText, Does.Contain("indexer.service"));
    Assert.That(window.ServicesText, Does.Not.Contain("could not be read"));
  }

  /// <summary>
  /// The row §27 named as the one thing on the General page that could be answered and was not. It
  /// costs nothing — the cgroup is already in the sample, and a systemd unit is a cgroup.
  /// </summary>
  [Test]
  public void TheGeneralPageNamesTheServiceTheProcessBelongsTo() {
    var (snapshot, delta, row, key) = Machine(cgroup: "/system.slice/indexer.service");
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.GeneralText, Does.Contain("Service: indexer.service"));
  }

  [Test]
  public void TheGeneralPageSaysWhenThereIsNoServiceRatherThanLeavingItBlank() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.GeneralText, Does.Contain("Service: none"));
  }

  #endregion

  #region the Memory map page (PRD §34)

  [Test]
  public void TheMemoryMapListsWhatIsMappedAndSaysWhatItAddsUpTo() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(
      new StubProbe {
        Map = new(MemoryMapState.Available, true, [
          Region(0x400000, 0x402000, MapPermissions.Read | MapPermissions.Execute | MapPermissions.Private, MemoryRegionKind.FileBacked, "/usr/bin/editor", 8192),
          Region(0x600000, 0x621000, MapPermissions.Read | MapPermissions.Write | MapPermissions.Private, MemoryRegionKind.Heap, "[heap]", 4096),
        ]),
      },
      key,
      row.Name
    );

    window.ShowPage("Memory map");
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.Multiple(() => {
      Assert.That(window.MemoryMapRows, Is.EqualTo(2));
      Assert.That(window.MemoryMapHeading, Does.Contain("2 mappings"));
      // Reserved and resident are never added together: a process reserves gigabytes it has never
      // touched, and the gap between the two is why "virtual size" answers nothing (PRD §17).
      Assert.That(window.MemoryMapHeading, Does.Contain("of address space"));
      Assert.That(window.MemoryMapHeading, Does.Contain("resident"));
    });
  }

  /// <summary>
  /// Nought mappings means two different things and the page has to say which.
  /// </summary>
  /// <remarks>
  /// A kernel thread has no address space of its own; another user's process has one and the kernel
  /// will not show it. Both arrive as an empty list, and only one of them is a fact about the process
  /// (PRD §5.3, §72.3).
  /// </remarks>
  [Test]
  public void AnEmptyMapSaysWhichKindOfEmptyItIs() {
    var (snapshot, delta, row, key) = Machine();
    var refused = new ProcessPropertiesWindow(
      new StubProbe { Map = new(MemoryMapState.NotPermitted, false, []) },
      key,
      row.Name
    );

    refused.ShowPage("Memory map");
    refused.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    var empty = new ProcessPropertiesWindow(
      new StubProbe { Map = new(MemoryMapState.Available, true, []) },
      key,
      row.Name
    );

    empty.ShowPage("Memory map");
    empty.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.Multiple(() => {
      Assert.That(refused.MemoryMapRows, Is.Zero);
      Assert.That(refused.MemoryMapHeading, Does.Contain("attaching a debugger"));
      Assert.That(empty.MemoryMapRows, Is.Zero);
      Assert.That(empty.MemoryMapHeading, Does.Contain("kernel thread"));
    });
  }

  /// <summary>
  /// Half an answer says so at the top rather than showing a column of dashes ten thousand times.
  /// </summary>
  [Test]
  public void AMapWithoutItsCountersSaysSoOnce() {
    var (snapshot, delta, row, key) = Machine();
    var window = new ProcessPropertiesWindow(
      new StubProbe {
        Map = new(MemoryMapState.Available, false, [
          Region(0x400000, 0x402000, MapPermissions.Read | MapPermissions.Private, MemoryRegionKind.FileBacked, "/usr/bin/editor", null),
        ]),
      },
      key,
      row.Name
    );

    window.ShowPage("Memory map");
    window.UpdateFromSample(snapshot, delta, row, Counter.NotSampledYet);

    Assert.That(window.MemoryMapHeading, Does.Contain("page tables"));
    Assert.That(window.MemoryMapHeading, Does.Not.Contain("resident"));
  }

  private static MemoryRegionRecord Region(
    ulong start,
    ulong end,
    MapPermissions permissions,
    MemoryRegionKind kind,
    string? path,
    ulong? resident
  ) {
    var value = resident is { } bytes ? Counter.Of(bytes) : Counter.NotPermitted;
    return new(
      start,
      end,
      end - start,
      permissions,
      kind,
      path,
      IsDeleted: false,
      FileOffset: Counter.Of(0ul),
      Inode: Counter.Of(0ul),
      Device: "00:00",
      ResidentBytes: value,
      ProportionalBytes: value,
      PrivateDirtyBytes: value,
      SharedDirtyBytes: value,
      AnonymousBytes: value,
      SwapBytes: value,
      LockedBytes: value,
      HugePageBytes: value,
      Flags: null
    );
  }

  #endregion

}
