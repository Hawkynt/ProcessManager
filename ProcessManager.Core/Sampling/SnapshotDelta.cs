using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>
/// Everything that needed two snapshots to know: rates, percentages, and which processes appeared
/// and vanished between them.
/// </summary>
/// <remarks>
/// Indexed by position in the <em>current</em> snapshot, so a front-end that is already walking
/// <see cref="SystemSnapshot.Processes"/> pays nothing to ask for the matching rate. Buffers are
/// reused across updates: a delta is as long-lived as the sampler that owns it.
/// </remarks>
public sealed class SnapshotDelta {

  private readonly Dictionary<ProcessKey, int> _previousIndex = [];
  private readonly List<ProcessKey> _exited = [];
  private Rate[] _cpuPercent = [];
  private Rate[] _cpuPercentPerCore = [];
  private Rate[] _cpuPercentDelta = [];
  // The share each process had in the interval before this one, in the previous snapshot's order.
  // Two arrays swapped rather than one copied: the buffer this call is about to overwrite is
  // exactly the one holding last call's answer.
  private Rate[] _previousCpuPercent = [];
  // How many of those entries were actually written. The arrays only ever grow and a grown one is
  // a fresh allocation, so every slot past this is default(Rate) — which is a confident zero, and
  // reading one would report a process's CPU as unchanged when nobody measured it (PRD §72.3).
  private int _previousCpuPercentCount;
  private Rate[] _readBytesPerSecond = [];
  private Rate[] _writeBytesPerSecond = [];
  private Rate[] _otherBytesPerSecond = [];
  private Rate[] _pageFaultsPerSecond = [];
  private Rate[] _contextSwitchesPerSecond = [];
  private Rate[] _cyclesPerSecond = [];
  private Rate[] _privateBytesDelta = [];
  private bool[] _isNew = [];
  private Rate[] _memoryPercent = [];
  private Rate[] _gpuGraphicsPercent = [];
  private Rate[] _gpuComputePercent = [];
  private Rate[] _gpuCopyPercent = [];
  private Rate[] _gpuEncodePercent = [];
  private Rate[] _gpuDecodePercent = [];
  private Rate[] _gpuDedicatedBytesDelta = [];
  private GpuEngine[] _gpuEngine = [];
  private Rate[] _perCoreBusy = [];
  private Rate[] _perCoreKernel = [];
  private Rate[] _perCoreUser = [];

  /// <summary>False until two samples have been taken; every rate is then <see cref="Rate.NotSampledYet"/>.</summary>
  public bool HasPrevious { get; private set; }

  /// <summary>Wall-clock nanoseconds between the two samples, measured on the monotonic clock.</summary>
  public double ElapsedNanoseconds { get; private set; }

  public double ElapsedSeconds => this.ElapsedNanoseconds / 1_000_000_000d;

  /// <summary>Busy percentage of the whole machine.</summary>
  public Rate SystemCpuPercent { get; private set; } = Rate.NotSampledYet;

  /// <summary>Processes present in the previous snapshot and gone from this one.</summary>
  public IReadOnlyList<ProcessKey> Exited => this._exited;

  /// <summary>How many processes appeared since the previous snapshot.</summary>
  public int StartedCount { get; private set; }

  public Rate CpuPercent(int index) => this._cpuPercent[index];

  /// <summary>
  /// The same CPU figure in the other convention, so both columns can be shown at once.
  /// </summary>
  /// <remarks>
  /// Process Explorer shows one and System Informer both; the two differ by the core count and a
  /// reader who has to remember which is on screen is a reader who will misread one of them (§3.2).
  /// </remarks>
  public Rate CpuPercentPerCore(int index) => this._cpuPercentPerCore[index];

  /// <summary>
  /// How far the process's share of the processor moved between the previous interval and this one,
  /// in percentage points (PRD §15).
  /// </summary>
  /// <remarks>
  /// The derivative of a derivative, and it needs three samples rather than two: a process that has
  /// just started working stands out from one that has been busy all along, and the CPU column alone
  /// cannot tell them apart. Signed, because the process that has just <em>stopped</em> is as
  /// interesting as the one that started. Unknown until the third sample rather than nought, which
  /// would read as "steady" for a whole interval after start-up.
  /// </remarks>
  public Rate CpuPercentDelta(int index) => this._cpuPercentDelta[index];

