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
  private static TimeSpan TimeRebuild(ProcessView view, SystemSnapshot snapshot, SnapshotDelta delta) {
    view.Rebuild(snapshot, delta);

    var clock = Stopwatch.StartNew();
    view.Rebuild(snapshot, delta);
    clock.Stop();
    TestContext.Out.WriteLine($"{snapshot.ProcessCount} processes, tree={view.TreeMode}: {clock.Elapsed.TotalMilliseconds:0.0} ms");
    return clock.Elapsed;
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

    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
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

}
