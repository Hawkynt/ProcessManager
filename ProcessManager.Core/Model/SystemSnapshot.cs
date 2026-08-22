namespace Hawkynt.ProcessManager.Model;

/// <summary>Machine-wide readings taken with the same sample as the process list.</summary>
public struct SystemCounters {

  public int CoreCount;

  /// <summary>Aggregate CPU time across all cores.</summary>
  public CpuTimes Cpu;

  public Counter TotalMemoryBytes;
  public Counter AvailableMemoryBytes;
  public Counter CachedMemoryBytes;
  public Counter TotalSwapBytes;
  public Counter UsedSwapBytes;

  /// <summary>
  /// Physically unallocated. Not the same as available, and much smaller on any machine that has
  /// been up a while: a healthy Linux keeps almost nothing free because it caches with the rest, and
  /// reporting free as "how much you can use" is the single most misread number in memory (PRD §47).
  /// </summary>
  public Counter FreeMemoryBytes;

  /// <summary>Cache pages holding file data that has not been read into it yet — block buffers.</summary>
  public Counter BufferMemoryBytes;

  /// <summary>
  /// Dirty and in-writeback pages: cache whose contents differ from the disk, so it cannot simply be
  /// dropped. Windows calls this modified and shows it as its own band of the composition bar.
  /// </summary>
  public Counter ModifiedMemoryBytes;

  /// <summary>Changed in memory and not yet handed to the disk.</summary>
  /// <remarks>
  /// The half of <see cref="ModifiedMemoryBytes"/> that has not started moving, and worth its own
  /// row beside <see cref="WritebackBytes"/>: a machine with gigabytes dirty and nothing in
  /// writeback is one the kernel has not begun flushing, and a machine with the reverse is one
  /// already waiting on its disk (PRD §47).
  /// </remarks>
  public Counter DirtyBytes;

  /// <summary>On its way to the disk right now.</summary>
  public Counter WritebackBytes;

  /// <summary>
  /// Anonymous pages: memory with nothing behind it but swap.
  /// </summary>
  /// <remarks>
  /// The distinction that decides what happens under pressure. File-backed pages can be dropped and
  /// read back; anonymous ones can only be compressed or written to swap, and on a machine without
  /// swap they cannot go anywhere at all — which is how a machine gets killed by the OOM reaper
  /// while it is still showing gigabytes of cache.
  /// </remarks>
  public Counter AnonymousBytes;

  /// <summary>File pages mapped into some process's address space, as opposed to merely cached.</summary>
  public Counter MappedBytes;

  /// <summary>Anonymous pages the kernel holds in swap as well as in memory.</summary>
  /// <remarks>
  /// Already written out and still resident, so they can be dropped without any further I/O. Their
  /// presence is the record of a machine that has been under pressure and has since recovered.
  /// </remarks>
  public Counter SwapCachedBytes;

  /// <summary>What the compressed pool occupies in memory.</summary>
  public Counter CompressedBytes;

  /// <summary>What those pages would occupy uncompressed, which is what makes the pool worth having.</summary>
  /// <remarks>
  /// The two are only useful together: 1.1 GB holding 2.4 GB is a machine that has saved itself
  /// 1.3 GB of swapping, and either figure alone says nothing about the ratio (PRD §47).
  /// </remarks>
  public Counter CompressedOriginalBytes;

  /// <summary>Every slab allocation, reclaimable and not — the sum the kernel itself reports.</summary>
  public Counter SlabBytes;

  /// <summary>Pages that can never be swapped: <c>mlock</c>ed, and the kernel's own unevictable lists.</summary>
  public Counter UnevictableBytes;

  /// <summary>The part of that a process asked for by locking it.</summary>
  public Counter LockedBytes;

  /// <summary>Kernel virtual mappings in use, which on a machine with many modules is not small.</summary>
  public Counter VmallocUsedBytes;

  /// <summary>Per-CPU allocator memory, which grows with the core count rather than with the load.</summary>
  public Counter PerCpuBytes;

