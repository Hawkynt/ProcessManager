using System.Diagnostics;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What happens at sizes no desktop reaches but a build server does (PRD §99).
/// </summary>
/// <remarks>
/// <para>
/// The interesting failures here are not slow ones, they are quadratic ones: an algorithm that costs
/// nothing at four hundred processes and stalls the interface at ten thousand. A machine with ten
/// thousand processes is ordinary on a busy build host, and the whole promise of §4 is a sample that
/// fits in its budget on one.
/// </para>
/// <para>
/// The bounds below are deliberately loose — several times the measured cost — because a tight
/// timing assertion on a shared machine is a test that fails for reasons that have nothing to do
/// with the code. They are here to catch a change of complexity, not a change of speed.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ScaleTests {

  /// <summary>
  /// Builds a snapshot of <paramref name="count"/> processes, with parentage chosen by the caller so
  /// each test can pick the tree shape that hurts.
  /// </summary>
  private static SystemSnapshot Snapshot(int count, Func<int, int> parentOf) {
    var snapshot = new SystemSnapshot { TimestampTicks = Stopwatch.Frequency };
    var processes = snapshot.PrepareProcesses(count);
    for (var i = 0; i < count; ++i) {
      processes[i] = default;
      processes[i].Key = new(i + 1, 1000);
      processes[i].ParentPid = parentOf(i);
      processes[i].Name = "p";
      processes[i].UserId = 1000;
      processes[i].CpuTimeNs = Counter.Of((ulong)i);
      processes[i].WorkingSetBytes = Counter.Of((ulong)(count - i));
    }

    snapshot.System.TotalMemoryBytes = Counter.Of(64UL * 1024 * 1024 * 1024);
    return snapshot;
  }

  private static SnapshotDelta Delta(SystemSnapshot snapshot) {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    before.PrepareProcesses(0);
    var delta = new SnapshotDelta();
    delta.Update(before, snapshot, CpuPercentMode.Normalized);
    return delta;
  }

  /// <summary>How long one rebuild takes, after a warm one that is not counted.</summary>
  /// <summary>
  /// How long one rebuild takes, at the machine's best rather than at whatever it was doing.
  /// </summary>
  /// <remarks>
  /// The best of several, for the reason <see cref="TimeUpdate"/> gives: a single measurement on a
  /// shared build runner measures the runner's mood, and a ratio between two unrelated moments is
  /// not a measurement of anything. This one has been reported flaky twice. The best case is the
  /// right one to take, because what these tests look for is a quadratic — and a quadratic is slow
  /// at the machine's most favourable moment too.
  /// </remarks>
  private static TimeSpan TimeRebuild(ProcessView view, SystemSnapshot snapshot, SnapshotDelta delta) {
    view.Rebuild(snapshot, delta);

    var best = TimeSpan.MaxValue;
    for (var run = 0; run < 5; ++run) {
      var clock = Stopwatch.StartNew();
      view.Rebuild(snapshot, delta);
      clock.Stop();
      if (clock.Elapsed < best)
        best = clock.Elapsed;
    }

    TestContext.Out.WriteLine(
      $"{snapshot.ProcessCount} processes, tree={view.TreeMode}: {best.TotalMilliseconds:0.0} ms, best of five"
    );

    return best;
  }

  #region ten thousand processes

  [Test]
  public void TenThousandProcessesFlatten() {
    var snapshot = Snapshot(10_000, i => i == 0 ? 0 : 1);
    var view = new ProcessView { TreeMode = false, SortColumn = ProcessField.WorkingSetBytes };

    var elapsed = TimeRebuild(view, snapshot, Delta(snapshot));

    Assert.That(view.RowCount, Is.EqualTo(10_000));
    Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
  }

  [Test]
  public void TenThousandProcessesNest() {
    var snapshot = Snapshot(10_000, i => i == 0 ? 0 : 1);
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.WorkingSetBytes };

    var elapsed = TimeRebuild(view, snapshot, Delta(snapshot));

    Assert.That(view.RowCount, Is.EqualTo(10_000), "every process is reachable from the root");
    Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
  }

  /// <summary>
  /// Ten thousand siblings under one parent, which is the shape a build host actually has: one make
  /// or one container runtime with an enormous fan-out. The child index has to be a slice rather
  /// than a scan for this to finish at all.
  /// </summary>
  [Test]
  public void OneParentWithTenThousandChildren() {
    var snapshot = Snapshot(10_000, i => i == 0 ? 0 : 1);
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.Pid, SortDescending = false };

    var elapsed = TimeRebuild(view, snapshot, Delta(snapshot));

    Assert.That(view.Rows[0].Depth, Is.Zero);
    Assert.That(view.Rows[1].Depth, Is.EqualTo(1));
    Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
  }

  /// <summary>
  /// The shape that finds a quadratic ancestor walk: every process is the child of the one before
  /// it, so the chain is as long as the list. Nothing on a real machine is ten thousand deep, but
  /// nothing stops it either, and the cost of checking for a cycle must not depend on the depth
  /// twice over.
  /// </summary>
  [Test]
  public void ATenThousandDeepChainIsNotQuadratic() {
    var snapshot = Snapshot(10_000, i => i);
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.Pid, SortDescending = false };

    var elapsed = TimeRebuild(view, snapshot, Delta(snapshot));

    Assert.That(view.RowCount, Is.EqualTo(10_000));
    Assert.That(view.Rows[9_999].Depth, Is.EqualTo(9_999), "the chain is as deep as it is long");
    Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
  }

  /// <summary>
  /// The same chain, twice the length. A linear rebuild takes about twice as long; a quadratic one
  /// takes four times, and that ratio is what this measures rather than any absolute figure.
  /// </summary>
  [Test]
  public void DoublingTheDepthDoesNotQuadrupleTheCost() {
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.Pid, SortDescending = false };

    var small = Snapshot(10_000, i => i);
    var large = Snapshot(20_000, i => i);
    var shortRun = TimeRebuild(view, small, Delta(small));
    var longRun = TimeRebuild(view, large, Delta(large));

    // Three, not two: measurement noise on a shared machine is real, and the failure this guards
    // against is a factor of four or worse.
    Assert.That(
      longRun.TotalMilliseconds,
      Is.LessThan(Math.Max(shortRun.TotalMilliseconds * 3, 20)),
      $"{shortRun.TotalMilliseconds:0.0} ms then {longRun.TotalMilliseconds:0.0} ms"
    );
  }

  /// <summary>
  /// A cycle across a namespace boundary has been seen in the wild. At this size, cutting it has to
  /// stay affordable — and the walk must still terminate, which is the point.
  /// </summary>
  [Test]
  public void ATenThousandLongCycleIsCutRatherThanFollowedForever() {
    // Every process's parent is the next one, and the last one's parent is the first.
    var snapshot = Snapshot(10_000, i => i == 9_999 ? 1 : i + 2);
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.Pid, SortDescending = false };

    var elapsed = TimeRebuild(view, snapshot, Delta(snapshot));

    Assert.That(view.RowCount, Is.GreaterThan(0), "the cycle was broken, not followed");
    Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
  }

  #endregion

  #region churn

  /// <summary>
  /// Processes appearing and disappearing between every sample, which is what a shell script in a
  /// loop does to a process table. The rebuild must not accumulate anything across samples — the
  /// buffers are reused deliberately, and a leak here is a leak per second.
  /// </summary>
  [Test]
  public void RapidChurnDoesNotAccumulate() {
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.CpuPercent };
    var previous = new SystemSnapshot { TimestampTicks = 0 };
    previous.PrepareProcesses(0);

    // Warm every buffer to its final size first, so what is measured is the steady state rather
    // than the growth that §4 permits once.
    for (var round = 0; round < 3; ++round) {
      var warm = Snapshot(2_000, i => i == 0 ? 0 : 1);
      var delta = new SnapshotDelta();
      delta.Update(previous, warm, CpuPercentMode.Normalized);
      view.Rebuild(warm, delta);
      previous = warm;
    }

    var total = 0L;
    for (var round = 0; round < 20; ++round) {
      // Half the table is replaced each round: pids that were there are gone, and pids that were
      // not have appeared. Building the snapshot allocates, and that is this test's own doing, so
      // only the rebuild is measured.
      var next = Snapshot(2_000, i => i == 0 ? 0 : 1);
      var records = next.PrepareProcesses(2_000);
      for (var i = 1_000; i < 2_000; ++i)
        records[i].Key = new(100_000 + round * 1_000 + i, 1000);

      var delta = new SnapshotDelta();
      delta.Update(previous, next, CpuPercentMode.Normalized);

      var before = GC.GetAllocatedBytesForCurrentThread();
      view.Rebuild(next, delta);
      total += GC.GetAllocatedBytesForCurrentThread() - before;
      previous = next;
    }

    var perRound = total / 20;
    TestContext.Out.WriteLine($"{perRound} bytes a round");
    // Every buffer the rebuild uses was sized in the warm-up above, so a steady-state sample should
    // be allocating essentially nothing however much the table churns (PRD §4).
    Assert.That(perRound, Is.LessThan(4_096));
  }

  /// <summary>
  /// A selection has to survive the table changing underneath it, or the row somebody is about to
  /// act on is not the row they are looking at (PRD §7.3).
  /// </summary>
  [Test]
  public void ASelectionSurvivesChurn() {
    var view = new ProcessView { TreeMode = false, SortColumn = ProcessField.WorkingSetBytes };
    var first = Snapshot(2_000, i => i == 0 ? 0 : 1);
    view.Rebuild(first, Delta(first));

    var chosen = first.Processes[1_234].Key;
    Assert.That(view.FindRow(chosen), Is.GreaterThanOrEqualTo(0));

    // Everything around it changes; it does not.
    var second = Snapshot(2_000, i => i == 0 ? 0 : 1);
    var records = second.PrepareProcesses(2_000);
    for (var i = 0; i < 2_000; ++i)
      if (i != 1_234)
        records[i].WorkingSetBytes = Counter.Of((ulong)(i * 7 % 2_000));

    view.Rebuild(second, Delta(second));

    var row = view.FindRow(chosen);
    Assert.That(row, Is.GreaterThanOrEqualTo(0), "the selected process is still findable");
    Assert.That(second.Processes[view.Rows[row].Index].Key, Is.EqualTo(chosen), "and it is the same one");
  }

  /// <summary>
  /// A pid that has been reused inside one sample interval must not carry the old process's row.
  /// This is the failure that ends the wrong program, and identity is the whole defence (§72.2).
  /// </summary>
  [Test]
  public void AReusedPidIsNotTheSameRow() {
    var view = new ProcessView { TreeMode = false, SortColumn = ProcessField.Pid };
    var first = Snapshot(1_000, i => i == 0 ? 0 : 1);
    view.Rebuild(first, Delta(first));

    var gone = first.Processes[500].Key;

    var second = Snapshot(1_000, i => i == 0 ? 0 : 1);
    var records = second.PrepareProcesses(1_000);
    // Same pid, started later: a different process wearing the same number.
    records[500].Key = new(gone.Pid, gone.StartTicks + 5_000);

    view.Rebuild(second, Delta(second));

    Assert.That(view.FindRow(gone), Is.EqualTo(-1), "the process that held that pid is gone");
    Assert.That(view.FindRow(records[500].Key), Is.GreaterThanOrEqualTo(0), "the one that holds it now is there");
  }

  #endregion

  #region filtering and sorting at size

  /// <summary>
  /// Filtering in tree mode promotes the ancestors of every match, which is a second walk over the
  /// table. At ten thousand rows with almost every row matching, that walk is the expensive case.
  /// </summary>
  [Test]
  public void FilteringTenThousandRowsInTreeModeStaysAffordable() {
    var snapshot = Snapshot(10_000, i => i);
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.Pid, TextFilter = "p" };

    var elapsed = TimeRebuild(view, snapshot, Delta(snapshot));

    Assert.That(view.RowCount, Is.EqualTo(10_000), "they all match");
    Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
  }

  /// <summary>
  /// Sorting by every field in turn, at size. A field whose comparison is accidentally expensive —
  /// one that formats a string to compare it, say — shows up here and nowhere else.
  /// </summary>
  [Test]
  public void EveryFieldCanSortTenThousandRows() {
    var snapshot = Snapshot(10_000, i => i == 0 ? 0 : 1);
    var delta = Delta(snapshot);
    var view = new ProcessView { TreeMode = false };

    foreach (var field in Enum.GetValues<ProcessField>()) {
      view.SortColumn = field;
      var clock = Stopwatch.StartNew();
      view.Rebuild(snapshot, delta);
      clock.Stop();

      Assert.That(view.RowCount, Is.EqualTo(10_000), field.ToString());
      Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), $"sorting by {field} took too long");
    }
  }

  #endregion

  #region a million resource rows

  /// <summary>
  /// A probe with as many descriptors as the test asks for, and nothing else. The names are shared
  /// instances deliberately: a million distinct strings would measure the allocator rather than the
  /// search.
  /// </summary>
  private sealed class CrowdedProbe : ISystemProbe {

    private readonly HandleRecord[] _handles;

    public CrowdedProbe(int handlesPerProcess) {
      var names = new string[16];
      for (var i = 0; i < names.Length; ++i)
        names[i] = $"/var/lib/thing/{i}/data.db";

      this._handles = new HandleRecord[handlesPerProcess];
      for (var i = 0; i < handlesPerProcess; ++i)
        this._handles[i] = new(
          (ulong)i,
          HandleKind.File,
          names[i % names.Length],
          "read/write",
          Counter.NotSupported,
          Counter.NotSupported,
          Counter.Of((ulong)i),
          Counter.NotSupported,
          Counter.NotSupported,
          null,
          null,
          null,
          FileNodeType.Unknown,
          null
        );

      // One of them, once, is the needle.
      if (handlesPerProcess > 0)
        this._handles[handlesPerProcess / 2] = this._handles[handlesPerProcess / 2] with { Name = "/var/lib/needle.sock" };
    }

    public int DeepReads { get; private set; }

    public string Description => "crowded";

    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) {
      ++this.DeepReads;
      return this._handles;
    }

    /// <summary>As many modules as the test asked for, and none unless it did.</summary>
    public ModuleRecord[] Modules { get; init; } = [];

    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => this.Modules;
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }

  }

  /// <summary>
  /// A thousand processes holding a thousand descriptors each, which is what "who has this file
  /// open" costs on a machine with a database on it. The search is the one place in the program that
  /// deliberately reads everything, so it is the one place where a million rows is the ordinary case
  /// rather than the pathological one (PRD §33).
  /// </summary>
  [Test]
  public void AMillionResourceRowsCanBeSearched() {
    var snapshot = Snapshot(1_000, i => i == 0 ? 0 : 1);
    var probe = new CrowdedProbe(1_000);

    var clock = Stopwatch.StartNew();
    var matches = ResourceSearch.Find(probe, snapshot, "needle");
    clock.Stop();
    TestContext.Out.WriteLine($"1 000 000 rows searched in {clock.Elapsed.TotalMilliseconds:0} ms");

    Assert.That(matches, Has.Count.EqualTo(1_000), "every process holds one");
    Assert.That(probe.DeepReads, Is.EqualTo(1_000), "each process read once, not once per pattern");
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)));
  }

  /// <summary>
  /// The shallow search must not touch a descriptor at all. This is the rule that keeps the process
  /// table affordable, and the only way to see it is to count the reads (PRD §5.4).
  /// </summary>
  [Test]
  public void AShallowSearchReadsNoDescriptorsAtAnySize() {
    var snapshot = Snapshot(1_000, i => i == 0 ? 0 : 1);
    var probe = new CrowdedProbe(1_000);

    ResourceSearch.Find(probe, snapshot, "needle", deep: false);

    Assert.That(probe.DeepReads, Is.Zero);
  }

  /// <summary>Counting a million descriptors is one pass, and must stay one pass.</summary>
  [Test]
  public void AMillionDescriptorsCanBeCounted() {
    var handles = new HandleRecord[1_000_000];
    for (var i = 0; i < handles.Length; ++i)
      handles[i] = new(
        (ulong)i,
        (HandleKind)(i % 8),
        null,
        null,
        Counter.NotSupported,
        Counter.NotSupported,
        Counter.NotSupported,
        Counter.NotSupported,
        Counter.NotSupported,
        null,
        null,
        null,
        FileNodeType.Unknown,
        null
      );

    var clock = Stopwatch.StartNew();
    var tally = HandleTally.From(handles);
    clock.Stop();
    TestContext.Out.WriteLine($"1 000 000 descriptors tallied in {clock.Elapsed.TotalMilliseconds:0} ms");

    Assert.That(tally.Total, Is.EqualTo(1_000_000));
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
  }

  #endregion

  #region what the front-ends do with the rows (PRD §71.5)

  /// <summary>
  /// The window's own per-refresh work at ten thousand processes.
  /// </summary>
  /// <remarks>
  /// <para>
  /// §71.5 is about the front-ends and not about <see cref="ProcessView"/>, which the tests above
  /// already take to ten thousand. The window keeps a row object per process and refreshes every
  /// field on it once a sample, so this is the number that decides whether the promise holds: the
  /// binder's four passes over the row list, plus one
  /// <see cref="ProcessManager.Ui.Desktop.ProcessRow"/> update per process, and each of those
  /// formats every field in the catalogue.
  /// </para>
  /// <para>
  /// The ceiling is a second because that is what a refresh has: at ten thousand processes a window
  /// that cannot finish a refresh in one sample interval is a window that never finishes one. It is
  /// not the target — the target is §71.4's hundred milliseconds — and the gap between the two is
  /// recorded in §71.5 rather than hidden by a looser assertion here.
  /// </para>
  /// </remarks>
  [Test]
  public void TheWindowsRowsSurviveTenThousandProcesses() {
    var snapshot = Snapshot(10_000, i => i == 0 ? 0 : 1);
    var delta = Delta(snapshot);
    var view = new ProcessView { TreeMode = false, SortColumn = ProcessField.WorkingSetBytes };
    view.Rebuild(snapshot, delta);

    var tree = new Hawkynt.NativeForms.TreeListView();
    var binder = new Ui.Desktop.ProcessTreeBinder(tree);

    // One sync to build the nodes, which is start-up rather than steady state, then the one that is
    // measured: a refresh of a table that is already there.
    binder.Sync(snapshot, delta, view);

    var clock = Stopwatch.StartNew();
    binder.Sync(snapshot, delta, view);
    clock.Stop();
    TestContext.Out.WriteLine($"desktop binder, 10 000 rows: {clock.Elapsed.TotalMilliseconds:0} ms a refresh");

    Assert.That(tree.Nodes, Has.Count.EqualTo(10_000), "every process has a row");
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
  }

  /// <summary>
  /// And the terminal's, which keeps no row objects at all: it formats the cells inside the viewport
  /// and nothing else, so its per-frame cost is the size of the screen rather than the size of the
  /// machine. What it does pay per sample is the rebuild, measured above, and the history rings for
  /// the rows around the viewport — bounded by the caller and asserted here, because an unbounded
  /// call is how the window's own equivalent came to keep a ring for every row on the machine.
  /// </summary>
  [Test]
  public void TheTerminalsHistoryFollowsTheViewportAndNotTheTable() {
    var snapshot = Snapshot(10_000, i => i == 0 ? 0 : 1);
    var delta = Delta(snapshot);
    var view = new ProcessView { TreeMode = false, SortColumn = ProcessField.WorkingSetBytes };
    view.Rebuild(snapshot, delta);

    var history = new ProcessHistory();
    const int Viewport = 48;

    var clock = Stopwatch.StartNew();
    history.Update(snapshot, delta, view, 0, Viewport);
    clock.Stop();
    TestContext.Out.WriteLine($"terminal history, {Viewport} of 10 000 rows: {clock.Elapsed.TotalMilliseconds:0.00} ms");

    // The rows on screen have a ring; the ten thousandth does not, and that is the whole point.
    Assert.That(history.Get(snapshot.Processes[view.Rows[0].Index].Key, HistorySeries.Cpu), Is.Not.Null);
    Assert.That(history.Get(snapshot.Processes[view.Rows[9_999].Index].Key, HistorySeries.Cpu), Is.Null);
    Assert.That(history.Count, Is.EqualTo(Viewport), "one ring a row on screen, and not one a process");
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(200)));
  }

  /// <summary>
  /// A hundred thousand module rows, which is the size §71.5 names and which one process really can
  /// reach: a browser with every tab's mappings, or anything that has been given a few thousand
  /// plugins. The table is built whole and then drawn a screen at a time, so what is measured is the
  /// building — the drawing is bounded by the terminal, not by this.
  /// </summary>
  [Test]
  public void AHundredThousandModuleRowsCanBeTabulated() {
    var modules = new ModuleRecord[100_000];
    for (var i = 0; i < modules.Length; ++i)
      modules[i] = new(
        "/usr/lib/libthing.so." + (i % 64).ToString(System.Globalization.CultureInfo.InvariantCulture),
        (ulong)(0x7f0000000000 + (i * 0x1000)),
        0x1000,
        "r-xp",
        (ulong)(0x7f0000000000 + (i * 0x1000) + 0x1000),
        Counter.Of(4096ul),
        Counter.Of(0ul),
        Counter.Of((ulong)i),
        "8:1",
        false,
        1,
        Counter.Of(65536ul),
        0,
        ModuleType.SharedObject,
        "x86-64",
        Counter.NotSupported,
        null,
        null,
        ImageMitigations.None,
        null,
        ModuleLoadReason.Unknown,
        1,
        ModuleRuntime.Unknown
      );

    var probe = new CrowdedProbe(0) { Modules = modules };

    var clock = Stopwatch.StartNew();
    var table = ProcessDetailTables.Modules(probe, new(1, 1000));
    clock.Stop();
    TestContext.Out.WriteLine($"100 000 module rows tabulated in {clock.Elapsed.TotalMilliseconds:0} ms");

    Assert.That(table.Rows, Has.Count.EqualTo(100_000));
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
  }

  /// <summary>
  /// And a hundred thousand handle rows through the same tabulator, which is the other half of
  /// §71.5's second line. Its formatter is the dearest of the detail tables — seven cells, three of
  /// them derived — so it is the one that would show a per-row cost that is not linear.
  /// </summary>
  [Test]
  public void AHundredThousandHandleRowsCanBeTabulated() {
    var probe = new CrowdedProbe(100_000);

    var clock = Stopwatch.StartNew();
    var table = ProcessDetailTables.Handles(probe, new(1, 1000));
    clock.Stop();
    TestContext.Out.WriteLine($"100 000 handle rows tabulated in {clock.Elapsed.TotalMilliseconds:0} ms");

    Assert.That(table.Rows, Has.Count.EqualTo(100_000));
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
  }

  #endregion

  #region a hundred thousand threads (PRD §29, §99)

  /// <summary>
  /// A hundred thousand threads, as records rather than as a recorded <c>/proc</c> tree.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Deliberately not a fixture. The Linux probe reads five files per thread, so a recorded tree at
  /// this size would be half a million files checked into the repository and copied on every build,
  /// and what it would measure is the filesystem the tests happen to run on. The layer that actually
  /// has to survive the size is above the probe: §29 says the thread tab is re-read on <em>every</em>
  /// tick while it is open, so <see cref="ThreadDelta"/> runs a hundred thousand dictionary
  /// operations a second on a machine like this, and it is the one thing here whose complexity a
  /// change could quietly ruin.
  /// </para>
  /// <para>
  /// A hundred thousand is not hypothetical: the kernel's default <c>threads-max</c> on a machine
  /// with this much memory is well above it, and a JVM or a Go runtime that has lost control of a
  /// pool gets there.
  /// </para>
  /// </remarks>
  private static ThreadRecord[] Threads(int count, ulong cpuNs, int firstTid = 1) {
    var threads = new ThreadRecord[count];
    for (var i = 0; i < count; ++i)
      threads[i] = new(
        firstTid + i,
        ProcessState.Running,
        Counter.Of(cpuNs + (ulong)i),
        StartTimeUtcTicks: 1,
        Counter.NotSupported,
        null,
        20,
        "worker",
        Counter.Of(cpuNs),
        Counter.Of(0ul),
        Counter.Of((ulong)i),
        LastCpu: 0,
        null,
        Counter.NotSupported,
        Counter.NotSupported,
        BasePriority: 0,
        SchedulingPolicy.Other,
        null,
        null,
        Counter.NotSupported,
        null,
        Counter.NotSupported,
        Counter.NotSupported,
        ThreadMode.Unknown,
        Counter.NotSupported,
        Counter.NotSupported
      );

    return threads;
  }

  private static readonly ProcessKey _Swarm = new(4242, 99);

  /// <summary>How long one update takes, after a warm one that is not counted.</summary>
  /// <summary>
  /// How long one update takes, at the machine's best rather than at whatever it happened to be
  /// doing.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The best of several runs, because a single measurement on a shared build runner measures the
  /// runner's mood. This test failed on the macOS leg and passed on every other, with nothing in the
  /// change touching threads: the small figure landed in a quiet window and the large one did not,
  /// and the ratio between two unrelated moments is not a measurement of anything.
  /// </para>
  /// <para>
  /// The best case is the right one to take here, and not a compromise for the sake of a green
  /// suite. What this is looking for is a lookup that has become a scan, and a scan is slow in the
  /// best case too — quadratic growth is a hundredfold and survives being measured at the machine's
  /// most favourable moment. What does not survive it is the scheduler taking the thread away once.
  /// </para>
  /// </remarks>
  private static TimeSpan TimeUpdate(ThreadDelta delta, IReadOnlyList<ThreadRecord> threads) {
    // Once to grow every buffer, so what follows is the steady state rather than the growth §71.3
    // permits once.
    delta.Update(_Swarm, threads, 16);

    var best = TimeSpan.MaxValue;
    for (var run = 0; run < 5; ++run) {
      var clock = Stopwatch.StartNew();
      delta.Update(_Swarm, threads, 16);
      clock.Stop();
      if (clock.Elapsed < best)
        best = clock.Elapsed;
    }

    TestContext.Out.WriteLine($"{threads.Count} threads: {best.TotalMilliseconds:0.0} ms, best of five");
    return best;
  }

  [Test]
  public void AHundredThousandThreadsAreRatedInBudget() {
    var delta = new ThreadDelta();
    var threads = Threads(100_000, 1_000_000);

    Assert.That(TimeUpdate(delta, threads), Is.LessThan(TimeSpan.FromSeconds(1)));

    // And every one of them has a rate, from the first index to the last. An off-by-one in the
    // buffer growth would leave the tail reading "not sampled yet" for ever, which looks exactly
    // like a thread that has only just appeared.
    Assert.That(delta.HasPrevious, Is.True);
    Assert.That(delta.CpuPercent(0).HasValue, Is.True);
    Assert.That(delta.CpuPercent(99_999).HasValue, Is.True);
    Assert.That(delta.CpuPercent(100_000).HasValue, Is.False, "past the end is not a reading");
  }

  /// <summary>
  /// Ten times the threads must not be a hundred times the work. The dictionary is keyed on the
  /// thread's identity, and a key that hashed badly — or a lookup that became a scan — would show
  /// up here and nowhere else.
  /// </summary>
  [Test]
  public void TenTimesTheThreadsIsNotAHundredTimesTheCost() {
    var small = TimeUpdate(new ThreadDelta(), Threads(10_000, 1_000_000)).TotalMilliseconds;
    var large = TimeUpdate(new ThreadDelta(), Threads(100_000, 1_000_000)).TotalMilliseconds;

    // Twenty-five times rather than ten, because the small figure is small enough that the timer's
    // own resolution is a visible part of it. Quadratic growth is a hundredfold and clears this by
    // a wide margin either way.
    Assert.That(large, Is.LessThan(Math.Max(small * 25, 100)));
  }

  /// <summary>
  /// The tab is re-read on every tick while it is open, so anything this allocates per update is
  /// allocated once a second for as long as somebody is looking (PRD §5.4).
  /// </summary>
  [Test]
  public void TheSteadyStateAllocatesNothingPerTick() {
    var delta = new ThreadDelta();
    var threads = Threads(100_000, 1_000_000);

    // Three rounds to grow every buffer and fill the dictionary, so what is measured below is the
    // steady state rather than the growth §71.3 permits once.
    for (var round = 0; round < 3; ++round)
      delta.Update(_Swarm, threads, 16);

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var round = 0; round < 10; ++round)
      delta.Update(_Swarm, threads, 16);

    var perRound = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
    TestContext.Out.WriteLine($"{perRound} bytes a tick at 100 000 threads");
    Assert.That(perRound, Is.LessThan(4_096));
  }

  /// <summary>
  /// A pool that ends a worker and starts another gets the same thread id back, and the kernel
  /// hands them out again quickly at this size. The history is keyed on the start time as well as
  /// the id for exactly that reason, so the new thread must read as new rather than inheriting the
  /// old one's CPU time — which would show a thread busy since before it existed.
  /// </summary>
  [Test]
  public void AReusedThreadIdAtSizeIsNotTheSameThread() {
    var delta = new ThreadDelta();
    var first = Threads(100_000, 1_000_000);
    delta.Update(_Swarm, first, 16);

    // The same hundred thousand ids, started later and with the counters of a thread that has just
    // begun. Every one of them is a different thread.
    var second = new ThreadRecord[first.Length];
    for (var i = 0; i < first.Length; ++i)
      second[i] = first[i] with { StartTimeUtcTicks = 2, CpuTimeNs = Counter.Of(0ul) };

    delta.Update(_Swarm, second, 16);

    Assert.That(delta.CpuPercent(0).HasValue, Is.False, "a thread that has been read once has no rate");
    Assert.That(delta.CpuPercent(99_999).HasValue, Is.False);
  }

  /// <summary>
  /// Threads that ended are dropped rather than left to accumulate. A pool at this size churns
  /// through ids all day, and a history that only ever grows is a leak with a slow fuse — the one
  /// that would be measured in hundreds of megabytes here rather than in kilobytes.
  /// </summary>
  [Test]
  public void ThreadsThatEndedAreForgottenAtSize() {
    var delta = new ThreadDelta();

    // Twelve rounds, each of a hundred thousand threads sharing no id with any round before it. If
    // nothing were forgotten the history would hold one million two hundred thousand readings by
    // the end.
    for (var round = 0; round < 12; ++round) {
      delta.Update(_Swarm, Threads(100_000, 1_000_000, firstTid: 1 + round * 100_000), 16);
      Assert.That(delta.HistoryCount, Is.EqualTo(100_000), $"after round {round}");
    }

    // Counted rather than weighed. Asking the garbage collector how much is held reads the
    // large-object heap's refusal to compact a hundred thousand discarded records as growth, which
    // is a false alarm about the one thing this test exists to catch.
    //
    // And the readings that remain are the current ones: the last round has a history to divide by,
    // which it would not if an earlier round's keys had been kept and this round's discarded.
    delta.Update(_Swarm, Threads(100_000, 2_000_000, firstTid: 1 + 11 * 100_000), 16);
    Assert.That(delta.CpuPercent(99_999).HasValue, Is.True);
  }

  #endregion

}
