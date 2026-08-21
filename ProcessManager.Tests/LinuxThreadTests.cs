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

  /// <summary>
  /// <c>schedstat</c>'s middle number: how long other threads have kept this one off a processor
  /// since it started. §29 declines to call it a wait duration, and it is not named as one — but it
  /// is readable for threads whose registers are refused, which makes it the only scheduling delay
  /// this program can report at all.
  /// </summary>
  [Test]
  public void TheQueuedTimeComesFromSchedStat() {
    var threads = this.Threads();

    Assert.That(One(threads, 1001).QueuedNs.Value, Is.EqualTo(4_200_000_000ul));
    Assert.That(One(threads, 1007).QueuedNs.Value, Is.EqualTo(120_000_000ul));
  }

  /// <summary>
  /// A kernel booted with <c>schedstats=disable</c> writes a literal <c>0 0 0</c>. Believing it would
  /// report a thread that has never been given a processor, which no thread with a file to read can
  /// be (PRD §72.3).
  /// </summary>
  [Test]
  public void SchedulerStatisticsSwitchedOffAreUnknownRatherThanZero() {
    var thread = One(this.Threads(), 1017);

    Assert.That(thread.QueuedNs.HasValue, Is.False);
    Assert.That(thread.QueuedNs.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>
  /// Linux gives no thread an entry point except the first one, which began where the executable
  /// says it does. The old reading put a zero here, and zero is an address (PRD §29, §72.3).
  /// </summary>
  [Test]
  public void OnlyTheFirstThreadHasAStartAddressAndTheRestSaySoRatherThanZero() {
    var threads = this.Threads();

    foreach (var tid in (int[])[1007, 1017, 1027]) {
      var thread = One(threads, tid);
      Assert.That(thread.StartAddress.HasValue, Is.False, $"tid {tid}");
      Assert.That(thread.StartAddress.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform), $"tid {tid}");
      Assert.That(thread.StartModule, Is.Null, $"tid {tid}");
    }

    // The fixture has no readable exe link, so the first thread's is a refusal — which is still not
    // the address zero.
    Assert.That(One(threads, 1001).StartAddress.HasValue, Is.False);
    Assert.That(One(threads, 1001).StartAddress.Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  /// <summary>
  /// The kernel/user indicator (PRD §29). <c>syscall</c> answers outright where it is readable, and a
  /// wait channel answers where it is not: a thread parked in a named kernel function is in the
  /// kernel whatever the permissions say.
  /// </summary>
  [Test]
  public void TheModeSaysWhichSideOfTheBoundaryAThreadIsOn() {
    var threads = this.Threads();

    Assert.That(One(threads, 1001).Mode, Is.EqualTo(ThreadMode.User), "syscall reads 'running'");
    Assert.That(One(threads, 1007).Mode, Is.EqualTo(ThreadMode.Kernel));
    Assert.That(One(threads, 1007).SyscallNumber.Value, Is.EqualTo(202ul));
    // -1: in the kernel and not in a call. The number is a hole with a reason, never -1 widened.
    Assert.That(One(threads, 1017).Mode, Is.EqualTo(ThreadMode.Kernel));
    Assert.That(One(threads, 1017).SyscallNumber.HasValue, Is.False);
  }

  /// <summary>
  /// A thread the kernel would not describe is <see cref="ThreadMode.Unknown"/> rather than assumed
  /// to be in user code: a runnable thread may be on either side, and a coin toss rendered as a
  /// reading is worse than an empty cell (PRD §5.3).
  /// </summary>
  [Test]
  public void AThreadWithNoSyscallFileAndNoWaitChannelIsNotGuessedAtUser() {
    var thread = One(this.Threads(), 1027);

    Assert.That(thread.Mode, Is.EqualTo(ThreadMode.Unknown));
    Assert.That(thread.SyscallNumber.HasValue, Is.False);
    Assert.That(thread.InstructionPointer.HasValue, Is.False);
  }

  /// <summary>
  /// The user-space program counter, from the only file that carries it. Not from <c>stat</c>, whose
  /// <c>kstkeip</c> has read zero for every task that is not core-dumping since Linux 4.9.
  /// </summary>
  [Test]
  public void TheInstructionPointerIsResolvedToTheImageItIsIn() {
    var thread = One(this.Threads(), 1007);

    Assert.That(thread.InstructionPointer.Value, Is.EqualTo(0x7f1000012345ul));
    Assert.That(thread.InstructionModule, Is.EqualTo("/usr/lib/libc.so.6"));
  }

  /// <summary>
  /// Stacks grow down, so what is in use is the distance from the stack pointer to the top of the
  /// mapping it is in — which is why this needs the process's mappings and not only the thread's
  /// registers (PRD §29).
  /// </summary>
  [Test]
  public void StackUsageIsMeasuredFromTheTopOfTheMappingTheStackPointerIsIn() {
    var threads = this.Threads();

    // sp 0x7ffd0001f000 in a [stack] mapping ending at 0x7ffd00021000.
    Assert.That(One(threads, 1007).StackPointer.Value, Is.EqualTo(0x7ffd0001f000ul));
    Assert.That(One(threads, 1007).StackBytes.Value, Is.EqualTo(0x2000ul));
    Assert.That(One(threads, 1017).StackBytes.Value, Is.EqualTo(0x3000ul), "sp 0x…1e000 under the same 0x…21000 top");
  }

  /// <summary>
  /// A thread whose registers were refused has no stack usage either, and the reason travels: a zero
  /// would say the thread is using none of its stack, which is not true of any thread.
  /// </summary>
  [Test]
  public void AThreadWithNoReadableStackPointerReportsNoUsageRatherThanZero() {
    var thread = One(this.Threads(), 1001);

    Assert.That(thread.StackPointer.HasValue, Is.False, "a running thread has no register snapshot");
    Assert.That(thread.StackBytes.HasValue, Is.False);
    Assert.That(thread.StackBytes.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  #region the stack viewer (PRD §30)

  [Test]
  public void TheKernelStackIsReadWhereThePlatformPermitsIt() {
    using var probe = this.Probe();
    var stack = probe.GetThreadStack(new(1001, 100500), 1007);

    Assert.That(stack.KernelReason, Is.EqualTo(UnknownReason.None));
    Assert.That(stack.KernelFrameCount, Is.EqualTo(6));
    Assert.That(stack.Frames[0].Symbol, Is.EqualTo("futex_wait_queue_me"));
    Assert.That(stack.Frames[0].Kind, Is.EqualTo(FrameKind.Kernel));
  }

  /// <summary>
  /// Below the kernel frames sits the one user-space frame Linux gives up: the instruction the thread
  /// will resume at. It is marked as one frame and not as an unwind, because that is what it is
  /// (PRD §4.1, §30).
  /// </summary>
  [Test]
  public void TheOneUserFrameLinuxGivesUpIsMarkedAsSuch() {
    using var probe = this.Probe();
    var stack = probe.GetThreadStack(new(1001, 100500), 1007);

    var last = stack.Frames[^1];
    Assert.That(last.Kind, Is.EqualTo(FrameKind.User));
    Assert.That(last.Address.Value, Is.EqualTo(0x7f1000012345ul));
    Assert.That(last.Module, Is.EqualTo("/usr/lib/libc.so.6"), "module and offset without opening the image");
    Assert.That(stack.UserReason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform), "the rest is not unwound");
  }

  /// <summary>
  /// A stack file that is not there is not a thread without a stack. Which of the two it is decides
  /// whether the reader should go and start the elevated helper (PRD §3.4).
  /// </summary>
  [Test]
  public void AThreadWhoseKernelStackCouldNotBeReadSaysWhy() {
    using var probe = this.Probe();
    var stack = probe.GetThreadStack(new(1001, 100500), 1001);

    Assert.That(stack.KernelFrameCount, Is.EqualTo(0));
    Assert.That(stack.KernelReason, Is.Not.EqualTo(UnknownReason.None));
  }

  [Test]
  public void AskingAboutAThreadThatIsNotThereIsNotAnException() {
    using var probe = this.Probe();
    var stack = probe.GetThreadStack(new(1001, 100500), 999999);

    Assert.That(stack.Frames, Is.Empty);
    Assert.That(stack.KernelReason, Is.EqualTo(UnknownReason.ProcessExited));
  }

  #endregion

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
