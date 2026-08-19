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
  private Rate[] _readBytesPerSecond = [];
  private Rate[] _writeBytesPerSecond = [];
  private Rate[] _otherBytesPerSecond = [];
  private Rate[] _pageFaultsPerSecond = [];
  private Rate[] _contextSwitchesPerSecond = [];
  private Rate[] _cyclesPerSecond = [];
  private Rate[] _privateBytesDelta = [];
  private bool[] _isNew = [];
  private Rate[] _perCoreBusy = [];

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

  /// <summary>Busy percentage of one logical core.</summary>
  public Rate PerCoreBusyPercent(int core)
    => (uint)core < (uint)this._perCoreBusy.Length ? this._perCoreBusy[core] : Rate.NotSampledYet;

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
    EnsureLength(ref this._cpuPercent, count);
    EnsureLength(ref this._cpuPercentPerCore, count);
    EnsureLength(ref this._readBytesPerSecond, count);
    EnsureLength(ref this._writeBytesPerSecond, count);
    EnsureLength(ref this._otherBytesPerSecond, count);
    EnsureLength(ref this._pageFaultsPerSecond, count);
    EnsureLength(ref this._contextSwitchesPerSecond, count);
    EnsureLength(ref this._cyclesPerSecond, count);
    EnsureLength(ref this._privateBytesDelta, count);
    EnsureLength(ref this._isNew, count);

    this._exited.Clear();
    this.StartedCount = 0;
    this.HasPrevious = previous is not null;

    if (previous is null) {
      this.UpdateDevices(null, current, double.NaN);
      this.ElapsedNanoseconds = double.NaN;
      this.SystemCpuPercent = Rate.NotSampledYet;
      this.PerCoreCount = 0;
      var processes = current.Processes;
      for (var i = 0; i < processes.Length; ++i) {
        this._cpuPercent[i] = Rate.NotSampledYet;
        this._cpuPercentPerCore[i] = Rate.NotSampledYet;
        this._readBytesPerSecond[i] = Rate.NotSampledYet;
        this._writeBytesPerSecond[i] = Rate.NotSampledYet;
        this._otherBytesPerSecond[i] = Rate.NotSampledYet;
        this._pageFaultsPerSecond[i] = Rate.NotSampledYet;
        this._contextSwitchesPerSecond[i] = Rate.NotSampledYet;
        this._cyclesPerSecond[i] = Rate.NotSampledYet;
        this._privateBytesDelta[i] = Rate.NotSampledYet;
        // Nothing is "new" against no previous sample; everything would flash green on start-up.
        this._isNew[i] = false;
      }

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
        this._cpuPercentPerCore[i] = Rate.NotSampledYet;
        this._readBytesPerSecond[i] = Rate.NotSampledYet;
        this._writeBytesPerSecond[i] = Rate.NotSampledYet;
        this._otherBytesPerSecond[i] = Rate.NotSampledYet;
        this._pageFaultsPerSecond[i] = Rate.NotSampledYet;
        this._contextSwitchesPerSecond[i] = Rate.NotSampledYet;
        this._cyclesPerSecond[i] = Rate.NotSampledYet;
        this._privateBytesDelta[i] = Rate.NotSampledYet;
        this._isNew[i] = true;
        ++this.StartedCount;
        continue;
      }

      ref readonly var before = ref previousProcesses[previousPosition];
      this._cpuPercent[i] = RateCalculator.CpuPercent(before.CpuTimeNs, process.CpuTimeNs, elapsed, cores, mode);
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
      this._isNew[i] = false;
    }

    // Whatever is left in the map was in the previous snapshot and is not in this one. Removing the
    // matches above rather than doing a second pass is what keeps this O(n) instead of O(n log n),
    // and it means "exited" needs no second dictionary.
    foreach (var (key, _) in this._previousIndex)
      this._exited.Add(key);

    this.UpdateDevices(previous, current, elapsed);
    this.SystemCpuPercent = RateCalculator.BusyPercent(previous.System.Cpu, current.System.Cpu);

    var coreCount = Math.Min(previous.PerCoreCount, current.PerCoreCount);
    EnsureLength(ref this._perCoreBusy, coreCount);
    this.PerCoreCount = coreCount;
    var previousCores = previous.PerCore;
    var currentCores = current.PerCore;
    for (var i = 0; i < coreCount; ++i)
      this._perCoreBusy[i] = RateCalculator.BusyPercent(previousCores[i], currentCores[i]);
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

  private static void EnsureLength<T>(ref T[] array, int length) {
    if (array.Length < length)
      array = new T[Math.Max(length, array.Length * 2)];
  }

}
