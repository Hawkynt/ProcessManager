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

  /// <summary>
  /// The logical processor this last ran on, or -1 when the platform does not say.
  /// </summary>
  /// <remarks>
  /// A snapshot of something that changes constantly, and useful for exactly that reason: a thread
  /// pinned to one core looks different from one the scheduler is moving around.
  /// </remarks>
  public int LastCpu;

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

  /// <summary>
  /// Resident memory backed by a file — executables, libraries, mapped data.
  /// </summary>
  /// <remarks>
  /// Split out from the working set because the two halves behave completely differently under
  /// pressure: file-backed pages can be dropped and read back, anonymous ones can only go to swap.
  /// A process whose resident set is almost all file-backed costs the machine far less than one of
  /// the same size that is almost all anonymous (PRD §17).
  /// </remarks>
  public Counter FileBackedBytes;

  /// <summary>Resident memory in shared segments — tmpfs, shared anonymous, System V shm.</summary>
  public Counter SharedResidentBytes;

  /// <summary>
  /// Proportional set size: private pages in full, plus each shared page divided by the number of
  /// processes mapping it.
  /// </summary>
  /// <remarks>
  /// The only per-process memory figure that adds up. Resident set counts every shared page in full
  /// for every process that maps it, so summing it over a machine reports several times the memory
  /// that exists; summing PSS gives back roughly what is actually in use. It is the honest answer to
  /// "what does this process cost me" and the reason it is worth an extra file read to get.
  /// <para>
  /// Not sampled: it comes from <c>smaps_rollup</c>, which is one more file per process per second
  /// and the kernel has to walk the page tables to answer. Read on demand for the process being
  /// looked at (PRD §5.4).
  /// </para>
  /// </remarks>
  public Counter ProportionalBytes;

  /// <summary>Swapped-out memory, counted proportionally the way <see cref="ProportionalBytes"/> is.</summary>
  public Counter ProportionalSwapBytes;

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

  /// <summary>
  /// Effective user id, which is the one that decides what the process may do. Differs from
  /// <see cref="UserId"/> for anything setuid, which is exactly the case worth noticing.
  /// </summary>
  public int EffectiveUserId;

  /// <summary>
  /// 1 when the process runs with administrative authority, 0 when it does not.
  /// </summary>
  /// <remarks>
  /// A counter rather than a bool so that "we could not tell" is expressible, which for a security
  /// field is the answer that matters most (PRD §72.3).
  /// </remarks>
  public Counter IsElevated;

  /// <summary>
  /// The Windows mandatory integrity level: 0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system.
  /// </summary>
  /// <remarks>
  /// Kept as the raw level rather than an enum so a value Microsoft adds later is still shown as a
  /// number instead of being flattened into the nearest name we happen to know.
  /// </remarks>
  public Counter IntegrityLevel;

  /// <summary>Linux seccomp mode: 0 disabled, 1 strict, 2 filtered.</summary>
  public Counter SeccompMode;

  /// <summary>Linux <c>no_new_privs</c>: 1 when the process can never gain privileges.</summary>
  public Counter NoNewPrivileges;

  /// <summary>Linux effective capability mask.</summary>
  public Counter EffectiveCapabilities;

  /// <summary>
  /// The LSM label — an SELinux context or an AppArmor profile — or <see langword="null"/>.
  /// </summary>
  /// <remarks>
  /// Costs an extra file per process, so it is off unless asked for (PRD §5.4).
  /// </remarks>
  public string? SecurityContext;

  /// <summary>
  /// Why <see cref="SecurityContext"/> is <see langword="null"/>: not asked for, no LSM on this
  /// kernel, or not readable as this user. A string field cannot carry its own reason the way a
  /// <see cref="Counter"/> does, and "no answer" needs one just as much (PRD §72.3).
  /// </summary>
  public UnknownReason SecurityContextReason;

}
