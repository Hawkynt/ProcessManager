namespace Hawkynt.ProcessManager.Model;

/// <summary>What the scheduler thinks of a process right now.</summary>
public enum ProcessState : byte {
  Unknown = 0,
  Running,
  Sleeping,
  DiskSleep,
  Stopped,
  Zombie,
  Traced,
  Idle,
  Dead,
}

/// <summary>
/// One process as one sample saw it: absolute readings only. Every rate, percentage and delta the UI
/// shows is computed by <see cref="Sampling.SnapshotDelta"/> from two of these, so a probe that
/// pre-divides anything has a bug (PRD §2).
/// </summary>
/// <remarks>
/// A mutable struct held in a pooled array rather than a class: a thousand of these are refreshed
/// every second, and a class would be a thousand allocations per sample against a budget of zero
/// (PRD §4). Take it by <c>in</c> or index into the span; do not copy it around.
/// </remarks>
public struct ProcessRecord {

  /// <summary>Identity across samples. See <see cref="ProcessKey"/> for why it is a pair.</summary>
  public ProcessKey Key;

  public int Pid => this.Key.Pid;

  /// <summary>Parent pid, or 0 when there is none (or it is no longer visible).</summary>
  public int ParentPid;

  /// <summary>The short name — <c>comm</c> on Linux, the image file name on Windows.</summary>
  public string Name;

  /// <summary>Resolved owner, or <see langword="null"/> when the name service could not answer.</summary>
  public string? UserName;

  /// <summary>Numeric owner id (uid / RID-ish), or -1 when unknown.</summary>
  public int UserId;

  public ProcessState State;

  /// <summary>Session or login id; -1 when the platform does not report one.</summary>
  public int SessionId;

  public int ThreadCount;

  /// <summary>Scheduler priority in the platform's own scale.</summary>
  public int Priority;

  /// <summary>Unix nice value; 0 elsewhere.</summary>
  public int Nice;

  /// <summary>Total CPU consumed since start, in nanoseconds.</summary>
  public Counter CpuTimeNs;

  /// <summary>The kernel-side half of <see cref="CpuTimeNs"/>.</summary>
  public Counter KernelTimeNs;

  /// <summary>The user-side half of <see cref="CpuTimeNs"/>.</summary>
  public Counter UserTimeNs;

  /// <summary>Start time as UTC ticks, for display. Identity uses <see cref="Key"/>, not this.</summary>
  public long StartTimeUtcTicks;

  /// <summary>
  /// Memory this process would give back if it exited — PSS on Linux, private bytes on Windows.
  /// This is the column to sort by; working set double-counts everything shared.
  /// </summary>
  public Counter PrivateBytes;

  /// <summary>Resident set / working set, shared pages included.</summary>
  public Counter WorkingSetBytes;

  public Counter VirtualBytes;
  public Counter SwapBytes;

  /// <summary>Bytes this process caused to be read, cumulative.</summary>
  public Counter ReadBytes;

  /// <summary>Bytes this process caused to be written, cumulative.</summary>
  public Counter WriteBytes;

  /// <summary>Open handles (Windows) or file descriptors (Unix).</summary>
  public Counter HandleCount;

  public Counter ContextSwitches;

  /// <summary>Full command line, or <see langword="null"/> when it could not be read.</summary>
  public string? CommandLine;

  /// <summary>Path of the executable image, or <see langword="null"/>.</summary>
  public string? ImagePath;

  /// <summary>cgroup / container path on Linux; <see langword="null"/> elsewhere or when not in one.</summary>
  public string? ContainerPath;

  /// <summary>Memory ceiling this process is subject to (cgroup limit), when there is one.</summary>
  public Counter MemoryLimitBytes;

  /// <summary>True when the whole process is stopped (SIGSTOP, or every thread suspended).</summary>
  public bool IsSuspended;

}
