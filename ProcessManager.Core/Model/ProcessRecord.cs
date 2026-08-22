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

  /// <summary>
  /// Sets every reading that only one platform can take to "this platform cannot" (PRD §72.3).
  /// </summary>
  /// <remarks>
  /// Called for every record before a probe fills it, so that a probe which has never heard of a
  /// field cannot be the reason that field claims a value. The alternative — each probe naming every
  /// other platform's readings and refusing them one by one — is the arrangement the older fields
  /// use, and it works only for as long as nobody adds a field without editing all three probes.
  /// <para>
  /// Only the Windows-only readings of §20 and §21 are here. The older per-platform counters are
  /// still refused by the probes themselves; moving them would be a change to code that is working,
  /// for no reading that is currently wrong.
  /// </para>
  /// </remarks>
  public static void ClearPlatformReadings(ref ProcessRecord record) {
    // A record handed out for reuse is a living process until something says otherwise. Left over
    // from the last row that occupied this slot, a stale exit time would turn a running process into
    // a tombstone and colour it as dead (PRD §14, §87).
    record.ExitedUtcTicks = 0;
    // And nobody has looked, which for an exit code is nearly always the final answer: neither kernel
    // tells a bystander what a process it did not start exited with.
    record.ExitCode = Counter.NotPermitted;
    record.ProtectionLevel = Counter.NotSupported;
    record.IsAppContainer = Counter.NotSupported;
    record.Emulation = Counter.NotSupported;
    record.Subsystem = Counter.NotSupported;
    record.DepPolicy = Counter.NotSupported;
    record.AslrPolicy = Counter.NotSupported;
    record.ControlFlowGuardPolicy = Counter.NotSupported;
    record.ShadowStackPolicy = Counter.NotSupported;
    record.DynamicCodePolicy = Counter.NotSupported;
    record.BinarySignaturePolicy = Counter.NotSupported;
    record.EventObjectCount = Counter.NotSupported;
    record.SemaphoreObjectCount = Counter.NotSupported;
    record.MutexObjectCount = Counter.NotSupported;
    record.SectionObjectCount = Counter.NotSupported;
    record.RegistryKeyCount = Counter.NotSupported;
    record.UserObjectCount = Counter.NotSupported;
    record.GdiObjectCount = Counter.NotSupported;
    // PRD §15, §16. The two Windows readings that need a handle per process per sample, and so are
    // filled after this runs rather than before it. Nought would be a claim in both of them: the
    // lowest page priority there is, and a processor set of none.
    record.PagePriority = Counter.NotSupported;
    record.CpuSets = null;
    record.CpuSetsReason = UnknownReason.NotSupportedOnPlatform;
    record.ImageDescription = null;
    record.ImageCompany = null;
    record.ImageProduct = null;
    record.ImageProductVersion = null;
    record.ImageFileVersion = null;
    record.ImageVersionReason = UnknownReason.NotSupportedOnPlatform;
    // PRD §21. An ELF carries no embedded signature and never did, so the five signature readings
    // are not a Linux gap but a Windows concept — "n/a" and not an empty cell that reads like one
    // (PRD §5.3). What a Linux machine can say about a file's provenance is its package's, which is
    // PackageStatus and TrustChain and is a different question already asked elsewhere.
    record.ImageSignature = SignatureStatus.NotChecked;
    record.ImageSignatureDetail = null;
    record.ImageSignatureReason = UnknownReason.NotSupportedOnPlatform;
    record.ImageSigner = null;
    record.CertificateSubject = null;
    record.CertificateIssuer = null;
    record.SignatureTimestampUtcTicks = Counter.NotSupported;
    // PRD §22. The one energy field any platform will answer per process, and Linux is not it: there
    // is no per-process energy quality of service here at all, only a scheduler class, which §15
    // already reports as itself rather than dressed as an energy reading.
    record.PowerThrottling = Counter.NotSupported;
  }

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
  /// The Windows priority class, as its <c>*_PRIORITY_CLASS</c> band rather than as a number
  /// (PRD §15).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Windows orders processes by a band and not by a scalar the way <c>nice</c> does, and the band is
  /// what every Windows tool shows and what <c>SetPriorityClass</c> takes. Linux has no such thing —
  /// <c>nice</c> orders tasks inside <c>SCHED_OTHER</c> and the class is
  /// <see cref="SchedulingPolicy"/> — so folding either into the other would be the false equivalence
  /// §5.3 forbids, and on Linux this is not applicable rather than unknown.
  /// </para>
  /// <para>
  /// Derived from <see cref="Priority"/>, which the bulk query already carries, rather than from
  /// <c>GetPriorityClass</c> on a handle. Not to save the call: the class is settable — this program
  /// sets it (§25.2) — so an answer cached for a process's lifetime would be wrong the moment
  /// somebody changed it, and an answer read per process per sample is the <c>OpenProcess</c> in the
  /// sampling loop §5.2 forbids. The base priority is refreshed by every sample and the kernel
  /// derives it from the class by a fixed table, so inverting that table is both free and current.
  /// A base priority outside the table is left unknown rather than rounded to the nearest band.
  /// </para>
  /// </remarks>
  public Counter PriorityClass;

  /// <summary>
  /// The memory-manager page priority, 0–5, where Windows reports one (PRD §16).
  /// </summary>
  /// <remarks>
  /// Which pages the memory manager takes back first when the machine is short. A backup or an
  /// indexer sets itself low so that its pages are trimmed before anybody else's, and no other column
  /// on the row says that. Nought is a real value — the lowest priority there is — which is why this
  /// is a <see cref="Counter"/> and not a number with a magic default (PRD §72.3).
  /// <para>
  /// Linux has no per-process page priority: reclaim there is driven by the LRU lists and by the
  /// cgroup's own knobs, neither of which is a property of a process.
  /// </para>
  /// </remarks>
  public Counter PagePriority;

  /// <summary>
  /// The CPU sets the process has been assigned to, as the numbers Windows uses for them, or
  /// <see langword="null"/> (PRD §15).
  /// </summary>
  /// <remarks>
  /// Not the affinity mask, and deliberately its own field: an affinity mask is a hard restriction
  /// the process cannot run outside, while a CPU set is a preference the scheduler honours when it
  /// can — which is the whole reason Windows grew a second mechanism (PRD §5.3). The empty string is
  /// a real answer and the ordinary one: a process with no set assigned gets the system's default
  /// set, which is every processor.
  /// </remarks>
  public string? CpuSets;

  /// <summary>
  /// Why <see cref="CpuSets"/> is <see langword="null"/>: not asked for, not readable, or a platform
  /// with no such notion. A string cannot carry its own reason the way a <see cref="Counter"/> does,
  /// and "no answer" needs one just as much (PRD §72.3).
  /// </summary>
  public UnknownReason CpuSetsReason;

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
  /// When this process ended, as UTC ticks, or nought while it is running (PRD §14, §87).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Non-zero marks a <b>tombstone</b>: the last reading taken of a process that has since gone,
  /// kept in the table for as long as somebody asked for. Nothing in a probe ever sets this — a probe
  /// reports what is there — and the sampler appends the row after the delta has already reported the
  /// process as exited.
  /// </para>
  /// <para>
  /// Nought rather than nullable so a record stays a value type the snapshot's array can hold without
  /// allocating, and because nought is not a time anything could have ended at: the wall clock does
  /// not run backwards to year one.
  /// </para>
  /// <para>
  /// Every counter on a tombstone is the last one that was read, not a fresh one, and every
  /// <em>rate</em> over it is unsampled — a row that has stopped moving must not report the rate it
  /// had when it stopped, which would show a dead process using a processor (§3.4, §72.3).
  /// </para>
  /// </remarks>
  public long ExitedUtcTicks;

  /// <summary>Whether this row is the remains of a process rather than a process.</summary>
  public readonly bool HasExited => this.ExitedUtcTicks > 0;

  /// <summary>
  /// What the process exited with, where that is knowable (PRD §14).
  /// </summary>
  /// <remarks>
  /// <b>Usually it is not, and the reason is the same on both platforms.</b> Neither kernel will tell
  /// a bystander what a process it did not start exited with: the status is delivered to the parent
  /// through <c>wait</c> on Unix, and on Windows it needs a handle held open across the exit. So this
  /// carries <see cref="UnknownReason.NotPermitted"/> for everything this program did not launch —
  /// which is nearly everything — and a real value only for a child it started itself and reaped.
  /// </remarks>
  public Counter ExitCode;

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

  /// <summary>
  /// The largest <see cref="PrivateBytes"/> this process has ever held (PRD §16).
  /// </summary>
  /// <remarks>
  /// The peak of the <em>same</em> charge the private column reports, which is what makes the pair
  /// worth having: a process sitting at fifty megabytes with a peak of four gigabytes has been
  /// somewhere the current row cannot show. Windows keeps it as <c>PeakPagefileUsage</c>, beside the
  /// commit charge in the structure the sampler already reads.
  /// <para>
  /// Linux keeps no such high-water mark. <c>status</c> carries <c>VmPeak</c>, which is the peak of
  /// the address space, and <c>VmHWM</c>, which is the peak of the resident set — neither is the peak
  /// of <c>VmData</c>, and reporting either under this name would be a different number wearing this
  /// one's label (PRD §5.3).
  /// </para>
  /// </remarks>
  public Counter PeakPrivateBytes;

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
  /// The stack segment the kernel accounts to the process — <c>VmStk</c> on Linux (PRD §16).
  /// </summary>
  /// <remarks>
  /// The <em>main</em> thread's stack and only that. Every other thread's stack is an ordinary
  /// anonymous mapping made by whoever created the thread, and the kernel keeps no separate figure
  /// for those: a thread pool with two hundred eight-megabyte stacks reports the same few kilobytes
  /// here as a single-threaded program. Which is why the column says "main stack" rather than
  /// "stacks" — the larger number is real and this is not it (PRD §5.3).
  /// </remarks>
  public Counter StackBytes;

  /// <summary>
  /// How much of the address space is backed by a file rather than by anonymous memory (PRD §16).
  /// </summary>
  /// <remarks>
  /// The <em>mapped</em> size and not the resident one: <see cref="FileBackedBytes"/> is how much of
  /// this is in memory at the moment, and the two answer different questions. A process that has
  /// mapped a four-gigabyte database and touched a megabyte of it reports four gigabytes here and a
  /// megabyte there, and neither figure is the other's approximation.
  /// <para>
  /// Summed from <c>/proc/[pid]/maps</c> over every mapping that names a file. The pseudo-mappings —
  /// <c>[heap]</c>, <c>[stack]</c>, <c>[vdso]</c> and the rest — name no file and are excluded,
  /// which is why the test for them is the bracket rather than a list of their names: the kernel
  /// adds new ones.
  /// </para>
  /// <para>
  /// A file per process per sample, and one the kernel formats a page at a time, so it is filled only
  /// when a column or a filter names it (PRD §5.4).
  /// </para>
  /// </remarks>
  public Counter MappedFileBytes;

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

  /// <summary>
  /// How many read and write <em>operations</em> the process has made — <c>syscr</c> and
  /// <c>syscw</c> on Linux, <c>ReadOperationCount</c> and <c>WriteOperationCount</c> on Windows
  /// (PRD §17).
  /// </summary>
  /// <remarks>
  /// A different question from the byte counters beside them, and the pair together is the one worth
  /// having: a process moving a gigabyte in a thousand operations and one moving it in a million are
  /// the same row under <see cref="ReadBytes"/> and very different machines to be sitting in front
  /// of. Free on both platforms — the lines are in a file already being read on one and fields of a
  /// structure already queried on the other.
  /// <para>
  /// Linux counts <em>system calls</em> here and not requests to a device: a read served from the
  /// page cache counts, which is exactly what makes the ratio against <see cref="ReadBytes"/> — the
  /// bytes that did reach a device — worth reading.
  /// </para>
  /// </remarks>
  public Counter ReadOperations;

  /// <inheritdoc cref="ReadOperations"/>
  public Counter WriteOperations;

  /// <summary>
  /// Operations that were neither reads nor writes — ioctls, mostly. Windows only.
  /// </summary>
  /// <remarks>
  /// The count beside <see cref="OtherBytes"/>, and it has no Linux counterpart: <c>/proc/[pid]/io</c>
  /// counts <c>syscr</c> and <c>syscw</c> and has no third figure of any kind, so a nought here on
  /// Linux would be a claim the kernel never made (PRD §5.3).
  /// </remarks>
  public Counter OtherOperations;

  /// <summary>
  /// Nanoseconds the process has spent waiting for block I/O, where the kernel accounts for it
  /// (PRD §17).
  /// </summary>
  /// <remarks>
  /// Linux's <c>delayacct_blkio_ticks</c>, field 42 of <c>stat</c>. The reading that separates a
  /// process that is slow because it is computing from one that is slow because it is waiting for a
  /// disk, which no other column on the row can say.
  /// <para>
  /// Delay accounting is compiled in on ordinary distribution kernels and <b>switched off</b>:
  /// <c>kernel.task_delayacct</c> has defaulted to 0 since 5.14, and with it off the kernel writes a
  /// literal 0 into that field for every process on the machine. So a probe must ask whether the
  /// accounting is on before believing the number — otherwise the column is a table-wide row of
  /// noughts that reads as "nothing here ever waits for a disk", which is the same lie as any other
  /// <c>default(Counter)</c> (PRD §72.3).
  /// </para>
  /// </remarks>
  public Counter BlockIoWaitNs;

  /// <summary>
  /// The process's I/O scheduling priority, packed the way <c>ioprio_get</c> returns it (PRD §17).
  /// </summary>
  /// <remarks>
  /// Kept packed rather than as an <see cref="IoPriority"/> so that "the syscall refused" stays
  /// distinct from "the process has none set" — the second is a real answer and the commonest one,
  /// and <see cref="IoPriorityClass.None"/> is what a struct nobody filled would already say
  /// (PRD §72.3).
  /// <para>
  /// A syscall per process per sample, so it is filled only when a column or a filter names it
  /// (PRD §5.4).
  /// </para>
  /// </remarks>
  public Counter IoPriorityValue;

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
  /// The same handle table split by the kernel object types Windows has and Unix does not
  /// (PRD §20).
  /// </summary>
  /// <remarks>
  /// A count each, for the same reason the three above are counts each: a service holding ten
  /// thousand registry keys and one holding ten thousand sections both show a large handle count and
  /// have nothing else in common. None of the five has a Linux counterpart worth counting — an
  /// <c>eventfd</c> is a descriptor and is already in <see cref="FileCount"/>'s neighbourhood, a
  /// futex has no kernel object at all, and there is no registry — so on Linux these are not
  /// unfilled, they are not applicable (PRD §5.3).
  /// <para>
  /// Filled only when asked for. The whole machine's handle table arrives in one query rather than
  /// one per process, which makes this cheaper on Windows than the equivalent is on Linux, but it is
  /// still megabytes of table per sample and §20 says the per-type tallies stay out of the sampling
  /// loop until somebody names a column (PRD §5.4).
  /// </para>
  /// </remarks>
  public Counter EventObjectCount;

  /// <inheritdoc cref="EventObjectCount"/>
  public Counter SemaphoreObjectCount;

  /// <inheritdoc cref="EventObjectCount"/>
  public Counter MutexObjectCount;

  /// <inheritdoc cref="EventObjectCount"/>
  public Counter SectionObjectCount;

  /// <inheritdoc cref="EventObjectCount"/>
  public Counter RegistryKeyCount;

  /// <summary>
  /// The window-manager and graphics objects charged to the process (PRD §20, §39).
  /// </summary>
  /// <remarks>
  /// Not handles and not in the handle table: these are the desktop's own quotas — ten thousand of
  /// each per process by default — and a program that exhausts one stops being able to draw while
  /// every other counter on its row still looks healthy, which is exactly the failure no other
  /// column would show.
  /// <para>
  /// Unlike the object tallies above, these change from moment to moment and cannot be cached for a
  /// process's lifetime, so they cost a call each per process per sample and are asked for rather
  /// than sampled (PRD §5.4).
  /// </para>
  /// </remarks>
  public Counter UserObjectCount;

  /// <inheritdoc cref="UserObjectCount"/>
  public Counter GdiObjectCount;

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

  /// <summary>
  /// The Windows protected-process level, as <c>ProcessProtectionLevelInfo</c> reports it (PRD §21).
  /// </summary>
  /// <remarks>
  /// Kept as the raw <c>PROTECTION_LEVEL_*</c> value for the same reason
  /// <see cref="IntegrityLevel"/> is raw. Two things about that value are worth stating here because
  /// both are traps: <c>PROTECTION_LEVEL_NONE</c> is <c>0xFFFFFFFE</c> rather than the <c>-1</c> a
  /// sentinel usually is, and <em>nought is a real level</em> — <c>PROTECTION_LEVEL_WINTCB_LIGHT</c>
  /// — so a record nobody filled must not read as the most protected process on the machine. That is
  /// why this is a <see cref="Counter"/> and not a number with a magic default (PRD §72.3).
  /// </remarks>
  public Counter ProtectionLevel;

  /// <summary>
  /// 1 when the process runs inside an AppContainer, 0 when it does not (PRD §21).
  /// </summary>
  /// <remarks>
  /// <c>TokenIsAppContainer</c>, out of the same token the owner and the integrity level come from,
  /// so it costs nothing beyond them. Not a Linux idea in any form: what confines a process there is
  /// the seccomp mode, the LSM label and the namespace set, each of which is already its own field
  /// and each of which says more than one sandbox flag could (PRD §5.3).
  /// </remarks>
  public Counter IsAppContainer;

  /// <summary>
  /// The six Windows per-process mitigation policies, each as the flags word its own structure
  /// carries (PRD §21).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The raw word rather than a verdict, so that a bit Microsoft adds later is still visible instead
  /// of being rounded into whichever of this build's words is nearest. Each is the <c>Flags</c>
  /// member of the matching <c>PROCESS_MITIGATION_*</c> structure, which is a union over a bitfield —
  /// so reading the word reads every named bit at once and the interpretation happens where the
  /// column is rendered, on every platform, and can therefore be tested on any of them.
  /// </para>
  /// <para>
  /// <see cref="DepPolicy"/> is the one that is not merely a word: its structure carries a
  /// <c>BOOLEAN Permanent</c> outside the union, and that flag is kept in bit 32 — above the
  /// thirty-two the word occupies, so nothing of the structure is lost and nothing is invented.
  /// </para>
  /// <para>
  /// These are <em>requests</em>, which is what makes them Windows' own idea rather than a thing
  /// Linux happens to spell differently: Linux publishes what is switched on for a task and has no
  /// record of what was asked for, which is <see cref="ThreadFeatures"/> and the two speculation
  /// fields, and those are deliberately not these (PRD §5.3).
  /// </para>
  /// </remarks>
  public Counter DepPolicy;

  /// <inheritdoc cref="DepPolicy"/>
  public Counter AslrPolicy;

  /// <inheritdoc cref="DepPolicy"/>
  public Counter ControlFlowGuardPolicy;

  /// <inheritdoc cref="DepPolicy"/>
  public Counter ShadowStackPolicy;

  /// <inheritdoc cref="DepPolicy"/>
  public Counter DynamicCodePolicy;

  /// <inheritdoc cref="DepPolicy"/>
  public Counter BinarySignaturePolicy;

  /// <summary>
  /// Windows' per-process power throttling, as the two masks its structure carries (PRD §22).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The one field in §22 that is a reading rather than a model. It does not say how much energy a
  /// process is using — nothing on any platform says that per process, and §22 refuses the eight
  /// columns that would pretend to — it says which of Windows' energy behaviours have been asked for
  /// on this process's behalf, which is a documented state with a documented call behind it.
  /// </para>
  /// <para>
  /// Both masks, packed: the control mask in the low thirty-two bits and the state mask above them.
  /// Two masks and not one because they answer different questions, and the pair has a third answer
  /// the single word cannot express — a bit that is not in the control mask at all is one the system
  /// is managing, which is neither "throttled" nor "not throttled" and is what most of a machine's
  /// table is (PRD §72.3). The bits are decoded where the column is rendered, in portable code with
  /// a test per state.
  /// </para>
  /// </remarks>
  public Counter PowerThrottling;

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
  /// The <see cref="SpeculationState"/> of this task's store-bypass mitigation, as its ordinal.
  /// </summary>
  /// <remarks>
  /// A <see cref="Counter"/> rather than the enum, so that "the kernel does not write this line"
  /// stays distinct from "the kernel wrote <c>unknown</c>". Both are answers and they are different
  /// answers: the first is a kernel too old or an architecture with no such control, the second is
  /// this kernel declining to say for this task (PRD §72.3).
  /// </remarks>
  public Counter SpeculationStoreBypass;

  /// <summary>
  /// The <see cref="IndirectBranchState"/> of this task's indirect-branch mitigation, as its ordinal.
  /// </summary>
  /// <remarks>
  /// Its own field because it is its own control: a process may have asked for one mitigation and
  /// not the other, and folding the pair into a single "mitigated" word would answer less than
  /// either of them does (PRD §5.3).
  /// </remarks>
  public Counter SpeculationIndirectBranch;

  /// <summary>
  /// The <see cref="ThreadSecurityFeatures"/> the kernel has switched on for this task, as the mask.
  /// </summary>
  /// <remarks>
  /// <see cref="ThreadSecurityFeatures.None"/> is a real answer and the ordinary one — most
  /// processes on most machines run without a shadow stack. The line being absent is not that
  /// answer, and the counter carries the difference.
  /// </remarks>
  public Counter ThreadFeatures;

  /// <summary>
  /// The file-creation mask, as the number <c>status</c> writes in octal.
  /// </summary>
  /// <remarks>
  /// Kept as the value of the mask rather than as its four digits, so that it sorts and filters as
  /// the set of bits it is. A daemon running with a mask of 0 creates every file it touches
  /// world-writable, which is a finding no other column on this row would show.
  /// </remarks>
  public Counter Umask;

  /// <summary>
  /// Which process is tracing this one, or 0 for none.
  /// </summary>
  /// <remarks>
  /// Zero is a real answer here and the usual one, so the counter's business is the other case: a
  /// kernel that does not write the line, or a <c>hidepid</c> mount. The pid rather than a yes/no,
  /// because "something is attached to this process" is only half the question and the other half is
  /// what.
  /// </remarks>
  public Counter TracerPid;

  /// <summary>
  /// How many slots this process's descriptor table has, from <c>FDSize</c> (PRD §20).
  /// </summary>
  /// <remarks>
  /// A capacity, not a count and not a peak. The table is grown when a descriptor will not fit and
  /// is never shrunk while the process lives, so it is an upper bound on how many descriptors this
  /// process has ever held at once — which is a different statement from the high-water mark Windows
  /// reports, and is labelled as the different statement it is. On the machine this was written on a
  /// shell held four descriptors with a table of 256 and a ceiling of 524288: three numbers, none of
  /// them the other two.
  /// </remarks>
  public Counter DescriptorTableSize;

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
  /// Which package the running image belongs to, and which application it is (PRD §14).
  /// </summary>
  /// <remarks>
  /// Opt-in: answering it means reading every installed package's file list once, which is thirty
  /// megabytes of text on an ordinary desktop (PRD §5.4).
  /// </remarks>
  public PackageIdentity Package;

  /// <summary>
  /// Whether the image is still the file its package shipped (PRD §70).
  /// </summary>
  /// <remarks>
  /// Local signature verification, and only that. It is not a hash — that is
  /// <see cref="ImageSha256"/>, and a matching hash is not a verdict — it is not a trust chain, and
  /// it is emphatically not a reputation: nothing about this process is transmitted anywhere to fill
  /// it in (PRD §3, §70).
  /// </remarks>
  public SignatureStatus PackageStatus;

  /// <summary>
  /// One sentence naming what was actually compared, so the word above is never the whole story.
  /// </summary>
  public string? PackageStatusDetail;

  /// <summary>
  /// Whether anybody this machine trusts signed for the image (PRD §70).
  /// </summary>
  /// <remarks>
  /// Trust-chain verification, and its own reading. On Linux it is what the packaging system
  /// recorded about the package rather than about the file — <c>pacman</c>'s <c>%VALIDATION%</c>,
  /// the fact <c>pacman -Qi</c> prints as "Validated By". It is routinely not the same answer as
  /// <see cref="PackageStatus"/>: a package built on this machine ships files that match their
  /// record exactly and carries nobody's signature, and reporting that in one word lost whichever
  /// half the reader wanted.
  /// </remarks>
  public SignatureStatus TrustChain;

  /// <summary>One sentence naming what stands behind the package, or what does not.</summary>
  public string? TrustChainDetail;

  /// <summary>
  /// Why <see cref="TrustChain"/> is <see cref="SignatureStatus.NotChecked"/>: not asked for, or a
  /// packaging system with no concept of a signature over an installed file.
  /// </summary>
  /// <remarks>
  /// <see cref="SignatureStatus.Unsigned"/> is a finding — somebody looked and nothing had signed
  /// it — so the absence of an answer needs a reason of its own rather than borrowing that word
  /// (PRD §72.3).
  /// </remarks>
  public UnknownReason TrustChainReason;

  /// <summary>
  /// Whether the image's own embedded signature still covers the bytes that are running (PRD §21,
  /// §70).
  /// </summary>
  /// <remarks>
  /// The same one of §70's five questions <see cref="PackageStatus"/> answers, asked of a different
  /// kind of evidence, which is why it is a field of its own rather than the same one wearing a
  /// second name. A PE image carries a signature inside it and an ELF does not; a Linux package
  /// database records a digest for a file and Windows has no such database. Folding the two together
  /// would make one column mean "the packaging system still recognises these bytes" on one machine
  /// and "the publisher's signature still covers them" on the other, which is precisely the false
  /// equivalence §5.3 forbids.
  /// <para>
  /// Never a trust chain: nothing behind this asks whether the certificate chains to a root this
  /// machine believes in. That is <see cref="TrustChain"/>, and it stays unanswered on Windows.
  /// </para>
  /// </remarks>
  public SignatureStatus ImageSignature;

  /// <summary>
  /// One sentence naming what was actually compared, so the word above is never the whole story.
  /// </summary>
  public string? ImageSignatureDetail;

  /// <summary>
  /// Why <see cref="ImageSignature"/> is <see cref="SignatureStatus.NotChecked"/>: nobody asked, no
  /// image to read, or a platform whose executables carry no signature to check at all.
  /// </summary>
  public UnknownReason ImageSignatureReason;

  /// <summary>
  /// Who the signing certificate says signed the image, by its common name (PRD §21).
  /// </summary>
  /// <remarks>
  /// Read out of the signature rather than out of the version resource, which is the difference
  /// between this and <see cref="ImageCompany"/>: a company name in a version resource is a string
  /// the publisher typed and anybody may type it, while this one is bound to a private key the
  /// signature was made with. That binding is what <see cref="ImageSignature"/> reports on, and
  /// without it this is a claim like any other — which is why the two are never read apart.
  /// </remarks>
  public string? ImageSigner;

  /// <summary>The signing certificate's whole subject, where its common name is not enough.</summary>
  public string? CertificateSubject;

  /// <summary>
  /// Who issued the signing certificate.
  /// </summary>
  /// <remarks>
  /// Who put their name to the signer, and not who this machine trusts — those are the same fact
  /// only on a machine whose root store happens to contain this issuer, and nothing here has looked.
  /// </remarks>
  public string? CertificateIssuer;

  /// <summary>
  /// When the signature was countersigned, in UTC ticks; nought where nothing countersigned it
  /// (PRD §21).
  /// </summary>
  /// <remarks>
  /// Its own field because a countersigned timestamp is what keeps a signature valid after the
  /// certificate behind it has expired, which is the ordinary state of most signed software. Nought
  /// is a real answer — a great deal of software is signed and never dated — and is not the same
  /// finding as an unknown, which is why it is a value rather than a reason.
  /// </remarks>
  public Counter SignatureTimestampUtcTicks;

  /// <summary>
  /// What a person calls the program, out of the desktop entry that starts it (PRD §14).
  /// </summary>
  /// <remarks>
  /// The Linux answer to a Windows binary's product name. There is nothing inside an ELF to read it
  /// out of, so it comes from the <c>.desktop</c> file whose <c>Exec</c> starts this image — the
  /// same string the machine's own menu shows. Null with
  /// <see cref="ApplicationNameReason"/> at <see cref="UnknownReason.None"/> means the machine has
  /// no entry for the program, which is most of a process table and is a finding rather than a hole.
  /// </remarks>
  public string? ApplicationName;

  /// <summary>
  /// Set when more than one application starts this program and nothing distinguishes them.
  /// </summary>
  /// <remarks>
  /// Its own answer, because it is not the absence of one. Eight desktop entries start
  /// <c>libreoffice</c> on the machine this was written on and each carries a different name;
  /// picking one would report a spreadsheet as a drawing half the time (PRD §5.3).
  /// </remarks>
  public bool ApplicationNameAmbiguous;

  /// <summary>
  /// Why <see cref="ApplicationName"/> is <see langword="null"/>: not asked for, no image to look
  /// up, or no permission to see which image it is.
  /// </summary>
  public UnknownReason ApplicationNameReason;

  /// <summary>
  /// What is executing inside the process — a runtime, or machine code (PRD §14).
  /// </summary>
  /// <remarks>
  /// From the module list, never from the name: a process called <c>java</c> may be a shell script,
  /// and one called anything at all may have a virtual machine in it (PRD §5.3).
  /// </remarks>
  public ProcessRuntime Runtime;

  /// <summary>
  /// Why <see cref="Runtime"/> is <see cref="ProcessRuntime.Unknown"/>: not asked for, no image to
  /// look inside — a kernel thread has none — or a module list this user may not read.
  /// </summary>
  /// <remarks>
  /// <see cref="ProcessRuntime.Native"/> is the answer when the modules were read and none of them
  /// was a runtime, so the enum cannot carry the reason as well: "there is no virtual machine in
  /// here" and "nobody could look" are opposite statements (PRD §72.3).
  /// </remarks>
  public UnknownReason RuntimeReason;

  /// <summary>
  /// When the image file was created, in UTC ticks, where the file system remembers (PRD §14).
  /// </summary>
  /// <remarks>
  /// <c>statx</c>'s birth time. Plenty of file systems do not carry one — an ext4 made without
  /// <c>crtime</c>, most network file systems — and there the kernel returns the field unset rather
  /// than a date. That is unknown and not the epoch: a column of 1970 would be a lie the width of
  /// the table (PRD §72.3).
  /// </remarks>
  public Counter ImageCreatedUtcTicks;

  /// <summary>
  /// What the running image says about itself in its version resource (PRD §14).
  /// </summary>
  /// <remarks>
  /// A PE keeps these five strings inside the file; an ELF has no such section and never did, so on
  /// Linux the same facts come from the package database and live in <see cref="Package"/> and
  /// <see cref="ApplicationName"/> instead. Keeping them apart is the point: a package's version is
  /// not a file's version, and a column that showed one under the other's name would be stating
  /// something false (PRD §5.3).
  /// <para>
  /// Read once per <em>image</em> rather than once per process — three hundred processes of one
  /// runtime share one binary — and only when a column or a filter names one of them, because the
  /// cost is opening and reading a file (PRD §5.4).
  /// </para>
  /// </remarks>
  public string? ImageDescription;
  public string? ImageCompany;
  public string? ImageProduct;
  public string? ImageProductVersion;
  public string? ImageFileVersion;

  /// <summary>
  /// Why the five strings above are <see langword="null"/>: not asked for, no image to read, an
  /// image this user may not open, or a program that ships no version resource at all.
  /// </summary>
  /// <remarks>
  /// The last of those is the common one and is a finding rather than a gap — a great many programs
  /// carry no version resource — which is why a string field cannot carry it and a reason must
  /// (PRD §72.3).
  /// </remarks>
  public UnknownReason ImageVersionReason;

  /// <summary>
  /// The <c>IMAGE_SUBSYSTEM_*</c> value out of the image's optional header (PRD §14).
  /// </summary>
  /// <remarks>
  /// What the loader is expected to give the program: a window station, a console, or nothing at
  /// all. Kept as the raw number for the same reason <see cref="IntegrityLevel"/> is — a subsystem
  /// Microsoft adds later shows as its number instead of being flattened into the nearest name this
  /// build happens to know. PE only: an ELF declares no such thing, and the field renders as not
  /// applicable there rather than as the unknown subsystem, which is a different statement.
  /// </remarks>
  public Counter Subsystem;

  /// <summary>
  /// Which instruction set the process is being translated from, or 0 when it is not (PRD §14).
  /// </summary>
  /// <remarks>
  /// The <c>IMAGE_FILE_MACHINE_*</c> value <c>IsWow64Process2</c> reports for the process, which is
  /// <c>IMAGE_FILE_MACHINE_UNKNOWN</c> — nought — for a process running natively. Nought is
  /// therefore a real answer here and the ordinary one, and the counter's business is the other
  /// case: a process that would not open, or a platform with no such notion.
  /// <para>
  /// The guest machine rather than the host's: every row on a machine shares the host, so naming it
  /// per process would repeat one fact six hundred times, while what differs between rows — an x86
  /// program on an x64 machine, an x64 program on an ARM64 one — is the half worth a column.
  /// </para>
  /// </remarks>
  public Counter Emulation;

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

  /// <summary>
  /// The <see cref="LsmConfinementMode"/> the label states, as its ordinal (PRD §21).
  /// </summary>
  /// <remarks>
  /// Derived from <see cref="SecurityContext"/> and so only ever filled when that was asked for,
  /// which is what keeps it free: the bracketed word is already inside the string the label column
  /// reads, and this is the same fact in a form that can be sorted and filtered on. A label that
  /// states no mode — every SELinux context does — leaves this unknown rather than inventing one.
  /// </remarks>
  public Counter ConfinementMode;

}
