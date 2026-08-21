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

  public ActionResult SendSignal(ProcessKey key, int signal) => this.Signal(key, signal);

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
