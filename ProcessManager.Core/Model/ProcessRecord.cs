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

/// <summary>Which scheduler class a task runs under.</summary>
/// <remarks>
/// The classes are the kernel's, not an invention of ours: a thread under <c>SCHED_FIFO</c> is not a
/// high-priority ordinary thread, it is a thread that ordinary threads cannot preempt at all, and
/// flattening the two onto one priority number is exactly the false equivalence §5.3 forbids.
/// <see cref="Unknown"/> is zero so that a record nobody filled says so rather than claiming the
/// ordinary class.
/// </remarks>
public enum SchedulingPolicy : byte {
  Unknown = 0,
  Other,
  Fifo,
  RoundRobin,
  Batch,
  Idle,
  Deadline,
  Extensible,
}

/// <summary>
/// Which part of a graphics adapter a process was using.
/// </summary>
/// <remarks>
/// A card is not one engine but several, and they run at once: a video call encodes on one while it
/// decodes on another and draws on a third. Reporting a single "GPU %" without saying which engine
/// produced it is the false equivalence §5.3 forbids — 80 % of the decoder and 80 % of the shaders
/// are not the same finding. <see cref="Unknown"/> is zero so that a record nobody filled says so.
/// </remarks>
public enum GpuEngine : byte {
  Unknown = 0,

  /// <summary>Shaders and rasterisation: i915's <c>render</c>, amdgpu's <c>gfx</c>.</summary>
  Graphics,

  /// <summary>Compute dispatch, where the driver counts it apart from graphics.</summary>
  Compute,

  /// <summary>The copy / blit / DMA engines that move memory without the shaders.</summary>
  Copy,

  /// <summary>Fixed-function video encode.</summary>
  Encode,

  /// <summary>Fixed-function video decode.</summary>
  Decode,
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

  /// <summary>
  /// The parent's short name, or <see langword="null"/> when no process in this sample holds
  /// <see cref="ParentPid"/>.
  /// </summary>
  /// <remarks>
  /// Filled by the sampler once the whole table has been read, not by the probe: it is the same
  /// string instance the parent's own row carries, so it costs a reference rather than a name read
  /// per process (PRD §4). Null is the ordinary answer for anything whose parent has exited and been
  /// reparented, and for pid 1.
  /// </remarks>
  public string? ParentName;

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
  /// The scheduler class, or <see cref="SchedulingPolicy.Unknown"/> where the platform has no such
  /// notion — which is the default, so a probe that never sets it does not claim the ordinary class.
  /// </summary>
  public SchedulingPolicy SchedulingPolicy;

  /// <summary>
  /// The logical processor this last ran on, or -1 when the platform does not say.
  /// </summary>
  /// <remarks>
  /// A snapshot of something that changes constantly, and useful for exactly that reason: a thread
  /// pinned to one core looks different from one the scheduler is moving around.
  /// </remarks>
  public int LastCpu;

  /// <summary>
  /// The processors the process is allowed to run on, in the kernel's own list notation
  /// (<c>0-7,15</c>), or <see langword="null"/> when nobody asked or nobody could tell.
  /// </summary>
  /// <remarks>
  /// The list rather than a mask, for the same reason <see cref="ThreadRecord.Affinity"/> is: on a
  /// 128-way machine the list is the readable form and thirty-two hex digits are not. Text and
  /// therefore an allocation per process per sample, so it is kept only when asked for (PRD §5.4) —
  /// the line itself is free, being in the <c>status</c> the sampler already has open.
  /// </remarks>
  public string? CpuAffinity;

  /// <summary>
  /// Why <see cref="CpuAffinity"/> is <see langword="null"/>: not asked for, not readable, or a
  /// platform whose affinity we do not read yet. A string cannot carry its own reason the way a
  /// <see cref="Counter"/> does, and "no answer" needs one just as much (PRD §72.3).
  /// </summary>
  public UnknownReason CpuAffinityReason;

  /// <summary>
  /// How many times the process's cgroup has been stopped for exhausting its CPU quota.
  /// </summary>
  /// <remarks>
  /// The cgroup's counter, not the process's: everything in one group shares it, and the column says
  /// so. A group with a quota it never reaches reports a real nought here, which is why an absent
  /// controller has to report unknown instead — the two would otherwise be the same cell with
  /// opposite meanings (PRD §72.3).
  /// </remarks>
  public Counter ThrottledPeriods;

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