  /// <summary>Bytes read, written and neither, per second.</summary>
  public Rate OtherBytesPerSecond(int index) => this._otherBytesPerSecond[index];

  /// <summary>Read + write + other, which is the column Process Explorer calls "I/O total rate".</summary>
  public Rate IoTotalBytesPerSecond(int index) {
    var read = this._readBytesPerSecond[index];
    var write = this._writeBytesPerSecond[index];
    var other = this._otherBytesPerSecond[index];
    if (!read.HasValue && !write.HasValue && !other.HasValue)
      return read;

    return Rate.Of(read.GetValueOrDefault() + write.GetValueOrDefault() + other.GetValueOrDefault());
  }

  public Rate PageFaultsPerSecond(int index) => this._pageFaultsPerSecond[index];

  public Rate ContextSwitchesPerSecond(int index) => this._contextSwitchesPerSecond[index];

  public Rate CyclesPerSecond(int index) => this._cyclesPerSecond[index];

  /// <summary>
  /// How much the committed private bytes moved since the last sample, in bytes per second.
  /// </summary>
  /// <remarks>
  /// Signed, unlike every other rate here, because the interesting reading is the negative one: a
  /// process whose private bytes only ever climb is the one leaking.
  /// </remarks>
  public Rate PrivateBytesDelta(int index) => this._privateBytesDelta[index];

  public Rate ReadBytesPerSecond(int index) => this._readBytesPerSecond[index];

  public Rate WriteBytesPerSecond(int index) => this._writeBytesPerSecond[index];

  /// <summary>True when this process was not in the previous snapshot (the green flash).</summary>
  public bool IsNew(int index) => this._isNew[index];

  /// <summary>
  /// What share of the machine's memory a process holds resident.
  /// </summary>
  /// <remarks>
  /// Computed here rather than where the columns are rendered, because that is the only place the
  /// machine's total is in scope alongside the process — a percentage of an unknown total is not a
  /// percentage.
  /// </remarks>
  public Rate MemoryPercent(int index)
    => (uint)index < (uint)this._memoryPercent.Length ? this._memoryPercent[index] : Rate.NotSampledYet;

  #region graphics (PRD §19)

  /// <summary>What share of the interval this process kept each engine of its adapter busy.</summary>
  public Rate GpuGraphicsPercent(int index) => this._gpuGraphicsPercent[index];

  public Rate GpuComputePercent(int index) => this._gpuComputePercent[index];

  public Rate GpuCopyPercent(int index) => this._gpuCopyPercent[index];

  public Rate GpuEncodePercent(int index) => this._gpuEncodePercent[index];

  public Rate GpuDecodePercent(int index) => this._gpuDecodePercent[index];

  /// <summary>
  /// The process's use of its adapter: the busiest of its engines, and nothing summed.
  /// </summary>
  /// <remarks>
  /// A card's engines run at once and their percentages are each of the whole interval, so adding
  /// them produces figures above a hundred for a process that is transcoding perfectly happily. The
  /// maximum is what Task Manager's GPU column shows and the only one of the two that can be read as
  /// "how much of this card is this process using".
  /// </remarks>
  public Rate GpuPercent(int index) => this.GpuEnginePercent(index);

  /// <summary>The busiest engine's share, which is the number <see cref="BusiestGpuEngine"/> names.</summary>
  public Rate GpuEnginePercent(int index) => this._gpuEnginePercent[index];

  /// <summary>
  /// Which engine <see cref="GpuEnginePercent"/> measured, or <see cref="GpuEngine.Unknown"/> when
  /// nothing could be measured at all.
  /// </summary>
  public GpuEngine BusiestGpuEngine(int index) => this._gpuEngine[index];