  /// <summary>
  /// Memory the machine has withdrawn because it failed.
  /// </summary>
  /// <remarks>
  /// Almost always zero, and worth a row for exactly that reason: anything else is a dying DIMM, and
  /// that is the sort of finding somebody opens a memory page hoping to be told rather than hoping
  /// to deduce.
  /// </remarks>
  public Counter HardwareCorruptedBytes;

  /// <summary>How large one huge page is here — 2 MB on most machines, 1 GB where configured.</summary>
  public Counter HugePageSizeBytes;

  /// <summary>Explicitly reserved huge pages, counted in pages rather than in bytes.</summary>
  public Counter HugePagesTotal;

  public Counter HugePagesFree;

  /// <summary>Promised to a process that has not faulted them in yet.</summary>
  public Counter HugePagesReserved;

  /// <summary>The whole <c>hugetlb</c> reservation in bytes, which is carved out of the total.</summary>
  /// <remarks>
  /// Part of no other band: pages here leave the kernel's ordinary accounting the moment they are
  /// reserved, whether or not anything ever touches them. A machine configured for a database with
  /// sixteen gigabytes of huge pages is missing sixteen gigabytes everywhere else, and nothing but
  /// this row explains where they went.
  /// </remarks>
  public Counter HugeTlbBytes;

  /// <summary>Anonymous memory the kernel has quietly backed with huge pages.</summary>
  public Counter AnonymousHugePagesBytes;

  /// <summary>Shared memory backed by huge pages.</summary>
  public Counter SharedHugePagesBytes;

  /// <summary>File-backed memory backed by huge pages.</summary>
  public Counter FileHugePagesBytes;

  /// <summary>The four reclaim lists: active and inactive, anonymous and file-backed.</summary>
  /// <remarks>
  /// What the kernel will take first. The inactive lists are the candidates: a machine whose file
  /// pages are nearly all active is one that will pay for dropping its cache, and a machine whose
  /// anonymous pages are mostly inactive is one about to swap.
  /// </remarks>
  public Counter ActiveAnonymousBytes;

  public Counter InactiveAnonymousBytes;

  public Counter ActiveFileBytes;

  public Counter InactiveFileBytes;

  /// <summary>Address space every process together has asked for, which may exceed what exists.</summary>
  public Counter CommittedBytes;

  /// <summary>The most the kernel will commit — RAM plus swap, adjusted by the overcommit policy.</summary>
  public Counter CommitLimitBytes;

  /// <summary>Kernel allocations it can hand back under pressure. The paged pool's counterpart.</summary>
  public Counter ReclaimableKernelBytes;

  /// <summary>Kernel allocations it cannot. The non-paged pool's counterpart.</summary>
  public Counter UnreclaimableKernelBytes;

  /// <summary>Memory spent on the page tables themselves, which on a large machine is not small.</summary>
  public Counter PageTableBytes;

  /// <summary>Kernel stacks, one per thread — a count of threads in another unit.</summary>
  public Counter KernelStackBytes;

  /// <summary>tmpfs and shared anonymous pages: counted as cache, but not reclaimable like cache.</summary>
  public Counter SharedMemoryBytes;

  /// <summary>
  /// How much each resource is stalling the machine (PRD §46).
  /// </summary>
  /// <remarks>
  /// A different question from utilisation, and usually the better one. A processor at 100 % is not
  /// in trouble if nothing is waiting for it; a processor at 60 % with things queued behind it is.
  /// </remarks>
  public PressureReading CpuPressure;

  public PressureReading MemoryPressure;

  public PressureReading IoPressure;

  public Counter ContextSwitches;
  public Counter Interrupts;

  /// <summary>
  /// Soft interrupts — the kernel's deferred work, and what Windows calls a DPC (PRD §46).
  /// </summary>
  /// <remarks>
  /// A hard interrupt is the handler that must run now and is kept as short as the driver can make
  /// it; everything it defers runs as a soft interrupt afterwards. Which is why the two are counted
  /// apart: a saturated network adapter raises one hard interrupt per batch and thousands of soft
  /// ones behind it, and a machine whose cores are in <c>ksoftirqd</c> is one whose deferred work
  /// has stopped keeping up rather than one that is busy.
  /// </remarks>
  public Counter SoftInterrupts;