  /// <summary>
  /// Unique set size: the memory only this process maps, and so the only memory that would come
  /// back if it exited.
  /// </summary>
  /// <remarks>
  /// The other half of the proportional set's story and from the same file, so it costs nothing
  /// extra once that has been read. PSS says what a process costs the machine; USS says what killing
  /// it would recover, and the two differ by exactly the shared pages somebody else is also using.
  /// </remarks>
  public Counter UniqueBytes;

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

  /// <summary>
  /// The same descriptors split by what they point at: sockets, names in the file system, pipes
  /// (PRD §20).
  /// </summary>
  /// <remarks>
  /// A count each rather than one number, because they answer different questions: a server leaking
  /// connections and an indexer holding a thousand files both show a large handle count and nothing
  /// else in common. Each carries its own reason, because the scan can fail for a process whose
  /// descriptor directory this user may not open — which is most of a machine's process table.
  /// <para>
  /// Filled only when asked for: the split needs the target of every descriptor resolved, which is
  /// a link to read on top of the directory listing that was already the most expensive thing in
  /// the sampler (PRD §5.4).
  /// </para>
  /// </remarks>
  public Counter SocketCount;
  public Counter FileCount;
  public Counter PipeCount;

  /// <summary>
  /// How many sockets of each kind this process holds a descriptor on (PRD §18, §40).
  /// </summary>
  /// <remarks>
  /// <para>
  /// A count of endpoints and never of traffic. "This process holds forty TCP connections" is a fact
  /// the socket tables can be joined to a descriptor list to establish; "this process has sent forty
  /// megabytes" is not, and the four counters here are deliberately the ones that can be answered
  /// (PRD §72.3).
  /// </para>
  /// <para>
  /// Expensive, and therefore <see cref="UnknownReason.NotSampledYet"/> unless somebody asked for
  /// them: filling them means listing every process's descriptors, which is the same 85 µs-per-process
  /// read that keeps <see cref="HandleCount"/> off the sampling path (PRD §5.4).
  /// </para>
  /// </remarks>
  public Counter TcpSocketCount;

  /// <inheritdoc cref="TcpSocketCount"/>
  public Counter UdpSocketCount;

  /// <summary>
  /// How many of this process's sockets are accepting connections rather than making them.
  /// </summary>
  /// <remarks>
  /// TCP only. A UDP socket bound to a port is not listening in any sense the kernel records — it
  /// has no such state — so counting one would be inventing a distinction the protocol does not
  /// make (PRD §5.3).
  /// </remarks>
  public Counter ListeningSocketCount;

  /// <summary>
  /// How many distinct remote endpoints this process is connected to.
  /// </summary>
  /// <remarks>
  /// Distinct addresses and ports rather than connections, because two connections to the same peer
  /// are one correspondent — which is what the column is read for. A socket with no peer, which is
  /// every listener and every unconnected datagram socket, counts towards none of it.
  /// </remarks>
  public Counter RemoteEndpointCount;

  public Counter ContextSwitches;

  /// <summary>Full command line, or <see langword="null"/> when it could not be read.</summary>
  public string? CommandLine;

  /// <summary>Path of the executable image, or <see langword="null"/>.</summary>
  public string? ImagePath;

  /// <summary>cgroup / container path on Linux; <see langword="null"/> elsewhere or when not in one.</summary>
  public string? ContainerPath;

  /// <summary>
  /// The controlling terminal's device number, or 0 for a process with none.
  /// </summary>
  /// <remarks>
  /// Packed the way <c>stat</c> packs it: minor in the low eight bits and bits 20–31, major in
  /// between. Zero is not a device — it is the answer for every daemon and every service, which is
  /// most of a machine's process table.
  /// </remarks>
  public int TerminalDevice;

  #region graphics (PRD §19)

