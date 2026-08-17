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
  /// Private memory the process has committed — <c>PrivatePageCount</c> on Windows, <c>VmData</c> on
  /// Linux. What Process Explorer calls "Private Bytes", and the column to sort by when the question
  /// is which process is costing the machine memory.
  /// </summary>
  /// <remarks>
  /// Committed, not resident: it counts memory the process has asked for and may not be touching.
  /// <see cref="PrivateWorkingSetBytes"/> is the resident part of the same thing, and
  /// <see cref="WorkingSetBytes"/> the resident total including everything shared.
  /// </remarks>
  public Counter PrivateBytes;

  /// <summary>
  /// The resident part of <see cref="PrivateBytes"/> — <c>WorkingSetPrivateSize</c> on Windows,
  /// <c>RssAnon</c> on Linux, or the proportional set size when that is switched on.
  /// </summary>
  /// <remarks>
  /// The honest answer to "how much would I get back if this exited": working set double-counts
  /// every shared page, and private bytes counts memory that was never touched.
  /// </remarks>
  public Counter PrivateWorkingSetBytes;

  /// <summary>Resident set / working set, shared pages included.</summary>
  public Counter WorkingSetBytes;

  /// <summary>The largest working set this process has ever held.</summary>
  public Counter PeakWorkingSetBytes;

  public Counter VirtualBytes;

  /// <summary>The largest virtual size this process has ever held.</summary>
  public Counter PeakVirtualBytes;

  public Counter SwapBytes;

  /// <summary>Kernel memory charged to this process from the paged pool, and its peak.</summary>
  public Counter PagedPoolBytes;
  public Counter PeakPagedPoolBytes;

  /// <summary>Kernel memory charged to this process from the non-paged pool, and its peak.</summary>
  public Counter NonPagedPoolBytes;
  public Counter PeakNonPagedPoolBytes;

  /// <summary>
  /// Page faults since the process started. The interesting figure is its rate, which the delta
  /// computes — a process faulting steadily is one the machine is paging for.
  /// </summary>
  public Counter PageFaults;

  /// <summary>
  /// CPU cycles consumed, where the platform counts them.
  /// </summary>
  /// <remarks>
  /// Windows only. Unlike CPU <em>time</em> it does not flatter a process that ran while the clock
  /// was throttled, which is why Process Explorer grew a column for it.
  /// </remarks>
  public Counter Cycles;

  /// <summary>Bytes transferred that were neither reads nor writes — ioctls, mostly.</summary>
  public Counter OtherBytes;

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