  /// <summary>How fast the process's adapter memory is moving, in bytes per second.</summary>
  /// <remarks>
  /// Signed, like the private-bytes delta and for the same reason: a renderer that only ever grows
  /// its VRAM allocation is the one that will eventually stop the machine drawing anything.
  /// </remarks>
  public Rate GpuDedicatedBytesDelta(int index) => this._gpuDedicatedBytesDelta[index];

  private Rate[] _gpuEnginePercent = [];

  #endregion

  /// <summary>Busy percentage of one logical core.</summary>
  public Rate PerCoreBusyPercent(int core)
    => (uint)core < (uint)this._perCoreBusy.Length ? this._perCoreBusy[core] : Rate.NotSampledYet;

  /// <summary>How much of one core's interval went to the kernel (PRD §46).</summary>
  public Rate PerCoreKernelPercent(int core)
    => (uint)core < (uint)this._perCoreKernel.Length ? this._perCoreKernel[core] : Rate.NotSampledYet;

  /// <summary>How much of one core's interval went to user code.</summary>
  public Rate PerCoreUserPercent(int core)
    => (uint)core < (uint)this._perCoreUser.Length ? this._perCoreUser[core] : Rate.NotSampledYet;

  /// <summary>
  /// Context switches across the whole machine, per second (PRD §51).
  /// </summary>
  /// <remarks>
  /// Not the sum of the per-process figures: the kernel's own counter includes switches into and out
  /// of processes that came and went between two samples, which a sum over the survivors cannot see.
  /// </remarks>
  public Rate SystemContextSwitchesPerSecond { get; private set; } = Rate.NotSampledYet;

  /// <summary>The machine's kernel and user time, the same split as the per-core figures.</summary>
  public Rate SystemKernelPercent { get; private set; } = Rate.NotSampledYet;

  public Rate SystemUserPercent { get; private set; } = Rate.NotSampledYet;

  public int PerCoreCount { get; private set; }