  /// <summary>
  /// Time this process kept each engine of its adapter busy, cumulative nanoseconds since it opened
  /// the device.
  /// </summary>
  /// <remarks>
  /// DRM's own shape, straight out of <c>/proc/[pid]/fdinfo</c>'s <c>drm-engine-*</c> lines: a
  /// monotonic counter per engine, exactly like <see cref="CpuTimeNs"/>, so
  /// <see cref="Sampling.SnapshotDelta"/> turns it into a share of the interval and no probe divides
  /// anything (PRD §2). Every engine carries its own reason, because which of them a driver counts
  /// is the driver's business — i915 publishes no compute engine at all, and a nought there would
  /// claim the card has one that nothing uses.
  /// </remarks>
  public Counter GpuGraphicsNs;
  public Counter GpuComputeNs;
  public Counter GpuCopyNs;
  public Counter GpuEncodeNs;
  public Counter GpuDecodeNs;

  /// <summary>
  /// The same use, as a percentage the driver sampled for itself rather than a counter to subtract.
  /// </summary>
  /// <remarks>
  /// NVIDIA's is the stack that forces this. NVML has no per-process engine counter of any kind: its
  /// per-process reading is <c>nvmlDeviceGetProcessUtilization</c>, which hands back the driver's own
  /// sampled percentages over its own recent window and nothing that can be differenced. Keeping the
  /// two shapes apart is the honest option — synthesising a counter by integrating a sampled
  /// percentage would produce a "GPU time" figure that drifts and that nobody could reconcile against
  /// <c>nvidia-smi</c>. A reader gets whichever of the two its hardware actually has.
  /// <para>
  /// <see cref="GpuBusyPercent"/> is NVML's <c>sm</c> figure, which covers graphics and compute
  /// together: the driver does not split them per process, so neither do we.
  /// </para>
  /// </remarks>
  public Counter GpuBusyPercent;
  public Counter GpuEncodePercent;
  public Counter GpuDecodePercent;

  /// <summary>
  /// Which engine <see cref="GpuBusyPercent"/> describes, where the driver says.
  /// </summary>
  /// <remarks>
  /// NVML gives one figure for the shaders and names no engine, but it does say which of its two
  /// lists a process is in — a CUDA client or a rendering one — and that is the split worth showing.
  /// <see cref="GpuEngine.Unknown"/> where there is no such hint, which is every driver that reports
  /// engine counters instead and needs none.
  /// </remarks>
  public GpuEngine GpuBusyEngine;

  /// <summary>Adapter memory this process holds — VRAM on a discrete card.</summary>
  public Counter GpuDedicatedBytes;

  /// <summary>
  /// System memory the adapter is using on this process's behalf: GTT on amdgpu, and on an
  /// integrated part the whole of it, there being no dedicated memory to hold.
  /// </summary>
  public Counter GpuSharedBytes;

  /// <summary>
  /// Which adapter these readings belong to — the kernel's <c>cardN</c> — or <see langword="null"/>.
  /// </summary>
  /// <remarks>
  /// A machine with two cards is the ordinary laptop, and a GPU figure that does not say which of
  /// them it came from is unreadable on exactly the machines where it matters most.
  /// </remarks>
  public string? GpuAdapter;

  /// <summary>
  /// Why <see cref="GpuAdapter"/> is <see langword="null"/>: not asked for, no adapter this process
  /// has open, or nothing on this machine that can answer. A string cannot carry its own reason the
  /// way a <see cref="Counter"/> does, and "no answer" needs one just as much (PRD §72.3).
  /// </summary>
  public UnknownReason GpuAdapterReason;

  #endregion

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
  /// The account <see cref="EffectiveUserId"/> resolves to, or <see langword="null"/> when the name
  /// service could not answer.
  /// </summary>
  /// <remarks>
  /// Its own field rather than a second look-up at render time. <see cref="UserName"/> is the real
  /// uid's name — who started the process — and this is the effective one's — whose authority it is
  /// running with. For all but a handful of processes they are the same string, and for the handful
  /// where they are not, that difference is the whole point of a security column (PRD §21).
  /// </remarks>
  public string? EffectiveUserName;

