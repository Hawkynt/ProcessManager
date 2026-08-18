using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The per-thread fields (PRD §29), read from a recorded <c>/proc/[pid]/task</c>.
/// </summary>
[TestFixture(false, TestName = "LinuxThreadTests (syscalls)")]
[TestFixture(true, TestName = "LinuxThreadTests (portable file access)")]
public sealed class LinuxThreadTests(bool portable) {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private LinuxProbe Probe() => new(new() {
    UsePortableFileAccess = portable,
    ProcRoot = FixtureRoot,
    PasswdPath = Path.Combine(FixtureRoot, "passwd"),
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    EffectiveUserId = 0,
  });

  private IReadOnlyList<ThreadRecord> Threads() {
    using var probe = this.Probe();
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    foreach (var process in snapshot.Processes)
      if (process.Pid == 1001)
        return probe.GetThreads(process.Key);

    Assert.Fail("pid 1001 is not in the fixture");
    return [];
  }

  private static ThreadRecord One(IReadOnlyList<ThreadRecord> threads, int tid) {
    foreach (var thread in threads)
      if (thread.Tid == tid)
        return thread;

    Assert.Fail($"tid {tid} was not enumerated");
    return default;
  }

  [Test]
  public void EveryThreadInTheTaskDirectoryIsEnumerated() {
    var threads = this.Threads();
    Assert.That(threads, Has.Count.EqualTo(2));
  }

  /// <summary>
  /// Linux gives every thread its own name and puts it in the same stat line everything else here
  /// comes from, so it costs nothing — and it is usually the most useful column on the page.
  /// </summary>
  [Test]
  public void EachThreadCarriesItsOwnName() {
    var threads = this.Threads();

    Assert.That(One(threads, 1007).Name, Is.EqualTo("worker"));
    // The main thread's name is the process's own, brackets and all: the same hostile comm that
    // breaks a naive stat parser has to survive here too (PRD §98).
    Assert.That(One(threads, 1001).Name, Is.EqualTo("foo) 0 (bar"));
  }

  [Test]
  public void UserAndKernelTimeAreReportedSeparatelyAndSumToTheTotal() {
    var main = One(this.Threads(), 1001);

    // utime 9000 + stime 1000 ticks at 100 Hz.
    Assert.That(main.UserTimeNs.Value, Is.EqualTo(90_000_000_000ul));
    Assert.That(main.KernelTimeNs.Value, Is.EqualTo(10_000_000_000ul));
    Assert.That(main.CpuTimeNs.Value, Is.EqualTo(main.UserTimeNs.Value + main.KernelTimeNs.Value));
  }

  [Test]
  public void ContextSwitchesComeFromTheThreadsOwnStatus() {
    var threads = this.Threads();

    Assert.That(One(threads, 1001).ContextSwitches.Value, Is.EqualTo(990ul), "90 voluntary + 900 not");
    Assert.That(One(threads, 1007).ContextSwitches.Value, Is.EqualTo(10ul));
  }

  /// <summary>
  /// Field 39 sits behind fourteen fields nothing else reads, so a miscount lands on a neighbour and
  /// still looks like a plausible processor number.
  /// </summary>
  [Test]
  public void TheLastProcessorIsReadFromTheRightFieldOfStat() {
    var threads = this.Threads();

    Assert.That(One(threads, 1001).LastCpu, Is.EqualTo(3));
    Assert.That(One(threads, 1007).LastCpu, Is.EqualTo(11));
  }

  /// <summary>
  /// The answer to "why is this hanging" without a stack walk, which is the question §2 puts first.
  /// </summary>
  [Test]
  public void ABlockedThreadNamesWhatItIsWaitingOn() {
    var threads = this.Threads();

    Assert.That(One(threads, 1007).WaitReason, Is.EqualTo("futex_wait_queue_me"));
    // A running thread's wchan reads "0", which means nothing to show rather than a symbol called 0.
    Assert.That(One(threads, 1001).WaitReason, Is.Null);
  }

  [Test]
  public void TheStateAndPriorityStillLandWhereTheyDid() {
    var threads = this.Threads();

    Assert.That(One(threads, 1001).State, Is.EqualTo(ProcessState.Running));
    Assert.That(One(threads, 1001).Priority, Is.EqualTo(15));
    Assert.That(One(threads, 1007).State, Is.EqualTo(ProcessState.Sleeping));
    Assert.That(One(threads, 1007).Priority, Is.EqualTo(20));
  }

  [Test]
  public void AProcessWithNoTaskDirectoryReturnsNothingRatherThanThrowing() {
    using var probe = this.Probe();
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    foreach (var process in snapshot.Processes)
      if (process.Pid == 1000)
        Assert.That(probe.GetThreads(process.Key), Is.Empty);
  }

  /// <summary>
  /// The same field, read for the process rather than for one of its threads. It is what makes
  /// "this process never leaves core 3" visible in the table (PRD §15).
  /// </summary>
  [Test]
  public void TheProcessAlsoCarriesItsLastProcessor() {
    using var probe = this.Probe();
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    foreach (var process in snapshot.Processes)
      if (process.Pid == 1001)
        Assert.That(process.LastCpu, Is.EqualTo(3));
  }

}