  /// <summary>
  /// Recomputes everything from a pair of snapshots.
  /// </summary>
  /// <param name="previous">The older snapshot, or <see langword="null"/> for the very first sample.</param>
  /// <param name="current">The snapshot just taken.</param>
  /// <param name="mode">Which CPU-percent convention to express process CPU in.</param>
  public void Update(SystemSnapshot? previous, SystemSnapshot current, CpuPercentMode mode) {
    ArgumentNullException.ThrowIfNull(current);

    var count = current.ProcessCount;
    // Before anything is written: what this call is about to overwrite is what the last one worked
    // out, and that is the only record of the interval before this one.
    (this._cpuPercent, this._previousCpuPercent) = (this._previousCpuPercent, this._cpuPercent);
    EnsureLength(ref this._cpuPercent, count);
    EnsureLength(ref this._cpuPercentDelta, count);
    EnsureLength(ref this._cpuPercentPerCore, count);
    EnsureLength(ref this._readBytesPerSecond, count);
    EnsureLength(ref this._writeBytesPerSecond, count);
    EnsureLength(ref this._otherBytesPerSecond, count);
    EnsureLength(ref this._pageFaultsPerSecond, count);
    EnsureLength(ref this._contextSwitchesPerSecond, count);
    EnsureLength(ref this._cyclesPerSecond, count);
    EnsureLength(ref this._privateBytesDelta, count);
    EnsureLength(ref this._isNew, count);
    EnsureLength(ref this._memoryPercent, count);
    EnsureLength(ref this._gpuGraphicsPercent, count);
    EnsureLength(ref this._gpuComputePercent, count);
    EnsureLength(ref this._gpuCopyPercent, count);
    EnsureLength(ref this._gpuEncodePercent, count);
    EnsureLength(ref this._gpuDecodePercent, count);
    EnsureLength(ref this._gpuEnginePercent, count);
    EnsureLength(ref this._gpuDedicatedBytesDelta, count);
    EnsureLength(ref this._gpuEngine, count);
    FillMemoryPercent(this._memoryPercent, current);

    this._exited.Clear();
    this.StartedCount = 0;
    this.HasPrevious = previous is not null;

    if (previous is null) {
      this.UpdateDevices(null, current, double.NaN);
      this.ElapsedNanoseconds = double.NaN;
      this.SystemCpuPercent = Rate.NotSampledYet;
      this.SystemKernelPercent = Rate.NotSampledYet;
      this.SystemUserPercent = Rate.NotSampledYet;
      this.SystemContextSwitchesPerSecond = Rate.NotSampledYet;
      this.PerCoreCount = 0;
      var processes = current.Processes;
      for (var i = 0; i < processes.Length; ++i) {
        this._cpuPercent[i] = Rate.NotSampledYet;
        this._cpuPercentDelta[i] = Rate.NotSampledYet;
        this._cpuPercentPerCore[i] = Rate.NotSampledYet;
        this._readBytesPerSecond[i] = Rate.NotSampledYet;
        this._writeBytesPerSecond[i] = Rate.NotSampledYet;
        this._otherBytesPerSecond[i] = Rate.NotSampledYet;
        this._pageFaultsPerSecond[i] = Rate.NotSampledYet;
        this._contextSwitchesPerSecond[i] = Rate.NotSampledYet;
        this._cyclesPerSecond[i] = Rate.NotSampledYet;
        this._privateBytesDelta[i] = Rate.NotSampledYet;
        // Not skipped with the rest: half the GPU readings are percentages the driver sampled for
        // itself, which are as true on the first sample as on the hundredth. Blanking them would
        // hide a process at full utilisation for a whole interval for no reason.
        this.FillGpu(i, in processes[i], in processes[i], hasPrevious: false, double.NaN);
        // Nothing is "new" against no previous sample; everything would flash green on start-up.
        this._isNew[i] = false;
      }

      this._previousCpuPercentCount = processes.Length;
      return;
    }

    this.ElapsedNanoseconds = RateCalculator.ElapsedNanoseconds(previous.TimestampTicks, current.TimestampTicks);
    var elapsed = this.ElapsedNanoseconds;
    var cores = current.System.CoreCount > 0 ? current.System.CoreCount : 1;

    this._previousIndex.Clear();
    var previousProcesses = previous.Processes;
    for (var i = 0; i < previousProcesses.Length; ++i)
      this._previousIndex[previousProcesses[i].Key] = i;

    var currentProcesses = current.Processes;
    for (var i = 0; i < currentProcesses.Length; ++i) {
      ref readonly var process = ref currentProcesses[i];
      if (!this._previousIndex.Remove(process.Key, out var previousPosition)) {
        this._cpuPercent[i] = Rate.NotSampledYet;
        this._cpuPercentDelta[i] = Rate.NotSampledYet;
        this._cpuPercentPerCore[i] = Rate.NotSampledYet;
        this._readBytesPerSecond[i] = Rate.NotSampledYet;
        this._writeBytesPerSecond[i] = Rate.NotSampledYet;
        this._otherBytesPerSecond[i] = Rate.NotSampledYet;
        this._pageFaultsPerSecond[i] = Rate.NotSampledYet;
        this._contextSwitchesPerSecond[i] = Rate.NotSampledYet;
        this._cyclesPerSecond[i] = Rate.NotSampledYet;
        this._privateBytesDelta[i] = Rate.NotSampledYet;
        this.FillGpu(i, in process, in process, hasPrevious: false, elapsed);
        this._isNew[i] = true;
        ++this.StartedCount;
        continue;
      }

      ref readonly var before = ref previousProcesses[previousPosition];
      this._cpuPercent[i] = RateCalculator.CpuPercent(before.CpuTimeNs, process.CpuTimeNs, elapsed, cores, mode);
      this._cpuPercentDelta[i] = this.CpuPercentChange(previousPosition, this._cpuPercent[i]);
      this._cpuPercentPerCore[i] = RateCalculator.CpuPercent(
        before.CpuTimeNs, process.CpuTimeNs, elapsed, cores, CpuPercentMode.PerCore
      );

      this._readBytesPerSecond[i] = RateCalculator.PerSecond(before.ReadBytes, process.ReadBytes, elapsed);
      this._writeBytesPerSecond[i] = RateCalculator.PerSecond(before.WriteBytes, process.WriteBytes, elapsed);
      this._otherBytesPerSecond[i] = RateCalculator.PerSecond(before.OtherBytes, process.OtherBytes, elapsed);
      this._pageFaultsPerSecond[i] = RateCalculator.PerSecond(before.PageFaults, process.PageFaults, elapsed);
      this._contextSwitchesPerSecond[i] = RateCalculator.PerSecond(before.ContextSwitches, process.ContextSwitches, elapsed);
      this._cyclesPerSecond[i] = RateCalculator.PerSecond(before.Cycles, process.Cycles, elapsed);
      this._privateBytesDelta[i] = RateCalculator.SignedPerSecond(before.PrivateBytes, process.PrivateBytes, elapsed);
      this.FillGpu(i, in process, in before, hasPrevious: true, elapsed);
      this._isNew[i] = false;
    }

    this._previousCpuPercentCount = currentProcesses.Length;

    // Whatever is left in the map was in the previous snapshot and is not in this one. Removing the
    // matches above rather than doing a second pass is what keeps this O(n) instead of O(n log n),
    // and it means "exited" needs no second dictionary.
    foreach (var (key, _) in this._previousIndex)
      this._exited.Add(key);

    this.UpdateDevices(previous, current, elapsed);
    this.SystemCpuPercent = RateCalculator.BusyPercent(previous.System.Cpu, current.System.Cpu);
    this.SystemKernelPercent = RateCalculator.KernelPercent(previous.System.Cpu, current.System.Cpu);
    this.SystemUserPercent = RateCalculator.UserPercent(previous.System.Cpu, current.System.Cpu);
    this.SystemContextSwitchesPerSecond =
      RateCalculator.PerSecond(previous.System.ContextSwitches, current.System.ContextSwitches, elapsed);

    var coreCount = Math.Min(previous.PerCoreCount, current.PerCoreCount);
    EnsureLength(ref this._perCoreBusy, coreCount);
    EnsureLength(ref this._perCoreKernel, coreCount);
    EnsureLength(ref this._perCoreUser, coreCount);
    this.PerCoreCount = coreCount;
    var previousCores = previous.PerCore;
    var currentCores = current.PerCore;
    for (var i = 0; i < coreCount; ++i) {
      this._perCoreBusy[i] = RateCalculator.BusyPercent(previousCores[i], currentCores[i]);
      this._perCoreKernel[i] = RateCalculator.KernelPercent(previousCores[i], currentCores[i]);
      this._perCoreUser[i] = RateCalculator.UserPercent(previousCores[i], currentCores[i]);
    }
  }

