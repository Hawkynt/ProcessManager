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
