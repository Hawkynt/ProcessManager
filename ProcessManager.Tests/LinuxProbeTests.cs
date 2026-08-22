using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The Linux probe against a recorded <c>/proc</c> tree (PRD §9.1, §9.2).
/// </summary>
/// <remarks>
/// Runs on every OS, not just Linux: the probe reads a directory, and the fixture is a directory.
/// That is the whole reason <see cref="LinuxProbeOptions.ProcRoot"/> exists — a parser tested only on
/// the platform it parses for is a parser tested on one CI leg out of three.
/// </remarks>
[TestFixture(false, TestName = "LinuxProbeFixtureTests (syscalls)")]
[TestFixture(true, TestName = "LinuxProbeFixtureTests (portable file access)")]
public sealed class LinuxProbeFixtureTests(bool portable) {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private LinuxProbeOptions Options => new() {
    // Run twice: once down the syscall path this machine would use, once down the portable one the
    // Windows and macOS legs are forced onto. They must agree field for field, which is the only way
    // to find out here that the other legs are broken rather than on somebody else's runner.
    UsePortableFileAccess = portable,
    ProcRoot = FixtureRoot,
    PasswdPath = Path.Combine(FixtureRoot, "passwd"),
    // Stated, not inherited: the fixture was written with USER_HZ 100, and reading the running
    // machine's value would make every CPU time in it wrong by a constant factor on a machine that
    // uses a different one — silently.
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    // The fixture was recorded by somebody else, so the live uid would refuse every file in it.
    EffectiveUserId = 0,
    CountFileDescriptors = true,
  };

  [Test]
  public void ItReadsEveryProcessInTheFixture() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    Assert.That(snapshot.ProcessCount, Is.EqualTo(5));
    var pids = new List<int>();
    foreach (var process in snapshot.Processes)
      pids.Add(process.Pid);

