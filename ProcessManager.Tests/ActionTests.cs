using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The subtree walk that "end process tree" depends on.
/// </summary>
[TestFixture]
public sealed class ProcessTreeWalkTests {

  [Test]
  public void DescendantsComeBeforeTheirParent() {
    // Ending the parent first reparents its children to init, and they are then no longer findable
    // as its descendants — so a kill-tree that starts at the top kills the top and loses the rest.
    var snapshot = Build((1, 0), (10, 1), (11, 10), (12, 11));

    var order = ProcessTree.DescendantsFirst(snapshot, 10);

    Assert.That(order.Select(k => k.Pid), Is.EqualTo(new[] { 12, 11, 10 }));
  }

  [Test]
  public void SiblingsAreAllIncluded() {
    var snapshot = Build((1, 0), (10, 1), (11, 10), (12, 10), (13, 12));

    var order = ProcessTree.DescendantsFirst(snapshot, 10);

    Assert.That(order.Select(k => k.Pid), Is.EquivalentTo(new[] { 10, 11, 12, 13 }));
    Assert.That(order[^1].Pid, Is.EqualTo(10), "the root is ended last");
    Assert.That(order.Select(k => k.Pid).ToList().IndexOf(13), Is.LessThan(order.Select(k => k.Pid).ToList().IndexOf(12)));
  }

  [Test]
  public void ALeafIsJustItself() {
    var snapshot = Build((1, 0), (10, 1));
    Assert.That(ProcessTree.DescendantsFirst(snapshot, 10).Select(k => k.Pid), Is.EqualTo(new[] { 10 }));
  }

  [Test]
  public void AnUnknownPidYieldsNothingRatherThanEverything() {
    // The failure mode this guards is the worst one available: a walk that treats "not found" as
    // "match everything" would end every process on the machine.
    var snapshot = Build((1, 0), (10, 1));
    Assert.That(ProcessTree.DescendantsFirst(snapshot, 999), Is.Empty);
  }

  [Test]
  public void ACycleTerminates() {
    var snapshot = Build((10, 11), (11, 10));
    Assert.That(() => ProcessTree.DescendantsFirst(snapshot, 10), Throws.Nothing);
  }

  [Test]
  public void FindReturnsTheIdentityNotJustThePid() {
    var snapshot = Build((1, 0), (10, 1));
    var key = ProcessTree.Find(snapshot, 10);

    Assert.That(key.Pid, Is.EqualTo(10));
    Assert.That(key.StartTicks, Is.EqualTo(1000ul));
    Assert.That(ProcessTree.Find(snapshot, 999).IsNone, Is.True);
  }

  private static SystemSnapshot Build(params (int Pid, int ParentPid)[] processes) {
    var snapshot = new SystemSnapshot { TimestampTicks = 0 };
    snapshot.System.CoreCount = 1;
    var buffer = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      buffer[i] = default;
      buffer[i].Key = new(processes[i].Pid, 1000ul);
      buffer[i].ParentPid = processes[i].ParentPid;
      buffer[i].Name = $"p{processes[i].Pid}";
    }

    return snapshot;
  }

}

/// <summary>
/// The identity check every action performs before it acts (PRD §8.2).
/// </summary>
/// <remarks>
/// These run against the recorded /proc tree, so they check the refusal paths without ending
/// anything: a real process is not needed to prove that the wrong process is refused, and a test
/// that terminates something to prove it can is not a test anyone should run in CI.
/// </remarks>
[TestFixture]
public sealed class LinuxProcessActionIdentityTests {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private static LinuxProcessActions Actions() => new(new() { ProcRoot = FixtureRoot });

  [Test]
  public void APidWhoseStartTimeDoesNotMatchIsRefused() {
    // The whole reason the key is a pair. Between the click and the syscall the pid can be recycled;
    // acting on it because the number matched is how the wrong program gets killed.
    var result = Actions().Terminate(new(1000, 999_999));

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
    Assert.That(result.Detail, Does.Contain("reused"));
  }