  /// <summary>
  /// This interval's share against the one the same process had in the interval before it.
  /// </summary>
  /// <remarks>
  /// Unknown wherever either half is: a process one interval old has a share and nothing to compare
  /// it with, and subtracting from a share nobody could compute would produce a change that looks
  /// like the whole of this interval's use.
  /// </remarks>
  private Rate CpuPercentChange(int previousPosition, Rate now) {
    if (!now.HasValue)
      return now;

    if ((uint)previousPosition >= (uint)this._previousCpuPercentCount)
      return Rate.NotSampledYet;

    var before = this._previousCpuPercent[previousPosition];
    return before.HasValue ? Rate.Of(now.Value - before.Value) : Rate.NotSampledYet;
  }

  #region devices

  private readonly Dictionary<string, DiskCounters> _previousDisks = new(StringComparer.Ordinal);
  private readonly Dictionary<string, NetworkCounters> _previousNetworks = new(StringComparer.Ordinal);
  private readonly Dictionary<string, DiskRates> _diskRates = new(StringComparer.Ordinal);
  private readonly Dictionary<string, NetworkRates> _networkRates = new(StringComparer.Ordinal);

  /// <param name="BusyPercent">
  /// Share of the interval the device had at least one request in flight — what Task Manager calls
  /// active time. Saturates at 100 and says nothing about queue depth.
  /// </param>
  public readonly record struct DiskRates(
    Rate ReadBytesPerSecond,
    Rate WriteBytesPerSecond,
    Rate ReadOperationsPerSecond,
    Rate WriteOperationsPerSecond,
    Rate BusyPercent
  );

