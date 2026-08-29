using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Rows that outlive the processes behind them (PRD §14, §87).
/// </summary>
/// <remarks>
/// <para>
/// The thing three boxes were waiting on. `exit.time` had nowhere for its answer to live, §87's
/// exited-highlight had nothing to highlight, and <see cref="ProcessCategory.Exited"/> had a colour
/// in the palette that nothing ever produced.
/// </para>
/// <para>
/// <b>Off by default and deliberately.</b> A table that keeps its dead is showing something that is
/// not there — a considered thing to ask for and a bad thing to assume — so every assertion here
/// that something is retained had to switch it on first, and the first assertion below is that
/// nothing is retained when nobody asked.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RetainedRowTests {

  /// <summary>A probe whose machine is whatever the test last said it was.</summary>
  private sealed class Machine : ISystemProbe {
    public List<(int Pid, ulong Started, string Name)> Processes { get; } = [];

    public string Description => "stub";
    public HostInfo DescribeHost() => new();

    public void Sample(SystemSnapshot snapshot) {
      var records = snapshot.PrepareProcesses(this.Processes.Count);
      for (var i = 0; i < this.Processes.Count; ++i) {
        records[i].Key = new(this.Processes[i].Pid, this.Processes[i].Started);
        records[i].Name = this.Processes[i].Name;
        records[i].CpuTimeNs = Counter.Of(1_000_000ul);
      }
    }

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

  private static int Rows(Sampler sampler, string name) {
    var found = 0;
    foreach (var process in sampler.Current.Processes)
      if (process.Name == name)
        ++found;

    return found;
  }

  private static ProcessRecord? Find(Sampler sampler, string name) {
    foreach (var process in sampler.Current.Processes)
      if (process.Name == name)
        return process;

    return null;
  }

  /// <summary>
  /// Nobody asked, so nothing is kept. This is the assertion that has to hold for every machine that
  /// never touches the setting, which is nearly all of them.
  /// </summary>
  [Test]
  public void NothingIsKeptUnlessSomebodyAsked() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    using var sampler = new Sampler(machine);

    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();

    Assert.That(sampler.RetainedCount, Is.Zero);
    Assert.That(Rows(sampler, "goes"), Is.Zero);
  }

  /// <summary>And when they did, the row is still there with the reading it last had.</summary>
  [Test]
  public void AProcessThatEndedIsStillARow() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    using var sampler = new Sampler(machine) { KeepExitedSeconds = 30 };

    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();

    Assert.That(Rows(sampler, "goes"), Is.EqualTo(1));

    var row = Find(sampler, "goes");
    Assert.That(row, Is.Not.Null);
    Assert.That(row!.Value.HasExited, Is.True);
    Assert.That(row.Value.ExitedUtcTicks, Is.GreaterThan(0));
    Assert.That(row.Value.CpuTimeNs.Value, Is.EqualTo(1_000_000ul), "the reading it last had");
  }

  /// <summary>
  /// <b>It is reported dead once and not once a second.</b> A tombstone left in the index would be
  /// unmatched every sample and announce the same death for as long as the row was kept, which would
  /// put one exit in a timeline sixty times a minute.
  /// </summary>
  [Test]
  public void ADeathIsAnnouncedOnce() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    using var sampler = new Sampler(machine) { KeepExitedSeconds = 30 };

    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();
    Assert.That(sampler.Delta.Exited, Has.Count.EqualTo(1), "the sample it went");

    sampler.Sample();
    Assert.That(sampler.Delta.Exited, Is.Empty, "and not again while the row is kept");

    sampler.Sample();
    Assert.That(sampler.Delta.Exited, Is.Empty);
    Assert.That(Rows(sampler, "goes"), Is.EqualTo(1), "still exactly one row and not four");
  }

  /// <summary>
  /// A kept row is never new. Without this it would flash as having just started on the sample after
  /// it died, which is the opposite of what happened.
  /// </summary>
  [Test]
  public void AKeptRowIsNotANewOne() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    using var sampler = new Sampler(machine) { KeepExitedSeconds = 30 };
    sampler.Delta.NewHighlightSeconds = 30;

    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();

    var processes = sampler.Current.Processes;
    for (var i = 0; i < processes.Length; ++i)
      if (processes[i].Name == "goes") {
        Assert.That(sampler.Delta.IsNew(i), Is.False, "it ended; it did not start");
        Assert.That(sampler.Delta.AppearedThisSample(i), Is.False);
      }
  }

  /// <summary>
  /// <b>Every rate over a kept row is unsampled, not nought.</b> A dead row reporting nought per cent
  /// would be a measurement — "it used no processor in the last second" — where the truth is that
  /// there was no last second for it to use one in (PRD §3.4, §72.3).
  /// </summary>
  [Test]
  public void AKeptRowMeasuresNothingRatherThanMeasuringZero() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    using var sampler = new Sampler(machine) { KeepExitedSeconds = 30 };

    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();

    var at = -1;
    var processes = sampler.Current.Processes;
    for (var i = 0; i < processes.Length; ++i)
      if (processes[i].Name == "goes")
        at = i;

    Assert.That(at, Is.GreaterThanOrEqualTo(0), "the kept row is in the snapshot");
    Assert.Multiple(() => {
      Assert.That(sampler.Delta.CpuPercent(at).HasValue, Is.False, "cpu");
      Assert.That(sampler.Delta.CpuPercent(at).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
      Assert.That(sampler.Delta.ReadBytesPerSecond(at).HasValue, Is.False, "read");
      Assert.That(sampler.Delta.WriteBytesPerSecond(at).HasValue, Is.False, "write");
    });
  }

  /// <summary>
  /// The colour the palette has had since it was written, and which nothing ever produced. It comes
  /// before New, because a short-lived process can be born and buried in one frame and "it has gone"
  /// is the more urgent of the two things to say.
  /// </summary>
  [Test]
  public void AKeptRowIsColouredAsEnded() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    using var sampler = new Sampler(machine) { KeepExitedSeconds = 30 };

    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();

    var row = Find(sampler, "goes");
    Assert.That(row, Is.Not.Null);
    Assert.That(
      ProcessCategories.Classify(row!.Value, currentUserId: 1000, isNew: true),
      Is.EqualTo(ProcessCategory.Exited),
      "ended beats new even when both are true"
    );
  }

  /// <summary>Turning it off forgets what was being kept, rather than freezing it on screen.</summary>
  [Test]
  public void SwitchingItOffDropsWhatWasKept() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    var sampler = new Sampler(machine) { KeepExitedSeconds = 30 };
    using (sampler) {
      sampler.Sample();
      machine.Processes.Clear();
      sampler.Sample();
      Assert.That(sampler.RetainedCount, Is.EqualTo(1));

      sampler.KeepExitedSeconds = 0;
      sampler.Sample();

      Assert.That(sampler.RetainedCount, Is.Zero);
      Assert.That(Rows(sampler, "goes"), Is.Zero);
    }
  }

  /// <summary>
  /// A recycled pid is a different process, so a live row and the tombstone of its predecessor are
  /// two rows. Folding them would attach a dead process's history to a living one, which is the
  /// exact thing the identity pair exists to prevent (PRD §8.2, §72.2).
  /// </summary>
  [Test]
  public void ARecycledPidDoesNotResurrectTheDeadRow() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    using var sampler = new Sampler(machine) { KeepExitedSeconds = 30 };

    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();

    // The same number, started later: the kernel handed the pid back.
    machine.Processes.Add((100, 999, "comes"));
    sampler.Sample();

    Assert.That(Rows(sampler, "goes"), Is.EqualTo(1), "the dead one is still its own row");
    Assert.That(Rows(sampler, "comes"), Is.EqualTo(1), "and the live one is another");
    Assert.That(Find(sampler, "comes")!.Value.HasExited, Is.False);
  }

  /// <summary>
  /// The exit code is a dash and not a nought. Neither kernel tells a bystander what a process it
  /// did not start exited with, and nought is the code that means success — the one value that must
  /// never be invented (PRD §14, §72.3).
  /// </summary>
  [Test]
  public void AnExitCodeNobodyCouldKnowIsNotZero() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "goes"));
    using var sampler = new Sampler(machine) { KeepExitedSeconds = 30 };

    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();

    var row = Find(sampler, "goes")!.Value;
    Assert.That(row.ExitCode.HasValue, Is.False);
    Assert.That(row.ExitCode.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(FieldAccessor.Text(ProcessField.ExitCode, in row, new(), 0), Is.EqualTo("—"));
  }

  /// <summary>
  /// And a running process has no exit time at all — not nought, which would sort with the oldest
  /// deaths there are and match a filter looking for one.
  /// </summary>
  [Test]
  public void ARunningProcessHasNoExitTime() {
    var machine = new Machine();
    machine.Processes.Add((100, 1, "runs"));
    using var sampler = new Sampler(machine);
    sampler.Sample();

    var row = Find(sampler, "runs")!.Value;
    Assert.That(FieldAccessor.Text(ProcessField.ExitTime, in row, new(), 0), Is.EqualTo("—"));
    Assert.That(FieldAccessor.Number(ProcessField.ExitTime, in row, new(), 0), Is.Null);
  }

  /// <summary>
  /// The count bound, which the age bound cannot cover. A build machine ends a thousand processes a
  /// second and thirty seconds of that is thirty thousand rows nobody can read — the duration is what
  /// somebody asked for and this is what keeps the table a table.
  /// </summary>
  [Test]
  public void TheNumberKeptIsBoundedAsWellAsTheAge() {
    var machine = new Machine();
    for (var i = 0; i < 2500; ++i)
      machine.Processes.Add((1000 + i, 1, $"p{i}"));

    using var sampler = new Sampler(machine) { KeepExitedSeconds = 3600 };
    sampler.Sample();
    machine.Processes.Clear();
    sampler.Sample();

    Assert.That(sampler.RetainedCount, Is.LessThanOrEqualTo(2000));
    Assert.That(sampler.RetainedCount, Is.GreaterThan(0));
  }

}
