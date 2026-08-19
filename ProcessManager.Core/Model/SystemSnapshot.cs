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

  public Counter ContextSwitches;
  public Counter Interrupts;
  public Counter ProcessesCreated;

  public double LoadAverage1;
  public double LoadAverage5;
  public double LoadAverage15;

  public int RunningProcesses;
  public int TotalThreads;

  /// <summary>Seconds since boot.</summary>
  public double UptimeSeconds;

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
  internal Span<ProcessRecord> PrepareProcesses(int count) {
    if (this._processes.Length < count)
      Array.Resize(ref this._processes, Math.Max(count, this._processes.Length * 2));

    this.ProcessCount = count;
    return this._processes.AsSpan(0, count);
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
    this.System = default;
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