    Assert.That(pids, Is.EquivalentTo(new[] { 1, 2, 1000, 1001, 1002 }));
  }

  [Test]
  public void ACommandNameContainingBracketsAndSpacesIsParsedWhole() {
    // "foo) 0 (bar" is a legal comm and is the exact shape that breaks a parser splitting on the
    // first ')' or on whitespace. Everything after it in the record must still land in the right
    // field, which the assertions below check by reading past it (PRD §5.1, §9.3).
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var process = Find(snapshot, 1001);
    Assert.That(process.Name, Is.EqualTo("foo) 0 (bar"));
    Assert.That(process.ParentPid, Is.EqualTo(1000));
    Assert.That(process.State, Is.EqualTo(ProcessState.Running));
    Assert.That(process.Nice, Is.EqualTo(-5));
    Assert.That(process.ThreadCount, Is.EqualTo(8));
    Assert.That(process.Key.StartTicks, Is.EqualTo(100500ul));
  }

  [Test]
  public void CpuTimeIsConvertedFromClockTicksToNanoseconds() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    // pid 1001: utime 9000 + stime 1000 = 10000 ticks at 100 Hz = 100 seconds.
    var process = Find(snapshot, 1001);
    Assert.That(process.CpuTimeNs.Value, Is.EqualTo(100_000_000_000ul));
    Assert.That(process.UserTimeNs.Value, Is.EqualTo(90_000_000_000ul));
    Assert.That(process.KernelTimeNs.Value, Is.EqualTo(10_000_000_000ul));
  }

  [Test]
  public void MemoryComesFromStatusInBytes() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var process = Find(snapshot, 1001);
    // Committed, not resident: VmData is what Windows calls private bytes, and using RssAnon here
    // made the same column mean two different things on the two platforms.
    Assert.That(process.PrivateBytes.Value, Is.EqualTo(95000ul * 1024), "VmData");
    Assert.That(process.PrivateWorkingSetBytes.Value, Is.EqualTo(90000ul * 1024), "RssAnon");
    Assert.That(process.WorkingSetBytes.Value, Is.EqualTo(25000ul * 4096), "rss pages from stat");
    Assert.That(process.VirtualBytes.Value, Is.EqualTo(99000000ul));
    Assert.That(process.PeakWorkingSetBytes.Value, Is.EqualTo(95048ul * 1024), "VmHWM");
    Assert.That(process.PeakVirtualBytes.Value, Is.EqualTo(150000ul * 1024), "VmPeak");
  }

  /// <summary>
  /// A kernel thread has no address space, so its status carries no VmData line at all. The
  /// committed figure must then read as "does not apply" rather than as zero committed bytes.
  /// </summary>
  [Test]
  public void AProcessWithoutAnAddressSpaceReportsUnknownRatherThanZero() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var process = Find(snapshot, 2);
    Assert.That(process.PrivateBytes.HasValue, Is.False);
    Assert.That(process.PrivateBytes.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  [Test]
  public void IoCountersAreRead() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var process = Find(snapshot, 1001);
    Assert.That(process.ReadBytes.Value, Is.EqualTo(1048576ul));
    Assert.That(process.WriteBytes.Value, Is.EqualTo(2097152ul));
  }

  [Test]
  public void UsersAreResolvedFromThePasswdFile() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    Assert.That(Find(snapshot, 1).UserName, Is.EqualTo("root"));
    Assert.That(Find(snapshot, 1000).UserName, Is.EqualTo("alice"));
  }

  [Test]
  public void TheCommandLineIsJoinedFromItsNulSeparatedParts() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    Assert.That(Find(snapshot, 1).CommandLine, Is.EqualTo("/sbin/init splash"));
    Assert.That(Find(snapshot, 1002).CommandLine, Is.EqualTo("sleep 600"));
  }

  [Test]
  public void TheCgroupPathIsTakenFromTheV2Line() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    Assert.That(Find(snapshot, 1000).ContainerPath, Is.EqualTo("/user.slice/user-1000.slice/session-3.scope"));
  }

  [Test]
  public void SystemCountersComeFromStatMeminfoLoadavgAndUptime() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    Assert.That(snapshot.PerCoreCount, Is.EqualTo(4));
    Assert.That(snapshot.System.CoreCount, Is.EqualTo(4));
    Assert.That(snapshot.System.TotalMemoryBytes.Value, Is.EqualTo(16384000ul * 1024));
    Assert.That(snapshot.System.AvailableMemoryBytes.Value, Is.EqualTo(8192000ul * 1024));
    // SwapTotal 4194304 kB minus SwapFree 3145728 kB.
    Assert.That(snapshot.System.UsedSwapBytes.Value, Is.EqualTo(1048576ul * 1024));
    Assert.That(snapshot.System.LoadAverage1, Is.EqualTo(1.25).Within(0.0001));
    Assert.That(snapshot.System.LoadAverage15, Is.EqualTo(0.55).Within(0.0001));
    Assert.That(snapshot.System.UptimeSeconds, Is.EqualTo(123456.78).Within(0.01));
    Assert.That(snapshot.System.RunningProcesses, Is.EqualTo(3));
  }

  [Test]
  public void PerCoreTimesAreScaledAndTheAggregateIsTheirSum() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    // cpu0: user 100000 ticks at 100 Hz = 1000 s = 1e12 ns.
    Assert.That(snapshot.PerCore[0].UserNs, Is.EqualTo(1_000_000_000_000ul));

    ulong userSum = 0;
    foreach (var core in snapshot.PerCore)
      userSum += core.UserNs;

    Assert.That(snapshot.System.Cpu.UserNs, Is.EqualTo(userSum));
  }

  /// <summary>
  /// §104's source backend. A snapshot says which probe filled it, and a replay against a recorded
  /// tree names the tree — so a reading taken from a diagnostic bundle can never be mistaken for one
  /// taken from this machine.
  /// </summary>
  [Test]
  public void ASnapshotNamesTheBackendThatFilledIt() {
    using var probe = new LinuxProbe(Options);
    using var sampler = new Sampler(probe);

    // Before anybody sampled: the snapshot came from nowhere, and says so.
    Assert.That(sampler.Current.Source, Is.Empty);

    sampler.Sample();
    Assert.That(sampler.Current.Source, Is.EqualTo(probe.Description));
    Assert.That(sampler.Current.Source, Does.StartWith("linux:"));
    Assert.That(sampler.Current.Source, Does.Not.EqualTo("linux:/proc"), "the fixture must not claim to be this machine");
  }

  [Test]
  public void TheProcessTreeMatchesTheFixture() {
    using var probe = new LinuxProbe(Options);
    using var sampler = new Sampler(probe);
    sampler.Sample();

    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.Pid, SortDescending = false };
    view.Rebuild(sampler.Current, sampler.Delta);

    var lines = new List<string>();
    foreach (var row in view.Rows) {
      ref readonly var process = ref sampler.Current.Processes[row.Index];
      lines.Add(new string(' ', row.Depth * 2) + process.Name);
    }

    // kthreadd is pid 2 and its own root, so sorting by pid puts the whole systemd subtree first
    // and the second root after it — not pid 2 in between pid 1 and pid 1000.
    Assert.That(lines, Is.EqualTo(new[] { "systemd", "  bash", "    foo) 0 (bar", "    sleep", "kthreadd" }));
  }

  [Test]
  public void FileDescriptorsAreCountedWithoutDotAndDotDot() {
    using var probe = new LinuxProbe(Options with { CountFileDescriptors = true });
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    Assert.That(Find(snapshot, 1000).HandleCount.Value, Is.EqualTo(3ul));
  }

  [Test]
  public void TheHandleCountIsAlsoAvailableOnDemand() {
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var key = Find(snapshot, 1000).Key;
    Assert.That(probe.GetHandleCount(key).Value, Is.EqualTo(3ul));
  }

  [Test]
  public void WithFileDescriptorCountingOffTheColumnSaysSoRatherThanShowingZero() {
    using var probe = new LinuxProbe(Options with { CountFileDescriptors = false });
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var handles = Find(snapshot, 1000).HandleCount;
    Assert.That(handles.HasValue, Is.False);
    Assert.That(handles.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  [Test]
  public void SamplingTwiceProducesTheSameAnswer() {
    // The buffers are reused between samples; a stale field from the previous one would show up here
    // and nowhere else.
    using var probe = new LinuxProbe(Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    var first = Find(snapshot, 1001);
    probe.Sample(snapshot);
    var second = Find(snapshot, 1001);

    Assert.That(second.Name, Is.EqualTo(first.Name));
    Assert.That(second.CpuTimeNs, Is.EqualTo(first.CpuTimeNs));
    Assert.That(second.Key, Is.EqualTo(first.Key));
  }

  private static ProcessRecord Find(SystemSnapshot snapshot, int pid) {
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process;

    Assert.Fail($"pid {pid} is not in the snapshot");
    return default;
  }

}