  public Counter ProcessesCreated;

  /// <summary>
  /// Open file descriptors across the whole machine — §46's handle count, in this kernel's terms.
  /// </summary>
  /// <remarks>
  /// A descriptor is what Linux has instead of a handle, and the kernel keeps the running total in
  /// one file rather than making it a sum over every process. That is the whole reason it is
  /// affordable here: the per-process figure costs a directory materialisation each and is
  /// deliberately not sampled (§3.5).
  /// </remarks>
  public Counter OpenDescriptors;

  /// <summary>
  /// The most the kernel will hand out, where that is a real limit.
  /// </summary>
  /// <remarks>
  /// Usually not one worth showing: <c>fs.file-max</c> is derived from memory and is routinely nine
  /// quintillion, which as a denominator says nothing at all. Kept as a counter so a caller can see
  /// the figure and decide, rather than being handed a percentage of an imaginary ceiling.
  /// </remarks>
  public Counter DescriptorLimit;

  public double LoadAverage1;
  public double LoadAverage5;
  public double LoadAverage15;

  public int RunningProcesses;
  public int TotalThreads;

  /// <summary>Seconds since boot.</summary>
  public double UptimeSeconds;

  /// <summary>
  /// Every counter explicitly unread, which is what a snapshot starts each sample as.
  /// </summary>
  /// <remarks>
  /// <c>default(Counter)</c> is a confident zero, so a plain <c>default(SystemCounters)</c> claims a
  /// machine with no memory, no swap, no kernel allocations and no pressure — and a probe that fills
  /// in twelve of these fields leaves the rest reading as measured zeros rather than as figures
  /// nobody asked for. A graph drawn from one of those is a flat line that never happened (PRD §5.3,
  /// §72.3). Every field is named here rather than derived, because the only way this stays true as
  /// counters are added is for the compiler to have nothing to guess.
  /// </remarks>
  public static SystemCounters Unread => new() {
    TotalMemoryBytes = Counter.NotSampledYet,
    AvailableMemoryBytes = Counter.NotSampledYet,
    CachedMemoryBytes = Counter.NotSampledYet,
    TotalSwapBytes = Counter.NotSampledYet,
    UsedSwapBytes = Counter.NotSampledYet,
    FreeMemoryBytes = Counter.NotSampledYet,
    BufferMemoryBytes = Counter.NotSampledYet,
    ModifiedMemoryBytes = Counter.NotSampledYet,
    DirtyBytes = Counter.NotSampledYet,
    WritebackBytes = Counter.NotSampledYet,
    AnonymousBytes = Counter.NotSampledYet,
    MappedBytes = Counter.NotSampledYet,
    SwapCachedBytes = Counter.NotSampledYet,
    CompressedBytes = Counter.NotSampledYet,
    CompressedOriginalBytes = Counter.NotSampledYet,
    SlabBytes = Counter.NotSampledYet,
    UnevictableBytes = Counter.NotSampledYet,
    LockedBytes = Counter.NotSampledYet,
    VmallocUsedBytes = Counter.NotSampledYet,
    PerCpuBytes = Counter.NotSampledYet,
    HardwareCorruptedBytes = Counter.NotSampledYet,
    HugePageSizeBytes = Counter.NotSampledYet,
    HugePagesTotal = Counter.NotSampledYet,
    HugePagesFree = Counter.NotSampledYet,
    HugePagesReserved = Counter.NotSampledYet,
    HugeTlbBytes = Counter.NotSampledYet,
    AnonymousHugePagesBytes = Counter.NotSampledYet,
    SharedHugePagesBytes = Counter.NotSampledYet,
    FileHugePagesBytes = Counter.NotSampledYet,
    ActiveAnonymousBytes = Counter.NotSampledYet,
    InactiveAnonymousBytes = Counter.NotSampledYet,
    ActiveFileBytes = Counter.NotSampledYet,
    InactiveFileBytes = Counter.NotSampledYet,
    CommittedBytes = Counter.NotSampledYet,
    CommitLimitBytes = Counter.NotSampledYet,
    ReclaimableKernelBytes = Counter.NotSampledYet,
    UnreclaimableKernelBytes = Counter.NotSampledYet,
    PageTableBytes = Counter.NotSampledYet,
    KernelStackBytes = Counter.NotSampledYet,
    SharedMemoryBytes = Counter.NotSampledYet,
    CpuPressure = PressureReading.Unknown,
    MemoryPressure = PressureReading.Unknown,
    IoPressure = PressureReading.Unknown,
    ContextSwitches = Counter.NotSampledYet,
    Interrupts = Counter.NotSampledYet,
    SoftInterrupts = Counter.NotSampledYet,
    ProcessesCreated = Counter.NotSampledYet,
    OpenDescriptors = Counter.NotSampledYet,
    DescriptorLimit = Counter.NotSampledYet,
  };

}

