using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Signals and scheduler changes through libc.
/// </summary>
/// <remarks>
/// Every method re-reads the target's start time and compares it against the key before it acts. A
/// pid that was recycled between the moment the user clicked and the moment the syscall runs is a
/// different program, and killing it because the number matched is the failure this check exists to
/// prevent (PRD §8.2).
/// </remarks>
public sealed class LinuxProcessActions(LinuxProbeOptions? options = null) : IProcessActions {

  private readonly LinuxProbeOptions _options = options ?? new();
  private readonly ProcFileReader _reader = new(
    (options ?? new()).UsePortableFileAccess ? new ManagedProcIo() : ProcIo.ForCurrentPlatform
  );

  /// <summary>How long a program started suspended is given to reach state <c>T</c>.</summary>
  private static readonly TimeSpan _SuspendGrace = TimeSpan.FromMilliseconds(2000);

  /// <summary>How long a process is given to act on <c>SIGTERM</c> before a restart gives up.</summary>
  /// <remarks>
  /// Generous on purpose. A program asked to stop is often writing something out, and a restart that
  /// hurried it would be the data loss the polite signal exists to avoid. What must not happen is a
  /// second copy started beside a first that is still running, which is why the timeout refuses
  /// rather than escalating to <c>SIGKILL</c>: escalating is a decision for whoever is watching.
  /// </remarks>
  private static readonly TimeSpan _RestartGrace = TimeSpan.FromSeconds(5);

  private const string _Shell = "/bin/sh";

  public ActionResult Terminate(ProcessKey key) => this.Signal(key, Native.SIGTERM);

  /// <summary>
  /// Asks the program to close, and only signals it if there is nothing to ask (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// <para>
  /// On a desktop this is a <c>WM_DELETE_WINDOW</c> to each window the process owns — the same
  /// message its close button sends, and the one its toolkit's handler is written for. It is what
  /// makes an editor offer to save rather than disappear, and it is the whole difference between
  /// this and <see cref="Terminate"/>.
  /// </para>
  /// <para>
  /// A process with no window has nothing to ask, and then <c>SIGTERM</c> is the polite request: it
  /// is what a daemon's own handler exists for. The two cases are reported distinctly, because
  /// "nothing answered so it was signalled" and "it was asked and is thinking about it" lead to
  /// different next actions.
  /// </para>
  /// </remarks>
  public ActionResult EndTask(ProcessKey key) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    // Only against the machine's own processes. A fixture replay's pid 1000 is a recording, while
    // the windows on this display belong to whatever this machine's pid 1000 happens to be, and
    // asking those to close would act on a process nobody named (PRD §9.1).
    var sessionAnswered = false;
    if (this._options.ProcRoot == LinuxProbeOptions.LiveProcRoot) {
      int asked;
      (sessionAnswered, asked) = X11Windows.AskToClose(key.Pid);
      if (asked > 0)
        return new(
          ActionOutcome.Succeeded,
          asked == 1
            ? "its window was asked to close; whether it does is the program's own decision"
            : $"its {asked} windows were asked to close; whether they do is the program's own decision"
        );
    }

