using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Terminate, suspend, resume, priority and affinity through the Win32 API.
/// </summary>
/// <remarks>
/// Every action opens the process with the least access that will do the job and re-checks its
/// creation time against the key before acting: a pid recycled between the click and the syscall is
/// a different program, and Windows recycles pids eagerly (PRD §8.2).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessActions : IProcessActions {

  public ActionResult Terminate(ProcessKey key)
    => this.WithProcess(key, Native.PROCESS_TERMINATE, handle
      => Native.TerminateProcess(handle, 1) ? ActionResult.Ok : LastError("could not end the process"));

  public ActionResult Suspend(ProcessKey key)
    => this.WithProcess(key, Native.PROCESS_SUSPEND_RESUME, handle
      => Native.NtSuspendProcess(handle) == 0
        ? ActionResult.Ok
        : ActionResult.Fail(ActionOutcome.Failed, "the process refused to suspend"));

  public ActionResult Resume(ProcessKey key)
    => this.WithProcess(key, Native.PROCESS_SUSPEND_RESUME, handle
      => Native.NtResumeProcess(handle) == 0
        ? ActionResult.Ok
        : ActionResult.Fail(ActionOutcome.Failed, "the process refused to resume"));

  public ActionResult SetPriority(ProcessKey key, int priority)
    => this.WithProcess(key, Native.PROCESS_SET_INFORMATION, handle
      => Native.SetPriorityClass(handle, ToPriorityClass(priority))
        ? ActionResult.Ok
        : LastError("could not change the priority"));

  public ActionResult SetAffinity(ProcessKey key, ulong mask) {
    if (mask == 0)
      return ActionResult.Fail(ActionOutcome.Refused, "an affinity mask with no cores in it would leave nothing to run on");

    return this.WithProcess(key, Native.PROCESS_SET_INFORMATION, handle
      => Native.SetProcessAffinityMask(handle, (nuint)mask)
        ? ActionResult.Ok
        : LastError("could not set CPU affinity"));
  }

  public ActionResult SendSignal(ProcessKey key, int signal)
    => ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, "Windows has no signals; use terminate, suspend or resume");

  /// <summary>
  /// Nice-style numbers in, priority classes out — the front-ends speak one scale, and it is the
  /// Unix one because it is ordered and Windows' is not.
  /// </summary>
  private static uint ToPriorityClass(int nice) => nice switch {
    <= -15 => Native.REALTIME_PRIORITY_CLASS,
    <= -5 => Native.HIGH_PRIORITY_CLASS,
    < 0 => Native.ABOVE_NORMAL_PRIORITY_CLASS,
    0 => Native.NORMAL_PRIORITY_CLASS,
    <= 10 => Native.BELOW_NORMAL_PRIORITY_CLASS,
    _ => Native.IDLE_PRIORITY_CLASS,
  };

  private ActionResult WithProcess(ProcessKey key, uint access, Func<nint, ActionResult> action) {
    if (key.Pid <= 0)
      return ActionResult.Fail(ActionOutcome.Refused, "there is no such pid");

    // QUERY_LIMITED_INFORMATION comes along for the identity check, which has to happen through the
    // same handle that will do the work — checking through a different one leaves a window in which
    // the pid could be recycled between the check and the act.
    var handle = Native.OpenProcess(access | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, key.Pid);
    if (handle == 0)
      return Marshal.GetLastWin32Error() switch {
        Native.ERROR_ACCESS_DENIED
          => ActionResult.Fail(ActionOutcome.NotPermitted, "this process may not be opened as this user"),
        Native.ERROR_INVALID_PARAMETER
          => ActionResult.Fail(ActionOutcome.ProcessExited, "the process has already ended"),
        var error
          => ActionResult.Fail(ActionOutcome.Failed, $"could not open the process (error {error})"),
      };

    try {
      if (!Native.GetProcessTimes(handle, out var creation, out _, out _, out _))
        return LastError("could not read the process's start time");

      if ((ulong)creation != key.StartTicks)
        return ActionResult.Fail(
          ActionOutcome.IdentityMismatch,
          $"pid {key.Pid} is no longer the process it was; it has been reused by another program"
        );

      return action(handle);
    } finally {
      Native.CloseHandle(handle);
    }
  }

  private static ActionResult LastError(string what) {
    var error = Marshal.GetLastWin32Error();
    return error == Native.ERROR_ACCESS_DENIED
      ? ActionResult.Fail(ActionOutcome.NotPermitted, $"{what}: not permitted as this user")
      : ActionResult.Fail(ActionOutcome.Failed, $"{what}: error {error}");
  }

}