/// <summary>
/// One reading of the whole machine: a monotonic timestamp, the system counters, and every process
/// that was visible. Owned by the <see cref="Sampling.Sampler"/>, which recycles the buffers — hold
/// on to a snapshot past the next sample and you are reading the next sample.
/// </summary>
public sealed class SystemSnapshot {

  private ProcessRecord[] _processes = new ProcessRecord[512];
  private CpuTimes[] _perCore = new CpuTimes[16];

  /// <summary>
  /// <see cref="System.Diagnostics.Stopwatch"/> ticks. Monotonic on purpose: a wall clock stepped by
  /// NTP or by a suspend/resume would produce negative intervals and infinite rates (PRD §3.1).
  /// </summary>
  public long TimestampTicks { get; internal set; }

  public SystemCounters System;

  public int ProcessCount { get; internal set; }

  public int PerCoreCount { get; internal set; }

  /// <summary>Every visible process, in whatever order the probe produced.</summary>
  public ReadOnlySpan<ProcessRecord> Processes => this._processes.AsSpan(0, this.ProcessCount);

  /// <summary>Per-core CPU times, indexed by logical core.</summary>
  public ReadOnlySpan<CpuTimes> PerCore => this._perCore.AsSpan(0, this.PerCoreCount);

  /// <summary>The writable process buffer, for the probe filling this snapshot.</summary>
  internal Span<ProcessRecord> ProcessBuffer => this._processes.AsSpan(0, this.ProcessCount);

  internal Span<CpuTimes> PerCoreBuffer => this._perCore.AsSpan(0, this.PerCoreCount);

  /// <summary>
  /// Makes room for <paramref name="count"/> processes, reusing the existing array whenever it is
  /// big enough. Growth is the only allocation a steady-state sample is allowed (PRD §4).
  /// </summary>
  private readonly Dictionary<int, string> _namesByPid = [];

  /// <summary>
  /// Fills in each process's parent name, once the whole table has been read.
  /// </summary>
  /// <remarks>
  /// Here rather than in either probe, because it is a fact about the table rather than about any
  /// one process: the parent's name cannot be known until every row exists. The dictionary is kept
  /// and cleared rather than made afresh, and what is stored is the parent's own name instance, so a
  /// steady-state sample pays no allocation for this at all (PRD §4).
  /// </remarks>
  internal void ResolveParentNames() {
    var count = this.ProcessCount;
    var processes = this._processes;

    this._namesByPid.Clear();
    for (var i = 0; i < count; ++i)
      // The last writer wins on a duplicate pid, which cannot happen in one sample of /proc but can
      // in a recorded tree somebody edited by hand.
      this._namesByPid[processes[i].Pid] = processes[i].Name;

    for (var i = 0; i < count; ++i) {
      var parent = processes[i].ParentPid;
      // A process that claims itself as its parent is what /proc reports for pid 1 inside a
      // container; naming it after itself would read as though it had forked itself.
      processes[i].ParentName = parent > 0
        && parent != processes[i].Pid
        && this._namesByPid.TryGetValue(parent, out var name)
          ? name
          : null;
    }
  }