  public readonly record struct NetworkRates(
    Rate ReceivedBytesPerSecond,
    Rate SentBytesPerSecond,
    Rate ReceivedPacketsPerSecond,
    Rate SentPacketsPerSecond
  );

  // Not default(DiskRates): a default Rate carries UnknownReason.None, which means "the value is
  // present and it is zero". A device plugged in a moment ago would report a confident zero for
  // every rate it has (PRD §72.3).
  private static readonly DiskRates _NoDiskRates = new(
    Rate.NotSampledYet, Rate.NotSampledYet, Rate.NotSampledYet, Rate.NotSampledYet, Rate.NotSampledYet
  );

  private static readonly NetworkRates _NoNetworkRates = new(
    Rate.NotSampledYet, Rate.NotSampledYet, Rate.NotSampledYet, Rate.NotSampledYet
  );

  /// <summary>Rates for one disk, by name. Unknown for a device that has only just appeared.</summary>
  public DiskRates DiskRatesOf(string name)
    => this._diskRates.TryGetValue(name, out var rates) ? rates : _NoDiskRates;

  public NetworkRates NetworkRatesOf(string name)
    => this._networkRates.TryGetValue(name, out var rates) ? rates : _NoNetworkRates;

  /// <summary>
  /// Matches devices between the two snapshots by name.
  /// </summary>
  /// <remarks>
  /// By name rather than by position: a USB disk appearing or an interface going away renumbers
  /// everything after it, and matching by index would attribute one device's traffic to another —
  /// the same reason processes are matched by identity rather than by their place in the array.
  /// </remarks>
  private void UpdateDevices(SystemSnapshot? previous, SystemSnapshot current, double elapsed) {
    this._diskRates.Clear();
    this._networkRates.Clear();

    this._previousDisks.Clear();
    if (previous is not null)
      foreach (var disk in previous.Disks)
        this._previousDisks[disk.Name] = disk;

    foreach (var disk in current.Disks) {
      if (!this._previousDisks.TryGetValue(disk.Name, out var before))
        continue;

      this._diskRates[disk.Name] = new(
        RateCalculator.PerSecond(before.ReadBytes, disk.ReadBytes, elapsed),
        RateCalculator.PerSecond(before.WriteBytes, disk.WriteBytes, elapsed),
        RateCalculator.PerSecond(before.ReadOperations, disk.ReadOperations, elapsed),
        RateCalculator.PerSecond(before.WriteOperations, disk.WriteOperations, elapsed),
        BusyPercent(before.BusyMilliseconds, disk.BusyMilliseconds, elapsed)
      );
    }

    this._previousNetworks.Clear();
    if (previous is not null)
      foreach (var network in previous.Networks)
        this._previousNetworks[network.Name] = network;

    foreach (var network in current.Networks) {
      if (!this._previousNetworks.TryGetValue(network.Name, out var before))
        continue;

      this._networkRates[network.Name] = new(
        RateCalculator.PerSecond(before.ReceivedBytes, network.ReceivedBytes, elapsed),
        RateCalculator.PerSecond(before.SentBytes, network.SentBytes, elapsed),
        RateCalculator.PerSecond(before.ReceivedPackets, network.ReceivedPackets, elapsed),
        RateCalculator.PerSecond(before.SentPackets, network.SentPackets, elapsed)
      );
    }
  }

  /// <summary>
  /// Busy milliseconds against wall-clock nanoseconds, as a percentage.
  /// </summary>
  /// <remarks>
  /// Clamped at 100 unlike the CPU figure, and for a different reason: a device cannot be busy for
  /// more than the interval, so anything above it is the counter and the clock disagreeing by a
  /// millisecond or two rather than something worth seeing.
  /// </remarks>
  private static Rate BusyPercent(Counter before, Counter now, double elapsedNanoseconds) {
    var perSecond = RateCalculator.PerSecond(before, now, elapsedNanoseconds);
    if (!perSecond.HasValue)
      return perSecond;

    // The counter is in milliseconds, so a device busy the whole time gains 1000 of them a second.
    return Rate.Of(Math.Min(100, perSecond.Value / 10));
  }