    var signalled = this.Signal(key, Native.SIGTERM);
    return signalled.Succeeded
      ? new(
        ActionOutcome.Succeeded,
        sessionAnswered
          ? "it has no window to ask, so SIGTERM was sent instead"
          : "this session does not report windows, so SIGTERM was sent instead"
      )
      : signalled;
  }

  /// <summary>
  /// Ends a process and starts it again as it was (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Everything needed to start it again is read <em>before</em> anything is sent, because none of
  /// it is readable afterwards: <c>/proc/[pid]/exe</c>, <c>cwd</c> and <c>cmdline</c> all vanish
  /// with the process.
  /// </para>
  /// <para>
  /// The replacement inherits this program's environment rather than the old process's. That is a
  /// deliberate limit and not an oversight: <c>/proc/[pid]/environ</c> is the block the kernel laid
  /// down at <c>exec</c>, so for any process that has since called <c>setenv</c> it is stale, and
  /// there is no way from outside to tell a stale block from a current one. Copying it would produce
  /// a "restart" that quietly differs from the process it replaced (PRD §5.3).
  /// </para>
  /// <para>
  /// If the process has not gone within <see cref="_RestartGrace"/>, no replacement is started. Two
  /// copies of a program that guards a socket or a lock file is a worse outcome than a restart that
  /// says it did not happen.
  /// </para>
  /// <para>
  /// The replacement is a child of <em>this</em> program rather than of whatever started the
  /// original, which nothing outside the kernel can change: a process can only be forked by its
  /// parent. It survives this program exiting — it is reparented to init like any other orphan — but
  /// a service manager that was watching the old one is not watching the new one, so restarting
  /// something a supervisor owns is that supervisor's job and not this one's (PRD §41).
  /// </para>
  /// </remarks>
  public LaunchResult Restart(ProcessKey key) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return new(check, 0, default);

    if (this.DescribeForRestart(key) is not { } request)
      return new(
        ActionResult.Fail(
          ActionOutcome.NotPermitted,
          $"what pid {key.Pid} was started with cannot be read, so it cannot be started again"
        ),
        0,
        default
      );

    var ended = this.Signal(key, Native.SIGTERM);
    if (!ended.Succeeded && ended.Outcome != ActionOutcome.ProcessExited)
      return new(ended, 0, default);

    if (!this.WaitForExit(key, _RestartGrace))
      return new(
        ActionResult.Fail(
          ActionOutcome.Refused,
          $"pid {key.Pid} was still running {_RestartGrace.TotalSeconds:0} s after being asked to stop, so no second copy was started"
        ),
        0,
        default
      );

    return this.Launch(request);
  }

  /// <summary>
  /// What a running process would have to be started with to be started again, or null when the
  /// kernel will not say.
  /// </summary>
  /// <remarks>
  /// The executable comes from the <c>exe</c> link rather than from <c>argv[0]</c>, which a program
  /// may set to anything it likes. For a script that means the interpreter, and <c>argv[1]</c> is
  /// then the script — which is exactly the pair needed to run it again.
  /// </remarks>
  private LaunchRequest? DescribeForRestart(ProcessKey key) {
    var root = this._options.ProcRoot.TrimEnd('/');
    var executable = this._reader.TryReadLink($"{root}/{key.Pid}/exe");
    if (executable is null)
      return null;

    // The kernel marks a replaced or removed image this way. Starting the path again would start
    // whatever now occupies it, which is not what was running.
    if (executable.EndsWith(" (deleted)", StringComparison.Ordinal))
      return null;

    // NUL-separated with a trailing NUL. Split here rather than reusing the joined command line the
    // sampler shows: that one is joined with spaces for a person to read, and re-splitting it would
    // break every argument that contains one.
    var arguments = new List<string>();
    if (this._reader.TryRead($"{root}/{key.Pid}/cmdline", out var content, out _))
      for (int start = 0, i = 0; i <= content.Length; ++i) {
        if (i < content.Length && content[i] != 0)
          continue;

        if (i > start)
          arguments.Add(System.Text.Encoding.UTF8.GetString(content[start..i]));

        start = i + 1;
      }

    // argv[0] is the program's own name, not an argument to it, and is dropped rather than passed on.
    if (arguments.Count > 0)
      arguments.RemoveAt(0);

    // Null rather than the current directory when it cannot be read: an unreadable cwd means the
    // replacement inherits this program's, which is at least a directory that exists.
    var directory = this._reader.TryReadLink($"{root}/{key.Pid}/cwd");
    return new(executable, arguments, Directory.Exists(directory) ? directory : null);
  }

  /// <summary>
  /// Which class the kernel runs the process under, and where it sits inside it (PRD §25.2).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Not the same control as nice, and not on the same scale. Nice orders processes <em>within</em>
  /// <c>SCHED_OTHER</c>; this changes which rules order them at all. A task at <c>SCHED_IDLE</c> is
  /// not merely last in the queue — it runs only when the machine has nothing else to run, which is
  /// what makes a re-index invisible rather than merely quieter.
  /// </para>
  /// <para>
  /// <b>Raising into a real-time class is deliberately not routed through the privileged helper</b>,
  /// for the same reason the real-time I/O class is not (PRD §68): a <c>SCHED_FIFO</c> task that
  /// spins never yields the processor it is on, and on a single-core machine that is the end of the
  /// session. Somebody should take that decision at a root prompt, not from a menu.
  /// </para>
  /// </remarks>
  public ActionResult SetSchedulingClass(ProcessKey key, SchedulingPolicy policy, int priority) {
    // Identity first, before the class is looked at and before the priority is checked against it.
    // Every other order lets a request naming a process that no longer exists come back saying
    // something about the class instead — which reads as a fault in the request rather than as the
    // one thing that actually mattered, and on a platform where a later branch refuses outright the
    // stale key would never be looked at at all (PRD §8.2).
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    if (PolicyNumber(policy) is not { } number)
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        policy switch {
          // Both exist, and neither can be asked for through this call: a deadline task is described
          // by a runtime, a period and a deadline that sched_setscheduler has nowhere to put, and the
          // extensible class belongs to whichever BPF scheduler is loaded, if any.
          SchedulingPolicy.Deadline => "SCHED_DEADLINE needs a runtime, a period and a deadline, which this call cannot carry",
          SchedulingPolicy.Extensible => "SCHED_EXT is set by the loaded BPF scheduler, not from outside",
          _ => $"{Query.Humanize.SchedulingPolicy(policy)} is not a class this kernel can be asked for",
        }
      );

    if (Native.SchedulerPriorityRange(number) is not { } range)
      return ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, $"this kernel does not know {Query.Humanize.SchedulingPolicy(policy)}");

    if (priority < range.Min || priority > range.Max)
      return ActionResult.Fail(
        ActionOutcome.Refused,
        range.Min == range.Max
          ? $"{Query.Humanize.SchedulingPolicy(policy)} has no static priority; it takes {range.Min} and nothing else"
          : $"{Query.Humanize.SchedulingPolicy(policy)} takes a static priority of {range.Min} to {range.Max}, not {priority}"
      );

    if (Native.SetScheduler(key.Pid, number, priority) == 0)
      return ActionResult.Ok;

    var errno = Native.LastError;
    var what = $"could not move pid {key.Pid} to {Query.Humanize.SchedulingPolicy(policy)}";
    if (errno is not (Native.EPERM or Native.EACCES))
      return Translate(errno, what);

    if (policy is SchedulingPolicy.Fifo or SchedulingPolicy.RoundRobin)
      return ActionResult.Fail(
        ActionOutcome.NotPermitted,
        $"{what}: a real-time class needs CAP_SYS_NICE or an RLIMIT_RTPRIO that allows it"
      );

    // The surprise this message exists for. Dropping a process into SCHED_IDLE needs no privilege
    // and taking it back out of one nearly always does: the kernel scores SCHED_IDLE as nice 20, so
    // leaving it is a promotion, and it is permitted only where RLIMIT_NICE reaches that far — which
    // at the default limit of 0 it never does. Without this the refusal reads as an ordinary
    // permission problem and sends people looking for one that is not there.
    if (this.TryReadStat(key, matchIdentity: true, out var current) && current.SchedulingPolicy == SchedulingPolicy.Idle)
      return ActionResult.Fail(
        ActionOutcome.NotPermitted,
        $"{what}: the kernel counts SCHED_IDLE as nice 20, so leaving it is a promotion and needs CAP_SYS_NICE or an RLIMIT_NICE that reaches it"
      );

    return Translate(errno, what);
  }

  /// <summary>The kernel's own number for a class, or null for one that cannot be set this way.</summary>
  private static int? PolicyNumber(SchedulingPolicy policy) => policy switch {
    SchedulingPolicy.Other => 0,
    SchedulingPolicy.Fifo => 1,
    SchedulingPolicy.RoundRobin => 2,
    SchedulingPolicy.Batch => 3,
    SchedulingPolicy.Idle => 5,
    _ => null,
  };

  public ActionResult Suspend(ProcessKey key) => this.Signal(key, Native.SIGSTOP);

  public ActionResult Resume(ProcessKey key) => this.Signal(key, Native.SIGCONT);

  /// <summary>
  /// Sends any signal the kernel has (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// The number is checked against what a signal number can be before it is sent, because
  /// <c>kill</c> with a nought is not a refusal — it is the existence test, which succeeds silently
  /// and does nothing, and a caller who mistyped a name would be told the action worked.
  /// </remarks>
  public ActionResult SendSignal(ProcessKey key, int signal) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    return Query.Signals.IsSendable(signal)
      ? this.Signal(key, signal)
      : ActionResult.Fail(ActionOutcome.Refused, $"{signal} is not a signal number this kernel has");
  }

  /// <summary>
  /// How likely the out-of-memory killer is to choose this process (PRD §25.5).
  /// </summary>
  /// <remarks>
  /// <para>
  /// <b>Not a memory limit.</b> It changes nothing about what the process may allocate — it changes
  /// who dies when the machine has run out and something has to. Lowering one process's score does
  /// not save memory; it points the killer at whatever is next on the list, which is somebody else's
  /// process (PRD §5.5).
  /// </para>
  /// <para>
  /// Raising it is free and lowering it needs <c>CAP_SYS_RESOURCE</c>, which is the opposite way
  /// round from most permissions and worth saying out loud: a process may always volunteer itself,
  /// and may never excuse itself. <b>Deliberately not routed through the privileged helper</b>, for
  /// the reason the real-time I/O class is not: making one process harder to kill makes every other
  /// process on the machine likelier to be chosen, and that is a decision to take at a root prompt
  /// rather than from a menu (PRD §68).
  /// </para>
  /// </remarks>
  public ActionResult SetOomScoreAdjustment(ProcessKey key, int adjustment) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    if (adjustment < ProcessLimits.OomAdjustmentMinimum || adjustment > ProcessLimits.OomAdjustmentMaximum)
      return ActionResult.Fail(
        ActionOutcome.Refused,
        $"the out-of-memory adjustment runs from {ProcessLimits.OomAdjustmentMinimum} to {ProcessLimits.OomAdjustmentMaximum}, not {adjustment}"
      );

    var path = $"{this._options.ProcRoot.TrimEnd('/')}/{key.Pid}/oom_score_adj";
    if (WriteControlFile(path, adjustment.ToString(System.Globalization.CultureInfo.InvariantCulture), out var errno))
      return ActionResult.Ok;

    var what = $"could not set the out-of-memory adjustment of pid {key.Pid}";
    return errno is Native.EPERM or Native.EACCES
      ? this.ExplainOomRefusal(key, adjustment, what)
      : Translate(errno, what);
  }

  /// <summary>
  /// Says which of the two refusals this is, because they send somebody to different places.
  /// </summary>
  /// <remarks>
  /// "Not permitted" on its own is the unhelpful answer here. Lowering the score is refused for
  /// everybody without <c>CAP_SYS_RESOURCE</c> including the process's own owner, while another
  /// user's process is refused whichever direction the change goes — and only one of those two is
  /// fixed by being the right user.
  /// </remarks>
  private ActionResult ExplainOomRefusal(ProcessKey key, int adjustment, string what) {
    var current = LinuxResourceLimits.Read(this._options.ProcRoot, key.Pid)?.OomScoreAdjustment;
    return ActionResult.Fail(
      ActionOutcome.NotPermitted,
      current is { } was && adjustment < was
        ? $"{what} from {was} to {adjustment}: a process may always volunteer itself for the "
          + "out-of-memory killer and needs CAP_SYS_RESOURCE to excuse itself again"
        : $"{what}: not permitted as this user"
    );
  }

  /// <summary>
  /// Writes a short value to a kernel control file, with the errno the kernel gave for refusing it.
  /// </summary>
  /// <remarks>
  /// The errno is the point. These files accept the open and refuse the write, and "not permitted"
  /// and "the process has gone" arrive as different numbers on the same exception type otherwise
  /// (PRD §88).
  /// </remarks>
  private static bool WriteControlFile(string path, string value, out int errno) {
    // Allocated rather than stack-composed. Every other path in this program is built into a stack
    // buffer because the sampler builds thousands a second; this one is built once per click, and a
    // cgroup path under a container runtime is long enough that a fixed buffer would be a length
    // limit rather than an optimisation.
    var encoded = new byte[System.Text.Encoding.UTF8.GetByteCount(path) + 1];
    System.Text.Encoding.UTF8.GetBytes(path, encoded);
    return Native.WriteControlFile(encoded, System.Text.Encoding.UTF8.GetBytes(value), out errno);
  }

  /// <summary>
  /// One of the kernel's per-process ceilings (PRD §25.2).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Through <c>prlimit64</c> rather than by writing a file, because there is no file to write:
  /// <c>/proc/[pid]/limits</c> is read-only and the syscall is the only way in. That is why the
  /// reading half of this feature parses the text and the writing half does not (PRD §5.1).
  /// </para>
  /// <para>
  /// <b>Lowering a hard limit cannot be undone</b> without <c>CAP_SYS_RESOURCE</c>. The kernel
  /// permits it to anybody and permits nobody to raise it again, which makes it the one irreversible
  /// thing in this sheet and the one a front-end has to say so about (PRD §5.5).
  /// </para>
  /// </remarks>
  public ActionResult SetResourceLimit(ProcessKey key, ResourceLimitKind kind, ulong? soft, ulong? hard) {
    // Identity first, before the architecture is looked at and before the values are checked against
    // each other — every other order lets a request naming a process that no longer exists come back
    // saying something about the arguments instead (PRD §8.2).
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    if (Query.ResourceLimits.Of(kind) is not { } definition)
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        Query.ResourceLimits.NumbersAreKnownHere
          ? $"{kind} is not a limit this kernel has"
          : Query.ResourceLimits.UnknownArchitecture
      );

    // Refused here rather than left to come back as EINVAL, which says nothing about which of the
    // two values was the problem.
    if (soft is { } wantedSoft && hard is { } wantedHard && wantedSoft > wantedHard)
      return ActionResult.Fail(
        ActionOutcome.Refused,
        $"{definition.Name}: a soft limit of {wantedSoft} is above the hard limit of {wantedHard}, and the soft limit is the one the kernel enforces"
      );

    var pair = new Native.ResourceLimitPair {
      Soft = soft ?? Native.ResourceLimitInfinity,
      Hard = hard ?? Native.ResourceLimitInfinity,
    };

    if (Native.SetResourceLimit(key.Pid, definition.Number, in pair) == 0)
      return ActionResult.Ok;

    var errno = Native.LastError;
    var what = $"could not set {definition.Name} on pid {key.Pid}";

    // The refusal people run into and misread. Raising a hard limit needs CAP_SYS_RESOURCE whoever
    // owns the process, and reaching into another user's process needs it as well — the second is
    // fixed by being the right user and the first is not.
    if (errno is Native.EPERM or Native.EACCES)
      return ActionResult.Fail(
        ActionOutcome.NotPermitted,
        $"{what}: raising a hard limit needs CAP_SYS_RESOURCE, and so does setting a limit on a process belonging to another user"
      );

    return Translate(errno, what);
  }

  /// <summary>
  /// Stops or restarts the whole cgroup the process is in (PRD §25.1, §38).
  /// </summary>
  /// <remarks>
  /// <para>
  /// <b>Not a suspend of the process.</b> <see cref="Suspend"/> sends one process <c>SIGSTOP</c> and
  /// leaves everything it started running; this stops the unit — every process in the cgroup, every
  /// cgroup below it, and anything either starts while it is frozen. On Linux that is what pausing a
  /// container or a service means, and the two are offered as separate items because they are
  /// separate things (PRD §5.3).
  /// </para>
  /// <para>
  /// <b>A frozen process still reports itself as sleeping.</b> The kernel has no process state for
  /// frozen — a task in a frozen cgroup shows <c>S</c> in <c>/proc/[pid]/stat</c> whether it was
  /// running or sleeping when the freeze landed — so nothing in a process table distinguishes a
  /// frozen program from an idle one, and the only honest place to read it is the cgroup's own
  /// <c>cgroup.events</c>. That is why the result names the cgroup: it is the only thing that will
  /// admit afterwards to what was done. Held against the kernel rather than assumed: the task's
  /// CPU time stops advancing while its state still says <c>S</c>.
  /// </para>
  /// <para>
  /// A fatal signal does still reach it. Unlike the cgroup v1 freezer, v2 breaks a frozen task out
  /// for <c>SIGKILL</c> and for anything else that would end it, so a frozen process is not one
  /// that has to be thawed before it can be stopped.
  /// </para>
  /// </remarks>
  public ActionResult FreezeCgroup(ProcessKey key, bool frozen) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    if (this.CgroupPathOf(key.Pid) is not { } path)
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        $"pid {key.Pid} is in no cgroup this build can read; only the unified hierarchy (cgroup v2) has a freezer"
      );

    // The root cgroup holds every process on the machine, this program among them. The kernel does
    // not publish cgroup.freeze there for that reason, and this refuses before finding out.
    if (path is "/")
      return ActionResult.Fail(ActionOutcome.Refused, "the root cgroup holds every process on this machine and cannot be frozen");

    // Freezing the cgroup this program is in stops this program, which then cannot report what
    // happened or thaw anything again. The one case where the honest answer is to refuse rather
    // than to do what was asked.
    if (frozen && this.CgroupPathOf(Environment.ProcessId) is { } ours && (ours == path || ours.StartsWith(path + "/", StringComparison.Ordinal)))
      return ActionResult.Fail(
        ActionOutcome.Refused,
        $"{path} contains this program as well; freezing it would stop the window that is asking and leave nothing able to thaw it"
      );

    var file = Path.Combine(this._options.CgroupRoot, path.TrimStart('/'), "cgroup.freeze");
    if (!File.Exists(file))
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        $"{path} has no cgroup.freeze; the freezer arrived in Linux 5.2 and needs the unified hierarchy"
      );

    if (!WriteControlFile(file, frozen ? "1" : "0", out var errno))
      return errno is Native.EPERM or Native.EACCES
        ? ActionResult.Fail(
            ActionOutcome.NotPermitted,
            $"{path} may not be frozen by this user: a cgroup is writable by whoever it was delegated to, "
            + "which for a service or a container is root"
          )
        : Translate(errno, $"could not {(frozen ? "freeze" : "thaw")} {path}");

    return new(
      ActionOutcome.Succeeded,
      frozen
        ? $"{path} is frozen. Every process in it is stopped — and each still reports itself as sleeping, "
          + "because the kernel has no process state for frozen."
        : $"{path} is thawed."
    );
  }

  /// <summary>
  /// The unified hierarchy's path for a pid, or null where there is not one.
  /// </summary>
  /// <remarks>
  /// The line beginning <c>0::</c> is the v2 one. A v1 machine has several lines and none of them
  /// begins that way, which is how a v1 layout reports itself as unreadable rather than as half an
  /// answer — the same rule <c>DescribeCgroup</c> follows.
  /// </remarks>
  private string? CgroupPathOf(int pid) {
    var path = $"{this._options.ProcRoot.TrimEnd('/')}/{pid}/cgroup";
    if (!this._reader.TryRead(path, out var content, out _))
      return null;

    foreach (var line in System.Text.Encoding.UTF8.GetString(content).Split('\n'))
      if (line.StartsWith("0::", StringComparison.Ordinal))
        return line[3..].Trim();

    return null;
  }

  public ActionResult SetPriority(ProcessKey key, int priority) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    if (Native.SetNice(key.Pid, priority) == 0)
      return ActionResult.Ok;

    var errno = Native.LastError;
    return errno is Native.EPERM or Native.EACCES
      ? this.ThroughHelper(ElevatedOpcode.SetPriority, key, priority, $"could not set nice to {priority}")
      : Translate(errno, $"could not set nice to {priority}");
  }

  public ActionResult SetAffinity(ProcessKey key, ulong mask) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;
    if (mask == 0)
      return ActionResult.Fail(ActionOutcome.Refused, "an affinity mask with no cores in it would leave nothing to run on");

    if (Native.SetAffinityMask(key.Pid, mask) == 0)
      return ActionResult.Ok;

    var errno = Native.LastError;
    return errno is Native.EPERM or Native.EACCES
      ? this.ThroughHelper(ElevatedOpcode.SetAffinity, key, (long)mask, "could not set CPU affinity")
      : Translate(errno, "could not set CPU affinity");
  }

  /// <summary>
  /// Starts a process (PRD §54).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The scheduling parts are applied after it exists, because there is no portable way to start a
  /// process that is already niced. So a launch can succeed while its priority does not — the result
  /// says the process started and names what could not be applied, rather than reporting a failure
  /// for a program that is now running.
  /// </para>
  /// <para>
  /// Suspended means stopped before it has run any of its own code, and it means it literally. The
  /// obvious implementation — start the program, then send it <c>SIGSTOP</c> — is a race the caller
  /// loses: between <c>exec</c> and the signal arriving the program has already run, which is the
  /// one thing "start suspended" exists to prevent. See <see cref="StartSuspended"/> for how it is
  /// closed.
  /// </para>
  /// </remarks>
  public LaunchResult Launch(LaunchRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    if (string.IsNullOrWhiteSpace(request.FileName))
      return LaunchResult.Failed(ActionOutcome.Refused, "there is no program to start");

    var start = new System.Diagnostics.ProcessStartInfo {
      // The shell is not involved: it would re-split and re-glob arguments that have already been
      // split, and every program that tries gets quoting wrong for at least one shell.
      UseShellExecute = false,
    };

    if (!request.Suspended)
      start.FileName = request.FileName;
    else if (StartSuspended(request, start) is { } refusal)
      return refusal;

    foreach (var argument in request.Arguments)
      start.ArgumentList.Add(argument);

    if (request.WorkingDirectory is { Length: > 0 } directory) {
      if (!Directory.Exists(directory))
        return LaunchResult.Failed(ActionOutcome.Refused, $"there is no directory {directory}");

      start.WorkingDirectory = directory;
    }

    // Overrides rather than a replacement: a process started with an emptied environment loses its
    // locale, its display and its path, which is never what somebody setting one variable meant.
    if (request.Environment is { } overrides)
      foreach (var (name, value) in overrides)
        start.Environment[name] = value;

    System.Diagnostics.Process started;
    try {
      started = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("no process");
    } catch (System.ComponentModel.Win32Exception e) {
      return LaunchResult.Failed(ActionOutcome.Failed, $"could not start {request.FileName}: {e.Message}");
    } catch (InvalidOperationException e) {
      return LaunchResult.Failed(ActionOutcome.Failed, $"could not start {request.FileName}: {e.Message}");
    }

    // A caller who asked for the program to be held before its first instruction is not told it
    // happened until the kernel says the task is stopped. Bounded, because the front-end that asked
    // is waiting on this call: a machine under load can take a few milliseconds to schedule the
    // shell, and an unbounded wait would hang the window rather than report anything.
    if (request.Suspended && !this.WaitForState(started.Id, ProcessState.Stopped, _SuspendGrace))
      return new(
        ActionResult.Fail(
          ActionOutcome.Failed,
          $"{request.FileName} was started but had not stopped {_SuspendGrace.TotalMilliseconds:0} ms later"
        ),
        started.Id,
        this.KeyOf(started.Id)
      );

    // The program is running, so the launch succeeded. Whether its identity could be read back and
    // whether the scheduling took are separate questions, answered below.
    var key = this.KeyOf(started.Id);
    var wanted = request.Nice is not null || request.AffinityMask != 0 || request.IoPriority is not null;
    if (key.Pid == 0)
      // Gone before it could be read. Ordinary for anything short-lived — echo does this — and a
      // successful launch of a brief program, not a failure to start one. Only worth mentioning if
      // there were settings that now cannot be applied to it.
      return new(
        wanted
          ? ActionResult.Fail(ActionOutcome.ProcessExited, $"{request.FileName} finished before its priority could be set")
          : ActionResult.Ok,
        started.Id,
        default
      );

    var refused = new List<string>();
    if (request.Nice is { } nice && !this.SetPriority(key, nice).Succeeded)
      refused.Add("priority");

    if (request.AffinityMask != 0 && !this.SetAffinity(key, request.AffinityMask).Succeeded)
      refused.Add("affinity");

    if (request.IoPriority is { } io && !this.SetIoPriority(key, io).Succeeded)
      refused.Add("I/O priority");

    return refused.Count == 0
      ? new(ActionResult.Ok, started.Id, key)
      : new(ActionResult.Fail(ActionOutcome.NotPermitted, $"started, but its {string.Join(" and ", refused)} could not be set"), started.Id, key);
  }

  /// <summary>
  /// Arranges for the program to be stopped before it has executed a single one of its own
  /// instructions, and fills in <paramref name="start"/> accordingly.
  /// </summary>
  /// <returns>A refusal to hand straight back, or <see langword="null"/> when the request is ready.</returns>
  /// <remarks>
  /// <para>
  /// The program is not what gets started. A shell is, and its one job is to stop <em>itself</em> and
  /// then <c>exec</c> the program — so the task is already in state <c>T</c> when the program's image
  /// is loaded, and there is no window in which it can run. <c>exec</c> keeps the pid, so the pid and
  /// identity reported to the caller are the program's own; until it is resumed, <c>cmdline</c> still
  /// reads as the shell's, because the program has not been loaded yet.
  /// </para>
  /// <para>
  /// <b>Nothing is re-split.</b> The arguments are passed to the shell as positional parameters and
  /// forwarded with <c>"$@"</c>, which no shell re-splits and none re-globs. Interpolating them into
  /// the <c>-c</c> text is what would break that, and is exactly what this does not do.
  /// </para>
  /// <para>
  /// The program is resolved here rather than left to the shell, because a failure to find it has to
  /// be reported to whoever asked instead of becoming an exit status the shell prints on resume.
  /// </para>
  /// </remarks>
  private static LaunchResult? StartSuspended(LaunchRequest request, System.Diagnostics.ProcessStartInfo start) {
    if (ResolveProgram(request.FileName) is not { } program)
      return LaunchResult.Failed(ActionOutcome.Refused, $"could not start {request.FileName}: no program of that name was found");

    if (!File.Exists(_Shell))
      return LaunchResult.Failed(ActionOutcome.NotSupportedOnPlatform, $"starting a program suspended needs {_Shell}, which is not on this machine");

    start.FileName = _Shell;
    start.ArgumentList.Add("-c");
    start.ArgumentList.Add("kill -STOP \"$$\"; exec \"$0\" \"$@\"");
    start.ArgumentList.Add(program);
    return null;
  }

  /// <summary>
  /// Where a program named on a request actually is, resolved the way a shell would resolve it.
  /// </summary>
  /// <remarks>
  /// Only the suspended path needs this: an ordinary launch lets <c>Process.Start</c> search
  /// <c>PATH</c> and turns a miss into an exception this class already reports. Whether the file is
  /// executable is deliberately not checked — that is the kernel's answer to give, and a check here
  /// would only race with the permissions changing underneath it.
  /// </remarks>
  private static string? ResolveProgram(string fileName) {
    if (fileName.Contains('/'))
      return File.Exists(fileName) ? Path.GetFullPath(fileName) : null;

    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries)) {
      var candidate = Path.Combine(directory, fileName);
      if (File.Exists(candidate))
        return candidate;
    }

    return null;
  }

  /// <summary>The identity pair of a process that has just been started, or default if it is gone.</summary>
  private ProcessKey KeyOf(int pid) {
    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    if (!this._reader.TryRead(ProcPath.Build(pathBuffer, System.Text.Encoding.UTF8.GetBytes(this._options.ProcRoot), pid, "stat"u8), out var content, out _))
      return default;

    var record = default(ProcessRecord);
    return LinuxProbe.ParseStat(content, 1, this._options.PageSize, ref record)
      ? new(pid, record.Key.StartTicks)
      : default;
  }

  public ActionResult SetIoPriority(ProcessKey key, IoPriority priority) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    if (!Native.SupportsIoPriority)
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        "the I/O priority syscall numbers for this architecture are not known"
      );

    if (Native.SetIoPriority(key.Pid, priority.Pack()) == 0)
      return ActionResult.Ok;

    // Deliberately not routed through the helper. Raising a process into the real-time I/O class
    // starves every other reader on the machine until it finishes, which is a decision somebody
    // should take at a root prompt rather than by picking a menu item (PRD §68).
    var errno = Native.LastError;
    return errno is Native.EPERM or Native.EACCES && priority.Class == IoPriorityClass.Realtime
      ? ActionResult.Fail(ActionOutcome.NotPermitted, "the real-time I/O class needs CAP_SYS_ADMIN")
      : Translate(errno, $"could not set I/O priority to {priority}");
  }

  /// <summary>
  /// A thread's priority.
  /// </summary>
  /// <remarks>
  /// <c>setpriority(PRIO_PROCESS, tid)</c> — the name is a lie inherited from before Linux had
  /// threads, and the "process" it takes is a tid. That is why this is the same call as the
  /// process-wide one with a different number in it.
  /// <para>
  /// The thread is checked to belong to the process the key names. Without that, a tid from one
  /// process could be passed with another's key and the identity check would pass while the syscall
  /// acted somewhere else entirely.
  /// </para>
  /// </remarks>
  public ActionResult SetThreadPriority(ProcessKey key, int threadId, int priority) {
    var check = this.VerifyThread(key, threadId);
    if (!check.Succeeded)
      return check;

    if (Native.SetNice(threadId, priority) == 0)
      return ActionResult.Ok;

    var errno = Native.LastError;
    // The rule nobody remembers: nice runs backwards, so *lowering* the number is asking for more
    // CPU and needs CAP_SYS_NICE, while raising it is always allowed. "Not permitted" on its own
    // sends people looking for a permission problem that is not there.
    return errno is Native.EPERM or Native.EACCES
      ? ActionResult.Fail(
          ActionOutcome.NotPermitted,
          $"lowering a nice value asks for more CPU and needs CAP_SYS_NICE; raising it to above {priority} is always allowed"
        )
      : Translate(errno, $"could not set thread {threadId} nice to {priority}");
  }

  public ActionResult SetThreadAffinity(ProcessKey key, int threadId, ulong mask) {
    var check = this.VerifyThread(key, threadId);
    if (!check.Succeeded)
      return check;

    if (mask == 0)
      return ActionResult.Fail(ActionOutcome.Refused, "an affinity mask with no cores in it would leave nothing to run on");

    return Native.SetAffinityMask(threadId, mask) == 0
      ? ActionResult.Ok
      : Translate(Native.LastError, $"could not set thread {threadId} affinity");
  }

  /// <summary>
  /// Asks one of the process's windows to come forward, go away, grow, shrink or close (PRD §39).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The key is re-validated first and the platform is asked second, which is the order every action
  /// here follows: a stale key must be refused before anything touches the display, or a recycled pid
  /// would have this program commanding the windows of whatever now holds that number. X11 then
  /// checks the window's own <c>_NET_WM_PID</c> against the same pid, because a window id is reused
  /// the way a tid is (PRD §8.2).
  /// </para>
  /// <para>
  /// Only against the machine's own processes, for the reason <see cref="EndTask"/> gives: a fixture
  /// replay's pid 1000 is a recording, while the windows on this display belong to whatever this
  /// machine's pid 1000 happens to be (PRD §9.1).
  /// </para>
  /// <para>
  /// Success here means the request was delivered and nothing more. A window manager may decline any
  /// of these, and a program asked to close may put up a dialog and carry on — which is the correct
  /// outcome rather than a failure, and the reason the detail says "asked" rather than "closed"
  /// (PRD §72.3).
  /// </para>
  /// </remarks>
  public ActionResult CommandWindow(ProcessKey key, ulong window, WindowCommand command) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    if (command == WindowCommand.None)
      return ActionResult.Fail(ActionOutcome.Refused, "no window command was named");

    if (this._options.ProcRoot != LinuxProbeOptions.LiveProcRoot)
      return ActionResult.Fail(
        ActionOutcome.Refused,
        "this is a recorded process tree; the windows on this display belong to other programs"
      );

    return X11Windows.Command(key.Pid, window, command) switch {
      WindowCommandResult.Sent => new(ActionOutcome.Succeeded, Asked(command)),
      WindowCommandResult.NoSession => ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        "there is no X11 session to ask. A Wayland client cannot be told about other clients' surfaces, "
        + "and a machine with no display has none to command"
      ),
      WindowCommandResult.NotListed => ActionResult.Fail(
        ActionOutcome.Refused,
        $"window {window:x} is no longer a top-level window of this session; it has closed since the list was taken"
      ),
      WindowCommandResult.NotThisProcess => ActionResult.Fail(
        ActionOutcome.IdentityMismatch,
        $"window {window:x} now names a different process; window ids are reused the way pids are"
      ),
      WindowCommandResult.NotHandled => ActionResult.Fail(
        ActionOutcome.Refused,
        "this window does not list WM_DELETE_WINDOW, which is a program saying it does not handle being "
        + "asked to close. The only thing left is severing its connection, which is not a polite close"
      ),
      WindowCommandResult.NoWindowManager => ActionResult.Fail(
        ActionOutcome.Refused,
        "nothing on this session manages windows, so there is nobody to grant the request. Closing a "
        + "window goes to the window itself and still works"
      ),
      _ => ActionResult.Fail(ActionOutcome.Failed, "the display server refused the request"),
    };
  }

  /// <summary>
  /// What was asked, in the past tense of a request rather than of a result.
  /// </summary>
  /// <remarks>
  /// Every one of these is a request the window manager may decline, so none of them claims the thing
  /// happened. "Asked to close" and "closed" are the same distinction §25.1 draws between ending a
  /// task and terminating one, and it is the same reason: only one of the two can still be refused.
  /// </remarks>
  private static string Asked(WindowCommand command) => command switch {
    WindowCommand.Foreground => "the desktop was asked to bring the window forward",
    WindowCommand.Minimize => "the desktop was asked to minimise the window",
    WindowCommand.Maximize => "the desktop was asked to maximise the window",
    WindowCommand.Restore => "the desktop was asked to restore the window",
    _ => "the window was asked to close; whether it does is the program's own decision",
  };

  /// <summary>
  /// The process is what the key says, and the thread belongs to it.
  /// </summary>
  /// <remarks>
  /// Both halves matter. A tid is a number in the same space as a pid, so a stale one may name a
  /// live thread of an unrelated process — checking only the process would let the syscall land
  /// there.
  /// </remarks>
  private ActionResult VerifyThread(ProcessKey key, int threadId) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    var task = Path.Combine(this._options.ProcRoot, key.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "task",
      threadId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    return Directory.Exists(task)
      ? ActionResult.Ok
      : ActionResult.Fail(ActionOutcome.IdentityMismatch, $"thread {threadId} does not belong to process {key.Pid}");
  }

  private ActionResult Signal(ProcessKey key, int signal) {
    var check = this.Verify(key);
    if (!check.Succeeded)
      return check;

    if (this.Discarded(key, signal) is { } discarded)
      return ActionResult.Fail(ActionOutcome.Refused, discarded);

    if (Native.SendSignal(key.Pid, signal) == 0)
      return ActionResult.Ok;

    var errno = Native.LastError;
    if (errno is not (Native.EPERM or Native.EACCES))
      return Translate(errno, $"could not send signal {signal}");

    // Another user's process. The helper can, if the user has authorised one — and it re-validates
    // the identity itself before acting, so this is not the check being skipped (PRD §8.2).
    var opcode = signal switch {
      Native.SIGTERM => ElevatedOpcode.Terminate,
      Native.SIGSTOP => ElevatedOpcode.Suspend,
      Native.SIGCONT => ElevatedOpcode.Resume,
      _ => ElevatedOpcode.None,
    };

    return opcode == ElevatedOpcode.None
      ? Translate(errno, $"could not send signal {signal}")
      : this.ThroughHelper(opcode, key, 0, $"could not send signal {signal}");
  }

  /// <summary>
  /// Why the kernel would throw this signal away rather than deliver it, or null when it would not
  /// (PRD §69).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The same failure <c>kill</c> with a nought would produce, and the reason that one is refused
  /// above: the call returns success, nothing whatever happens, and the person who asked is told the
  /// action worked. Here it is worse than a mistyped signal number, because the two targets it
  /// happens on are the two a person is most likely to be wrong about.
  /// </para>
  /// <para>
  /// <b>Pid 1 is delivered only what it has a handler for.</b> The kernel drops every signal sent to
  /// init that init did not install a handler for, and <c>SIGKILL</c> and <c>SIGSTOP</c> are the two
  /// no process may ever have one for — so those two are discarded by construction and no privilege
  /// changes it. Signals init <em>does</em> handle are left alone and are not this function's
  /// business: <c>systemd</c> handles <c>SIGTERM</c>, and what it does with it is between the
  /// sender and the manual page.
  /// </para>
  /// <para>
  /// <b>A kernel thread never acts on a signal at all.</b> It has no user-space to return to, so
  /// there is no point at which a pending signal is looked at, and even <c>SIGKILL</c> to one is a
  /// successful call that does nothing. They are recognised by their parent — everything descended
  /// from <c>kthreadd</c>, plus <c>kthreadd</c> itself — which is the same test <c>ps</c> uses and
  /// costs a read of a file this program has already opened for the identity check.
  /// </para>
  /// <para>
  /// Only the two undeliverable signals are checked, deliberately. A refusal here is this program
  /// declining to do what was asked, and it may only decline where the kernel's own behaviour is
  /// certain rather than likely.
  /// </para>
  /// </remarks>
  private string? Discarded(ProcessKey key, int signal) {
    if (signal is not (Native.SIGKILL or Native.SIGSTOP))
      return null;

    var name = signal == Native.SIGKILL ? "SIGKILL" : "SIGSTOP";
    if (key.Pid == 1)
      return $"the kernel discards {name} sent to pid 1: init is delivered only the signals it "
        + "installed a handler for, and neither of those two can ever have one";

    if (!this.TryReadStat(key, matchIdentity: true, out var record))
      return null;

    return key.Pid == 2 || record.ParentPid == 2
      ? $"pid {key.Pid} is a kernel thread. It never returns to user space, so it never acts on a "
        + $"signal — {name} would be reported as sent and nothing would happen"
      : null;
  }

  /// <summary>Asks the helper, when there is one. Its refusals are reported as the helper's own.</summary>
  private ActionResult ThroughHelper(ElevatedOpcode opcode, ProcessKey key, long argument, string what) {
    if (this._options.Elevated is not { } channel)
      return ActionResult.Fail(ActionOutcome.NotPermitted, $"{what}: not permitted as this user");

    var (status, _) = channel.Send(opcode, key, argument);
    return status switch {
      ElevatedStatus.Ok => ActionResult.Ok,
      ElevatedStatus.IdentityMismatch => ActionResult.Fail(
        ActionOutcome.IdentityMismatch,
        $"pid {key.Pid} is no longer the process it was; it has been reused by another program"
      ),
      ElevatedStatus.ProcessExited => ActionResult.Fail(ActionOutcome.ProcessExited, $"{what}: the process ended first"),
      ElevatedStatus.NotPermitted => ActionResult.Fail(ActionOutcome.NotPermitted, $"{what}: the helper refused it too"),
      _ => ActionResult.Fail(ActionOutcome.Failed, $"{what}: the helper answered {status}"),
    };
  }

  /// <summary>
  /// Waits until the process the key names is no longer running, or the time is up.
  /// </summary>
  /// <remarks>
  /// A zombie counts as gone. It has exited; what is left is a row in the process table nobody has
  /// reaped, and it will never run again — which is the question being asked. This program is rarely
  /// the parent, so it usually does not arise, but a process this program started itself becomes one
  /// and would otherwise look alive for as long as nobody called <c>wait</c>.
  /// </remarks>
  private bool WaitForExit(ProcessKey key, TimeSpan limit) {
    var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
    while (true) {
      var state = this.ReadState(key);
      if (state is null or ProcessState.Zombie or ProcessState.Dead)
        return true;

      if (Environment.TickCount64 >= deadline)
        return false;

      Thread.Sleep(10);
    }
  }

  /// <summary>Waits for a pid to reach a state, or gives up. Used to confirm a suspended start.</summary>
  private bool WaitForState(int pid, ProcessState wanted, TimeSpan limit) {
    var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
    while (true) {
      // No identity pair yet: this runs against a process this call has just started, so there is
      // nothing that could have recycled the pid in between and nothing to compare it against.
      if (this.ReadState(new(pid, 0), matchIdentity: false) == wanted)
        return true;

      if (Environment.TickCount64 >= deadline)
        return false;

      Thread.Sleep(1);
    }
  }

  /// <summary>
  /// The process's state right now, or null when it is gone or is no longer the one the key names.
  /// </summary>
  private ProcessState? ReadState(ProcessKey key, bool matchIdentity = true)
    => this.TryReadStat(key, matchIdentity, out var record) ? record.State : null;

  /// <summary>The process's own <c>stat</c> line, parsed, if it is still the one the key names.</summary>
  private bool TryReadStat(ProcessKey key, bool matchIdentity, out ProcessRecord record) {
    record = new();
    var path = $"{this._options.ProcRoot.TrimEnd('/')}/{key.Pid}/stat";
    if (!this._reader.TryRead(path, out var content, out _))
      return false;

    if (!LinuxProbe.ParseStat(content, 1, this._options.PageSize, ref record))
      return false;

    return !matchIdentity || record.Key.StartTicks == key.StartTicks;
  }

  /// <summary>Confirms that the pid is still the process the caller meant.</summary>
  private ActionResult Verify(ProcessKey key) {
    if (key.Pid <= 0)
      return ActionResult.Fail(ActionOutcome.Refused, "there is no such pid");

    var path = $"{this._options.ProcRoot.TrimEnd('/')}/{key.Pid}/stat";
    if (!this._reader.TryRead(path, out var content, out var errno))
      return errno is Native.EACCES or Native.EPERM
        ? ActionResult.Fail(ActionOutcome.NotPermitted, "this process may not be read as this user")
        : ActionResult.Fail(ActionOutcome.ProcessExited, "the process has already ended");

    var record = new ProcessRecord();
    if (!LinuxProbe.ParseStat(content, 1, 4096, ref record))
      return ActionResult.Fail(ActionOutcome.Failed, "the process's stat file could not be read");

    return record.Key.StartTicks == key.StartTicks
      ? ActionResult.Ok
      : ActionResult.Fail(
        ActionOutcome.IdentityMismatch,
        $"pid {key.Pid} is no longer the process it was; it has been reused by another program"
      );
  }

  private static ActionResult Translate(int errno, string what) => errno switch {
    Native.EPERM or Native.EACCES
      => ActionResult.Fail(ActionOutcome.NotPermitted, $"{what}: not permitted as this user"),
    Native.ESRCH
      => ActionResult.Fail(ActionOutcome.ProcessExited, $"{what}: the process ended first"),
    _ => ActionResult.Fail(ActionOutcome.Failed, $"{what}: errno {errno}"),
  };

}