  /// <summary>
  /// Hands a probe the span of records to fill, every one of them already saying that the readings
  /// only one platform can take have not been taken.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>default(Counter)</c> is a <em>confident nought</em>, and this program has already shipped one
  /// of those: <c>default(SystemCounters)</c> reported machines as having no free memory at all. The
  /// same shape of bug on a security column is worse — a mitigation policy nobody filled would read
  /// as a mitigation that is switched off, and a protection level nobody filled would read as
  /// <c>PROTECTION_LEVEL_WINTCB_LIGHT</c>, which is a real and high level rather than "none"
  /// (PRD §72.3).
  /// </para>
  /// <para>
  /// The established way out of that is for each probe to say <c>Counter.NotSupported</c> for
  /// everything its platform cannot answer, and the probes do that for the readings they know about.
  /// It does not scale to a reading a probe has never heard of: the Windows-only fields added for
  /// PRD §20 and §21 are not something the Linux or macOS probe should have to know exists in order
  /// to avoid claiming a value for it. So the default is set here, once, where a record is handed
  /// out — a record starts out knowing nothing, and a probe that can answer overwrites it.
  /// </para>
  /// </remarks>
  internal Span<ProcessRecord> PrepareProcesses(int count) {
    if (this._processes.Length < count)
      Array.Resize(ref this._processes, Math.Max(count, this._processes.Length * 2));

    this.ProcessCount = count;
    var span = this._processes.AsSpan(0, count);
    for (var i = 0; i < span.Length; ++i)
      ProcessRecord.ClearPlatformReadings(ref span[i]);

    return span;
  }

  private DiskCounters[] _disks = [];
  private NetworkCounters[] _networks = [];

  /// <summary>Storage devices, whole ones rather than partitions (PRD §48).</summary>
  public ReadOnlySpan<DiskCounters> Disks => this._disks.AsSpan(0, this.DiskCount);

  public int DiskCount { get; private set; }

  /// <summary>Network interfaces (PRD §49).</summary>
  public ReadOnlySpan<NetworkCounters> Networks => this._networks.AsSpan(0, this.NetworkCount);

  public int NetworkCount { get; private set; }

  internal Span<DiskCounters> PrepareDisks(int count) {
    if (this._disks.Length < count)
      Array.Resize(ref this._disks, Math.Max(count, Math.Max(8, this._disks.Length * 2)));

    this.DiskCount = count;
    return this._disks.AsSpan(0, count);
  }

  internal Span<NetworkCounters> PrepareNetworks(int count) {
    if (this._networks.Length < count)
      Array.Resize(ref this._networks, Math.Max(count, Math.Max(8, this._networks.Length * 2)));

    this.NetworkCount = count;
    return this._networks.AsSpan(0, count);
  }

  internal Span<CpuTimes> PrepareCores(int count) {
    if (this._perCore.Length < count)
      Array.Resize(ref this._perCore, Math.Max(count, this._perCore.Length * 2));

    this.PerCoreCount = count;
    return this._perCore.AsSpan(0, count);
  }

  internal void Clear() {
    this.ProcessCount = 0;
    this.PerCoreCount = 0;
    // Unread rather than default: see SystemCounters.Unread. A probe that fills in some of these
    // must leave the rest saying nobody looked, not saying zero.
    this.System = SystemCounters.Unread;
  }

  /// <summary>
  /// Finds a process by identity and says where it is, which is what the rate accessors want.
  /// </summary>
  /// <remarks>
  /// The record alone is not enough for anything measured over an interval: a rate lives in the
  /// delta, indexed by position in this snapshot, so a caller holding only a key cannot ask for one.
  /// Linear, like the overload below it, and for the same callers — one lookup when something is
  /// pointed at, not one per row per frame.
  /// </remarks>
  public bool TryGetProcess(ProcessKey key, out ProcessRecord record, out int index) {
    var processes = this.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      if (processes[i].Key != key)
        continue;

      record = processes[i];
      index = i;
      return true;
    }

    record = default;
    index = -1;
    return false;
  }

  /// <summary>Finds a process by identity. Linear — callers in a loop want the delta's index map.</summary>
  public bool TryGetProcess(ProcessKey key, out ProcessRecord record) {
    var processes = this.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      if (processes[i].Key != key)
        continue;

      record = processes[i];
      return true;
    }

    record = default;
    return false;
  }

}