  /// <summary>
  /// The saved set-user id: the identity a process that has dropped privileges may take back.
  /// </summary>
  /// <remarks>
  /// -1 when unknown, the same convention <see cref="UserId"/> uses. A process whose real and
  /// effective ids are an ordinary user while the saved one is root has not given anything up — it
  /// can call <c>seteuid(0)</c> whenever it likes — and no other field says so.
  /// </remarks>
  public int SavedUserId;

  /// <summary>The filesystem user id, which decides what the process may open. -1 when unknown.</summary>
  public int FilesystemUserId;

  /// <summary>Real, effective, saved and filesystem group ids; -1 when unknown.</summary>
  public int GroupId;
  public int EffectiveGroupId;
  public int SavedGroupId;
  public int FilesystemGroupId;

  /// <summary>
  /// The supplementary groups, as the numbers <c>status</c> writes them, separated by spaces.
  /// </summary>
  /// <remarks>
  /// Text rather than a list because a list is an allocation per process per sample against a budget
  /// of zero (PRD §4), and null unless it was asked for, for the same reason
  /// <see cref="SecurityContext"/> is (PRD §5.4). The empty string is a real answer — a process in no
  /// supplementary group at all, which is what every kernel thread is.
  /// </remarks>
  public string? SupplementaryGroups;

  /// <summary>
  /// Why <see cref="SupplementaryGroups"/> is <see langword="null"/>: not asked for, not readable,
  /// or a kernel that does not write the line.
  /// </summary>
  public UnknownReason SupplementaryGroupsReason;

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

  /// <summary>
  /// How many seccomp filter programs are attached, where the kernel reports it.
  /// </summary>
  /// <remarks>
  /// <c>Seccomp_filters</c> arrived in 5.9; an older kernel leaves this unknown rather than zero.
  /// Distinct from <see cref="SeccompMode"/> because a mode of 2 with several filters is a process
  /// something has sandboxed more than once — a browser renderer inside a container, typically —
  /// and the mode alone cannot say that.
  /// </remarks>
  public Counter SeccompFilters;

  /// <summary>Linux <c>no_new_privs</c>: 1 when the process can never gain privileges.</summary>
  public Counter NoNewPrivileges;

  /// <summary>
  /// Linux effective capability mask: what the process may do <em>right now</em>.
  /// </summary>
  /// <remarks>
  /// The five masks are five different questions and are kept apart for that reason. Effective is
  /// what the kernel checks on a privileged operation; <see cref="PermittedCapabilities"/> is what
  /// the process may raise into it without asking anybody; <see cref="BoundingCapabilities"/> is the
  /// ceiling nothing it execs can exceed; <see cref="InheritableCapabilities"/> and
  /// <see cref="AmbientCapabilities"/> are what survives an <c>execve</c>, the second without the
  /// file needing capabilities of its own. Showing only the effective set hides a process that has
  /// dropped a capability for now and can take it back at any instant.
  /// </remarks>
  public Counter EffectiveCapabilities;

  /// <summary>Linux permitted capability mask — what may be raised into the effective set.</summary>
  public Counter PermittedCapabilities;

  /// <summary>Linux inheritable capability mask — what a file with the bits set may keep.</summary>
  public Counter InheritableCapabilities;

  /// <summary>Linux bounding capability set — the ceiling on anything this process execs.</summary>
  public Counter BoundingCapabilities;

  /// <summary>Linux ambient capability set — what survives an exec of an unprivileged file.</summary>
  public Counter AmbientCapabilities;

  /// <summary>
  /// The digests of the image the process is running, or <see langword="null"/> (PRD §21, §70).
  /// </summary>
  /// <remarks>
  /// What the bytes are, and nothing else: a hash is not a verdict, and neither of these says
  /// anything about whether the image is signed, trusted or known. Computed only when asked for and
  /// once per image rather than once per process — the cost of hashing is the size of the file, and
  /// three hundred processes of one runtime share one image between them (PRD §5.4).
  /// </remarks>
  public string? ImageSha256;
  public string? ImageSha1;

  /// <summary>
  /// Why the two digests are <see langword="null"/>: not asked for, no image to hash — a kernel
  /// thread has none — the file replaced since the process started, or a file this user may not
  /// read.
  /// </summary>
  public UnknownReason ImageHashReason;

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