  [Test]
  public void APidThatIsNotThereIsReportedAsExited() {
    var result = Actions().Terminate(new(4242, 1));
    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.ProcessExited));
  }

  [Test]
  public void APidOfZeroOrLessIsRefusedOutright() {
    Assert.That(Actions().Terminate(new(0, 0)).Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(Actions().Suspend(new(-1, 0)).Outcome, Is.EqualTo(ActionOutcome.Refused));
  }

  [Test]
  public void AnEmptyAffinityMaskIsRefusedBeforeAnythingIsAttempted() {
    // A mask with no cores in it would leave the process nothing to run on. The refusal has to come
    // before the identity check, because it is wrong regardless of which process it names.
    var result = Actions().SetAffinity(new(1000, 100000), 0);
    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.Refused));
  }

  [Test]
  public void EveryActionChecksIdentityRatherThanJustTheTerminateOne() {
    var wrong = new ProcessKey(1000, 999_999);
    var actions = Actions();

    Assert.Multiple(() => {
      Assert.That(actions.Terminate(wrong).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.EndTask(wrong).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.Suspend(wrong).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.Resume(wrong).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SetPriority(wrong, 5).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SetAffinity(wrong, 0b11).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SendSignal(wrong, 15).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SetSchedulingClass(wrong, SchedulingPolicy.Batch, 0).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.Restart(wrong).Outcome.Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
    });
  }

  /// <summary>
  /// The identity is checked before the arguments are, on every platform.
  /// </summary>
  /// <remarks>
  /// Both answers are defensible in isolation — the class is unaskable and the key is stale — and
  /// only one of them is the answer to what was asked. The order also has to be the same everywhere:
  /// this ran first on Linux and second on Windows and macOS, where the class is refused outright
  /// and the stale key was therefore never looked at. An action that can reach a platform branch
  /// without having validated its key is one syscall away from acting on the wrong process (§8.2).
  /// </remarks>
  [Test]
  public void TheIdentityIsCheckedBeforeTheArgumentsAre() {
    var actions = Actions();
    var stale = new ProcessKey(1000, 999_999);

    Assert.Multiple(() => {
      // Every one of these arguments is independently wrong: 500 is outside every real-time range,
      // SCHED_DEADLINE cannot be asked for at all, and SCHED_OTHER takes no static priority.
      Assert.That(actions.SetSchedulingClass(stale, SchedulingPolicy.Fifo, 500).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SetSchedulingClass(stale, SchedulingPolicy.Deadline, 0).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SetSchedulingClass(stale, SchedulingPolicy.Other, 9).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
    });
  }

  /// <summary>
  /// A class this call cannot express is named as such, rather than being attempted and failing with
  /// an errno that says nothing about why (PRD §5.3).
  /// </summary>
  [Test]
  public void AClassThatCannotBeSetThisWaySaysWhichAndWhy() {
    var actions = Actions();
    var deadline = actions.SetSchedulingClass(new(1000, 100000), SchedulingPolicy.Deadline, 0);
    var extensible = actions.SetSchedulingClass(new(1000, 100000), SchedulingPolicy.Extensible, 0);

    Assert.Multiple(() => {
      Assert.That(deadline.Outcome, Is.EqualTo(ActionOutcome.NotSupportedOnPlatform));
      Assert.That(deadline.Detail, Does.Contain("SCHED_DEADLINE"));
      Assert.That(extensible.Outcome, Is.EqualTo(ActionOutcome.NotSupportedOnPlatform));
      Assert.That(extensible.Detail, Does.Contain("SCHED_EXT"));
    });
  }

  /// <summary>
  /// The static priority is checked against what the class accepts before anything is attempted, and
  /// the refusal says the range rather than repeating the request back.
  /// </summary>
  [Test]
  [Platform("Linux")]
  public void APriorityTheClassDoesNotTakeIsRefusedWithItsRange() {
    var actions = Actions();

    var ordinary = actions.SetSchedulingClass(new(1000, 100000), SchedulingPolicy.Other, 5);
    Assert.That(ordinary.Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(ordinary.Detail, Does.Contain("no static priority"), ordinary.Detail);

    var realtime = actions.SetSchedulingClass(new(1000, 100000), SchedulingPolicy.Fifo, 500);
    Assert.That(realtime.Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(realtime.Detail, Does.Contain("1 to 99"), realtime.Detail);
  }

}

/// <summary>
/// Ending a whole subtree (PRD §25.1).
/// </summary>
/// <remarks>
/// Against a recording of the calls rather than against real processes: what is being checked is the
/// order and the accounting, and proving those by killing four programs would be a test nobody
/// should run twice.
/// </remarks>
[TestFixture]
public sealed class TerminateTreeTests {

  private sealed class RecordingActions : IProcessActions {

    public List<ProcessKey> Terminated { get; } = [];

    public Dictionary<int, ActionResult> Answers { get; } = [];

    public ActionResult Terminate(ProcessKey key) {
      this.Terminated.Add(key);
      return this.Answers.TryGetValue(key.Pid, out var answer) ? answer : ActionResult.Ok;
    }

    public ActionResult Suspend(ProcessKey key) => ActionResult.Ok;

    public ActionResult Resume(ProcessKey key) => ActionResult.Ok;

    public ActionResult SetPriority(ProcessKey key, int priority) => ActionResult.Ok;

    public ActionResult SetAffinity(ProcessKey key, ulong mask) => ActionResult.Ok;

    public ActionResult SendSignal(ProcessKey key, int signal) => ActionResult.Ok;

  }

  private static readonly ProcessKey[] _Tree = [new(12, 1), new(11, 1), new(10, 1)];

  [Test]
  public void EveryMemberIsEndedInTheOrderItWasGiven() {
    // Deepest first. A tree walked the other way ends the root, whose children are reparented to
    // init and are then no longer findable as its descendants.
    var recorder = new RecordingActions();

    // Through the interface, because that is where the walk lives and where every caller reaches it.
    Assert.That(((IProcessActions)recorder).TerminateTree(_Tree).Succeeded, Is.True);
    Assert.That(recorder.Terminated.Select(k => k.Pid), Is.EqualTo(new[] { 12, 11, 10 }));
  }

  [Test]
  public void AMemberThatHasAlreadyGoneIsNotAFailure() {
    // Killing a parent routinely takes its children with it. Reporting that race as a failure would
    // make the ordinary case look broken.
    var recorder = new RecordingActions();
    recorder.Answers[11] = ActionResult.Fail(ActionOutcome.ProcessExited, "gone");

    Assert.That(((IProcessActions)recorder).TerminateTree(_Tree).Succeeded, Is.True);
  }

  [Test]
  public void ARefusalSaysHowMuchOfTheTreeWentAndWhyTheRestDidNot() {
    var recorder = new RecordingActions();
    recorder.Answers[10] = ActionResult.Fail(ActionOutcome.NotPermitted, "not permitted as this user");

    var result = ((IProcessActions)recorder).TerminateTree(_Tree);

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.NotPermitted));
    Assert.That(result.Detail, Does.Contain("ended 2 of 3"));
    Assert.That(result.Detail, Does.Contain("not permitted as this user"));
    Assert.That(recorder.Terminated, Has.Count.EqualTo(3), "one refusal does not abandon the rest");
  }

  [Test]
  public void AnEmptyTreeIsRefusedRatherThanReportedAsDone() {
    var recorder = new RecordingActions();

    Assert.That(((IProcessActions)recorder).TerminateTree([]).Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(recorder.Terminated, Is.Empty);
  }

}

/// <summary>
/// The actions that can only be proved against a running kernel, checked against the kernel's own
/// tools rather than against the code that set them (PRD §9.1).
/// </summary>
/// <remarks>
/// Every process here is one this fixture started itself and cleans up after, and every one of them
/// exits on its own if the clean-up fails. Nothing here touches a process it did not create.
/// </remarks>
[TestFixture]
[Platform("Linux")]
public sealed class LiveProcessActionTests {

  private static LinuxProcessActions Actions() => new(new());

  private static void Stop(int pid) {
    if (pid <= 0)
      return;

    try {
      System.Diagnostics.Process.GetProcessById(pid).Kill();
    } catch (ArgumentException) {
    } catch (InvalidOperationException) {
    }
  }

  /// <summary>The kernel's one-letter state for a pid, or null when there is no such process.</summary>
  private static string? StateOf(int pid) {
    try {
      return File.ReadAllText($"/proc/{pid}/stat").Split(' ')[2];
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  /// <summary>What a system tool says, so an assertion is against the kernel and not against us.</summary>
  private static string Ask(string program, params string[] arguments) {
    var start = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = System.Diagnostics.Process.Start(start);
    if (process is null)
      return string.Empty;

    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output;
  }

  [Test]
  [TestCase(SchedulingPolicy.Idle, "SCHED_IDLE")]
  [TestCase(SchedulingPolicy.Batch, "SCHED_BATCH")]
  [TestCase(SchedulingPolicy.Other, "SCHED_OTHER")]
  public void AClassChangeIsWhatChrtThenReports(SchedulingPolicy policy, string expected) {
    var actions = Actions();
    var started = actions.Launch(new("/bin/sleep", ["120"]));

    try {
      Assert.That(started.Outcome.Succeeded, Is.True, started.Outcome.Detail);

      var result = actions.SetSchedulingClass(started.Key, policy, 0);
      Assert.That(result.Succeeded, Is.True, result.Detail);

      // The kernel's own answer. If ours ever disagrees with chrt, the kernel is right.
      Assert.That(Ask("/usr/bin/chrt", "-p", started.Pid.ToString()), Does.Contain(expected));
    } finally {
      Stop(started.Pid);
    }
  }

  /// <summary>
  /// A real-time class is not reachable without privilege, and the refusal says which privilege
  /// rather than reporting a bare "not permitted" that sends people looking in the wrong place.
  /// </summary>
  [Test]
  public void ARealTimeClassWithoutPrivilegeSaysWhichPrivilegeIsMissing() {
    if (Environment.IsPrivilegedProcess)
      Assert.Ignore("run as root, where this is permitted and there is nothing to refuse");

    var actions = Actions();
    var started = actions.Launch(new("/bin/sleep", ["120"]));

    try {
      var result = actions.SetSchedulingClass(started.Key, SchedulingPolicy.Fifo, 50);

      Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.NotPermitted));
      Assert.That(result.Detail, Does.Contain("CAP_SYS_NICE"));
      Assert.That(Ask("/usr/bin/chrt", "-p", started.Pid.ToString()), Does.Contain("SCHED_OTHER"), "and nothing changed");
    } finally {
      Stop(started.Pid);
    }
  }

  /// <summary>
  /// Dropping a process into <c>SCHED_IDLE</c> needs no privilege and taking it back out of one
  /// nearly always does, which is surprising enough that the refusal has to say why.
  /// </summary>
  /// <remarks>
  /// The kernel scores <c>SCHED_IDLE</c> as nice 20, so leaving it is a promotion and is permitted
  /// only where <c>RLIMIT_NICE</c> reaches that far — at the default limit of 0 it never does. The
  /// first version of this reported it as an ordinary permission problem, which sends somebody
  /// looking for a permission that is not the one in the way.
  /// </remarks>
  [Test]
  public void LeavingTheIdleClassExplainsItselfRatherThanSayingNotPermitted() {
    if (Environment.IsPrivilegedProcess)
      Assert.Ignore("run as root, where CAP_SYS_NICE is held and there is nothing to refuse");

    var actions = Actions();
    var started = actions.Launch(new("/bin/sleep", ["120"]));

    try {
      Assert.That(actions.SetSchedulingClass(started.Key, SchedulingPolicy.Idle, 0).Succeeded, Is.True, "going in is free");

      var back = actions.SetSchedulingClass(started.Key, SchedulingPolicy.Other, 0);
      Assert.That(back.Outcome, Is.EqualTo(ActionOutcome.NotPermitted));
      Assert.That(back.Detail, Does.Contain("nice 20"), back.Detail);
      Assert.That(back.Detail, Does.Contain("RLIMIT_NICE"), back.Detail);
    } finally {
      Stop(started.Pid);
    }
  }

  /// <summary>
  /// A restart is the same program, with the same arguments, in the same directory — and a different
  /// process, which is the point of asking for one.
  /// </summary>
  [Test]
  public void ARestartIsTheSameProgramAsANewProcess() {
    var directory = Path.Combine(Path.GetTempPath(), $"procman restart test {Environment.ProcessId}");
    Directory.CreateDirectory(directory);

    var actions = Actions();
    var started = actions.Launch(new("/bin/sleep", ["1200"], directory));
    var restarted = default(LaunchResult);

    try {
      Assert.That(started.Outcome.Succeeded, Is.True, started.Outcome.Detail);

      restarted = actions.Restart(started.Key);
      Assert.That(restarted.Outcome.Succeeded, Is.True, restarted.Outcome.Detail);
      Assert.That(restarted.Pid, Is.Not.EqualTo(started.Pid), "a restart is a new process");

      // "Gone" and "not running" are not the same directory listing. This fixture started the old
      // process itself, so it is its parent, and a terminated child stays in the process table as a
      // zombie until the runtime's reaper gets to it — which it does on its own schedule and not on
      // this test's. A zombie has exited, holds no socket and no lock, and is exactly what the
      // restart is entitled to have started a replacement for.
      Assert.That(StateOf(started.Pid), Is.Null.Or.EqualTo("Z"), "the old one is no longer running");

      Assert.That(File.ReadAllText($"/proc/{restarted.Pid}/cmdline").Split('\0'), Does.Contain("1200"), "with its arguments");
      Assert.That(
        Directory.ResolveLinkTarget($"/proc/{restarted.Pid}/cwd", true)?.FullName,
        Is.EqualTo(directory),
        "in the directory it was running in"
      );
    } finally {
      Stop(started.Pid);
      Stop(restarted.Pid);
      Directory.Delete(directory, recursive: true);
    }
  }

  /// <summary>
  /// A process with no window has nothing to ask, so ending its task politely is <c>SIGTERM</c> —
  /// and the result says that is what happened rather than implying a dialog appeared somewhere.
  /// </summary>
  [Test]
  public void EndingATaskWithNoWindowFallsBackToTheSignalAndSaysSo() {
    var actions = Actions();
    var started = actions.Launch(new("/bin/sleep", ["120"]));

    try {
      var result = actions.EndTask(started.Key);

      Assert.That(result.Succeeded, Is.True, result.Detail);
      Assert.That(result.Detail, Does.Contain("SIGTERM"));

      // Gone, or a zombie this fixture has not reaped yet — see ARestartIsTheSameProgramAsANewProcess
      // for why the two are the same answer to "is it still running".
      var deadline = Environment.TickCount64 + 5000;
      while (StateOf(started.Pid) is not (null or "Z") && Environment.TickCount64 < deadline)
        Thread.Sleep(10);

      Assert.That(StateOf(started.Pid), Is.Null.Or.EqualTo("Z"), "sleep does not catch SIGTERM");
    } finally {
      Stop(started.Pid);
    }
  }

}
