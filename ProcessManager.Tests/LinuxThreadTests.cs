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
    Assert.That(threads, Has.Count.EqualTo(4));
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
  /// The two halves mean opposite things — a thread that yields is waiting on something, a thread
  /// that is preempted is losing a contended processor — and the total cannot tell them apart.
  /// </summary>
  [Test]
  public void TheVoluntaryAndInvoluntaryHalvesAreReportedSeparately() {
    var main = One(this.Threads(), 1001);

    Assert.That(main.VoluntaryContextSwitches.Value, Is.EqualTo(90ul));
    Assert.That(main.InvoluntaryContextSwitches.Value, Is.EqualTo(900ul));
    Assert.That(
      main.ContextSwitches.Value,
      Is.EqualTo(main.VoluntaryContextSwitches.Value + main.InvoluntaryContextSwitches.Value)
    );
  }

  /// <summary>
  /// A kernel built without <c>CONFIG_SCHEDSTATS</c> writes no switch lines at all. That is not a
  /// thread which has never been switched, and the total must not become a confident zero either.
  /// </summary>
  [Test]
  public void AStatusWithoutTheSwitchLinesReportsUnknownRatherThanZero() {
    var thread = One(this.Threads(), 1017);

    Assert.That(thread.VoluntaryContextSwitches.HasValue, Is.False);
    Assert.That(thread.VoluntaryContextSwitches.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(thread.InvoluntaryContextSwitches.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(thread.ContextSwitches.HasValue, Is.False, "a total of two unknowns is not zero");
    // The rest of the status was readable, so the affinity is still an answer.
    Assert.That(thread.Affinity, Is.EqualTo("2-3"));
  }

  /// <summary>
  /// A thread that exits between the task listing and the status read leaves an ENOENT behind. That
  /// is a thread that is gone, not a thread we lack the privilege for — and the old reading called
  /// every failure a permission problem, which sends the reader hunting for a right they already had.
  /// </summary>
  [Test]
  public void AThreadThatVanishedBeforeItsStatusWasReadSaysSo() {
    var thread = One(this.Threads(), 1027);

    Assert.That(thread.ContextSwitches.Reason, Is.EqualTo(UnknownReason.ProcessExited));
    Assert.That(thread.VoluntaryContextSwitches.Reason, Is.EqualTo(UnknownReason.ProcessExited));
    Assert.That(thread.InvoluntaryContextSwitches.Reason, Is.EqualTo(UnknownReason.ProcessExited));
    Assert.That(thread.Affinity, Is.Null);
    // Everything stat carried is still there: one unreadable file is not an unreadable thread.
    Assert.That(thread.Name, Is.EqualTo("gone"));
    Assert.That(thread.LastCpu, Is.EqualTo(5));
  }

  /// <summary>
  /// The nice value, which is the priority the thread was <em>given</em>: the effective priority in
  /// <c>Priority</c> moves with the load, and only the pair says whether a busy thread is being
  /// polite or was simply never asked to be.
  /// </summary>
  [Test]
  public void TheBasePriorityIsTheNiceValue() {
    var threads = this.Threads();

    Assert.That(One(threads, 1001).BasePriority, Is.EqualTo(-5));
    Assert.That(One(threads, 1007).BasePriority, Is.EqualTo(0));
  }

  /// <summary>
  /// Field 41 sits two past the processor, behind fields nothing else reads, and every one of its
  /// neighbours is a plausible small integer — so a miscount produces a scheduling class rather than
  /// an error. The realtime thread is the guard: only the right field reads 1.
  /// </summary>
  [Test]
  public void TheSchedulingPolicyIsReadFromTheRightFieldOfStat() {
    var threads = this.Threads();

    Assert.That(One(threads, 1001).Policy, Is.EqualTo(SchedulingPolicy.Other));
    Assert.That(One(threads, 1017).Policy, Is.EqualTo(SchedulingPolicy.Fifo));
    // The effective priority the kernel derives from a realtime priority of 50, which is what makes
    // the policy column worth having: -51 on its own says nothing a reader can act on.
    Assert.That(One(threads, 1017).Priority, Is.EqualTo(-51));
  }

  /// <summary>
  /// Kept in the kernel's own list notation rather than expanded to a mask: on a wide machine the
  /// list is the readable form, and it is what <c>taskset</c> both prints and accepts.
  /// </summary>
  [Test]
  public void TheAffinityIsTheListTheKernelWrote() {
    var threads = this.Threads();

    Assert.That(One(threads, 1001).Affinity, Is.EqualTo("0-3"));
    Assert.That(One(threads, 1007).Affinity, Is.EqualTo("0,2"), "a comma list, not only a range");
  }

  /// <summary>
  /// Threads start after the process that made them, and the difference is what identifies a worker
  /// pool that grew under load. The tick base is the process's, so an off-by-one boot time would
  /// move every thread at once — hence the comparison against the process rather than a constant.
  /// </summary>
  [Test]
  public void EachThreadCarriesItsOwnStartTime() {
    var threads = this.Threads();
    var main = One(threads, 1001);
    var worker = One(threads, 1007);

    Assert.That(main.StartTimeUtcTicks, Is.GreaterThan(0));
    // 100 clock ticks at 100 Hz is one second, in 100 ns units.
    Assert.That(worker.StartTimeUtcTicks - main.StartTimeUtcTicks, Is.EqualTo(TimeSpan.TicksPerSecond));
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