  #endregion

  /// <summary>
  /// Each process's resident memory against the machine's total.
  /// </summary>
  /// <remarks>
  /// Needs no previous sample, so it is filled on the first one too — unlike every rate beside it.
  /// A machine that will not say how much memory it has leaves this unknown rather than reporting
  /// every process at nought percent (PRD §5.3).
  /// </remarks>
  private static void FillMemoryPercent(Rate[] destination, SystemSnapshot current) {
    var processes = current.Processes;
    var total = current.System.TotalMemoryBytes;
    if (!total.HasValue || total.Value == 0) {
      for (var i = 0; i < processes.Length; ++i)
        destination[i] = Rate.Unknown(total.HasValue ? UnknownReason.CounterInvalid : total.Reason);

      return;
    }

    for (var i = 0; i < processes.Length; ++i)
      destination[i] = processes[i].WorkingSetBytes.HasValue
        ? Rate.Of(processes[i].WorkingSetBytes.Value * 100d / total.Value)
        : Rate.Unknown(processes[i].WorkingSetBytes.Reason);
  }

  #region graphics

  /// <summary>
  /// One process's use of its graphics adapter, from whichever of the two shapes its driver has.
  /// </summary>
  /// <remarks>
  /// The kernel's own accounting is a counter per engine and is differenced here, exactly like CPU
  /// time. NVIDIA's is a percentage the driver sampled for itself and needs no previous sample at
  /// all, which is why <paramref name="hasPrevious"/> silences one and not the other. Where a driver
  /// offers both for the same engine the counter wins: it covers precisely the interval between the
  /// two samples, where the sampled figure covers whatever window the driver chose.
  /// </remarks>
  private void FillGpu(int index, in ProcessRecord process, in ProcessRecord before, bool hasPrevious, double elapsed) {
    var graphics = Merge(
      EnginePercent(before.GpuGraphicsNs, process.GpuGraphicsNs, elapsed, hasPrevious),
      Sampled(process.GpuBusyPercent, process.GpuBusyEngine, GpuEngine.Graphics)
    );
    var compute = Merge(
      EnginePercent(before.GpuComputeNs, process.GpuComputeNs, elapsed, hasPrevious),
      Sampled(process.GpuBusyPercent, process.GpuBusyEngine, GpuEngine.Compute)
    );
    var copy = EnginePercent(before.GpuCopyNs, process.GpuCopyNs, elapsed, hasPrevious);
    var encode = Merge(
      EnginePercent(before.GpuEncodeNs, process.GpuEncodeNs, elapsed, hasPrevious),
      Instant(process.GpuEncodePercent)
    );
    var decode = Merge(
      EnginePercent(before.GpuDecodeNs, process.GpuDecodeNs, elapsed, hasPrevious),
      Instant(process.GpuDecodePercent)
    );

    this._gpuGraphicsPercent[index] = graphics;
    this._gpuComputePercent[index] = compute;
    this._gpuCopyPercent[index] = copy;
    this._gpuEncodePercent[index] = encode;
    this._gpuDecodePercent[index] = decode;

    var best = Rate.Unknown(UnknownReason.NotImplementedHere);
    var engine = GpuEngine.Unknown;
    Consider(ref best, ref engine, graphics, GpuEngine.Graphics);
    Consider(ref best, ref engine, compute, GpuEngine.Compute);
    Consider(ref best, ref engine, copy, GpuEngine.Copy);
    Consider(ref best, ref engine, encode, GpuEngine.Encode);
    Consider(ref best, ref engine, decode, GpuEngine.Decode);
    this._gpuEnginePercent[index] = best;
    this._gpuEngine[index] = engine;

    this._gpuDedicatedBytesDelta[index] = hasPrevious
      ? RateCalculator.SignedPerSecond(before.GpuDedicatedBytes, process.GpuDedicatedBytes, elapsed)
      : Rate.NotSampledYet;
  }

