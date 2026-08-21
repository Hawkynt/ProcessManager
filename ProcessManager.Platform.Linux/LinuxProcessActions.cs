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

  public ActionResult Terminate(ProcessKey key) => this.Signal(key, Native.SIGTERM);

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
  /// Suspended means stopped before it has run any of its own code. There is a race here that cannot
  /// be closed from outside: between <c>exec</c> and the signal the program has begun. It is the same
  /// race every tool that offers this has, and it is worth saying so rather than implying the
  /// instruction pointer is at the entry point.
  /// </para>
  /// </remarks>
  public LaunchResult Launch(LaunchRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    if (string.IsNullOrWhiteSpace(request.FileName))
      return LaunchResult.Failed(ActionOutcome.Refused, "there is no program to start");

    var start = new System.Diagnostics.ProcessStartInfo(request.FileName) {
      // The shell is not involved: it would re-split and re-glob arguments that have already been
      // split, and every program that tries gets quoting wrong for at least one shell.
      UseShellExecute = false,
    };

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

    // Suspend first and everything else after: a process being held still cannot outrun the settings
    // being applied to it, and one that was asked to start suspended must not get a chance to run.
    if (request.Suspended)
      Native.SendSignal(started.Id, Native.SIGSTOP);

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