  /// <summary>
  /// Busy nanoseconds against the interval, as a percentage.
  /// </summary>
  /// <remarks>
  /// Clamped at 100 the way the disks' active time is, and for the same reason: an engine cannot be
  /// busy for longer than the interval, so anything above it is the driver's counter and the
  /// monotonic clock disagreeing. A part with two of an engine behind one name — i915 reports the
  /// second through <c>drm-engine-capacity-video</c> — can genuinely reach twice the interval, and
  /// showing that as 200 % would be read as a bug rather than as a saturated pair.
  /// </remarks>
  private static Rate EnginePercent(Counter before, Counter current, double elapsedNanoseconds, bool hasPrevious) {
    if (!current.HasValue)
      return Rate.Unknown(current.Reason);
    if (!hasPrevious)
      return Rate.NotSampledYet;

    var perSecond = RateCalculator.PerSecond(before, current, elapsedNanoseconds);
    if (!perSecond.HasValue)
      return perSecond;

    // Nanoseconds of work per second of wall clock: a billion of them is one engine, all the time.
    return Rate.Of(Math.Min(100, perSecond.Value / 10_000_000));
  }

  /// <summary>A percentage the driver sampled, as a rate needing no interval of ours.</summary>
  private static Rate Instant(Counter percent) => percent.HasValue ? Rate.Of(percent.Value) : Rate.Unknown(percent.Reason);

  /// <summary>
  /// The sampled figure, but only for the engine the driver attributed it to.
  /// </summary>
  /// <remarks>
  /// NVML publishes one number covering graphics and compute together and says which of its two
  /// lists the process came from. Showing that number in both columns would claim a compute client
  /// is also drawing; showing it in neither would throw away the only reading there is.
  /// </remarks>
  private static Rate Sampled(Counter percent, GpuEngine attributed, GpuEngine wanted)
    => attributed == wanted ? Instant(percent) : Rate.Unknown(UnknownReason.NotImplementedHere);

  /// <summary>
  /// The counter-derived figure where there is one, and the driver's own sample otherwise.
  /// </summary>
  /// <remarks>
  /// The last line is not a detail. Where neither half has a reading the reason is the whole of what
  /// the reader gets, and "this driver publishes no such counter" is the weaker answer whenever the
  /// other half is merely waiting for its first sample: without it, every NVIDIA process read as
  /// "not implemented here" for the one interval before NVML's own sampler had published anything,
  /// which says the program cannot do something it does perfectly well.
  /// </remarks>
  private static Rate Merge(Rate derived, Rate sampled) {
    if (derived.HasValue)
      return derived;
    if (sampled.HasValue)
      return sampled;

    return derived.Reason == UnknownReason.NotImplementedHere ? sampled : derived;
  }

  /// <summary>
  /// Keeps the busiest engine seen so far, and its name.
  /// </summary>
  /// <remarks>
  /// A reason is kept only until a real reading turns up, so a process whose adapter reports four
  /// engines and refuses the fifth is described by the four rather than by the refusal.
  /// </remarks>
  private static void Consider(ref Rate best, ref GpuEngine engine, Rate candidate, GpuEngine name) {
    if (!candidate.HasValue) {
      if (!best.HasValue && best.Reason == UnknownReason.NotImplementedHere)
        best = candidate;

      return;
    }

    if (best.HasValue && best.Value >= candidate.Value)
      return;

    best = candidate;
    // A process using none of the adapter is on no engine. Naming the first one that reported a
    // nought would put "3D" against every kernel thread on the machine.
    engine = candidate.Value > 0 ? name : GpuEngine.Unknown;
  }

  #endregion

  private static void EnsureLength<T>(ref T[] array, int length) {
    if (array.Length < length)
      array = new T[Math.Max(length, array.Length * 2)];
  }

}
