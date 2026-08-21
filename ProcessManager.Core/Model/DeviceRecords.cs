namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// One storage device as one sample saw it — absolute counters only, like everything else a probe
/// returns (PRD §48).
/// </summary>
/// <remarks>
/// Whole devices, not partitions: <c>nvme0n1</c> rather than <c>nvme0n1p1</c>. Summing partitions
/// double-counts, because the kernel charges the same I/O to both.
/// </remarks>
public struct DiskCounters {

  public string Name;

  /// <summary>Completed reads and writes, as counts of operations rather than bytes.</summary>
  public Counter ReadOperations;
  public Counter WriteOperations;

  public Counter ReadBytes;
  public Counter WriteBytes;

  /// <summary>
  /// Milliseconds the device spent with at least one request in flight.
  /// </summary>
  /// <remarks>
  /// What "active time" means: its growth over an interval, against the interval, is the percentage
  /// Task Manager shows. It saturates at 100 % and says nothing about how deep the queue got.
  /// </remarks>
  public Counter BusyMilliseconds;

  /// <summary>
  /// Milliseconds spent waiting, summed over every request of that direction (PRD §48).
  /// </summary>
  /// <remarks>
  /// Wall-clock time per request, from when it was queued to when it completed — so a device with a
  /// deep queue accumulates far more of this than the interval itself contains. Against the count of
  /// requests it is the average response time, which is the figure that says whether a busy disk is
  /// keeping up or falling behind. <c>iostat</c>'s <c>r_await</c> and <c>w_await</c> are this
  /// arithmetic.
  /// </remarks>
  public Counter ReadWaitMilliseconds;

  public Counter WriteWaitMilliseconds;

  /// <summary>
  /// The time-weighted queue depth, in millisecond-requests.
  /// </summary>
  /// <remarks>
  /// The kernel adds the in-flight count to this on every millisecond of activity, so its growth
  /// over an interval divided by that interval is the average number of requests outstanding — a
  /// disk at 100 % active time with a queue of one is saturated by one client, and the same disk
  /// with a queue of thirty-two is being asked for far more than it can do.
  /// </remarks>
  public Counter WeightedQueueMilliseconds;

  /// <summary>Requests outstanding at the instant of the sample, which is an instantaneous depth.</summary>
  public Counter QueuedRequests;

}

/// <summary>What a storage device is, as opposed to what it is doing. Read once (PRD §48).</summary>
/// <param name="Rotational">
/// <see langword="true"/> for spinning rust, <see langword="false"/> for solid state, and
/// <see langword="null"/> where the kernel does not say — which is not the same as "no".
/// </param>
/// <param name="Serial">
/// The device's own serial number, where the driver publishes one. Null where it does not — a
/// virtual disk, a device-mapper target, an SD card — which is not the same as a blank serial.
/// </param>
/// <param name="Bus">
/// What the device is attached by, in the kernel's own word for it: <c>nvme</c>, <c>scsi</c>,
/// <c>virtio</c>, <c>mmc</c>. Deliberately the subsystem rather than a prettier name: "SATA" and
/// "USB" are both <c>scsi</c> from here, and inventing the distinction would be a guess.
/// </param>
/// <param name="Volumes">
/// Where this disk is mounted, one entry per mount point, through whatever stack of partitions,
/// device-mapper targets and RAID sets is between them. Null when the mount table could not be read
/// at all, empty when it could and nothing on this disk is mounted — two different statements
/// (PRD §5.3).
/// </param>
/// <param name="IsSystemDisk">Whether the root file system lives on it. Null when nobody could tell.</param>
/// <param name="HoldsSwap">Whether a swap area lives on it, as a partition or as a file.</param>
public sealed record DiskInfo(
  string Name,
  string? Model,
  bool? Rotational,
  Counter CapacityBytes,
  string? Serial = null,
  string? Bus = null,
  IReadOnlyList<string>? Volumes = null,
  bool? IsSystemDisk = null,
  bool? HoldsSwap = null
);

/// <summary>One network interface as one sample saw it (PRD §49).</summary>
public struct NetworkCounters {

  public string Name;

  public Counter ReceivedBytes;
  public Counter SentBytes;
  public Counter ReceivedPackets;
  public Counter SentPackets;

  /// <summary>Errors and drops, which are the two different ways a packet fails to arrive.</summary>
  public Counter ReceiveErrors;
  public Counter SendErrors;
  public Counter ReceiveDropped;
  public Counter SendDropped;

}

/// <summary>What a network interface is. Read once (PRD §49).</summary>
/// <param name="LinkSpeedBitsPerSecond">
/// The negotiated speed. Unknown on anything not up, and on virtual interfaces that have no wire —
/// reported as unknown rather than as zero, which would read as a dead link.
/// </param>
/// <param name="State">operstate: up, down, unknown, dormant.</param>
/// <param name="Index">
/// The kernel's <c>ifindex</c> — what the routing table, netlink and every packet capture identify
/// this interface by, and the only name for it that does not change when udev renames the device.
/// </param>
/// <param name="Kind">
/// What sort of interface it is: <c>ethernet</c>, <c>wireless</c>, <c>loopback</c>, <c>bridge</c>,
/// <c>tunnel</c>, <c>virtual</c>. Worked out from what the kernel publishes about it rather than
/// from its name, because a name is a convention and a machine with two of anything breaks it.
/// </param>
/// <param name="Driver">The module driving it — <c>iwlwifi</c>, <c>e1000e</c> — or null for a
/// virtual interface, which has none.</param>
/// <param name="Addresses">
/// Every address it carries, as <c>address/prefix</c>. Empty means it is up and has none, which is a
/// real state; null means nobody could ask (PRD §5.3).
/// </param>
/// <param name="Gateway">The default route through this interface, where it has one.</param>
/// <param name="DnsServers">
/// Who the machine asks about names. A machine-wide fact rather than a per-interface one — the
/// resolver has one list however many adapters are up — and carried here because this is the page
/// where somebody looks for it.
/// </param>
/// <param name="Ssid">The network a wireless adapter is associated with, or null for anything else.</param>
/// <param name="SignalDbm">
/// How well it hears that network, in dBm: about −40 is excellent and −85 is unusable. Negative by
/// nature, which is why it is not a <see cref="Counter"/>.
/// </param>
/// <param name="FrequencyMegahertz">What it is tuned to, from which the channel and band follow.</param>
public sealed record NetworkInterfaceInfo(
  string Name,
  string? MacAddress,
  Counter LinkSpeedBitsPerSecond,
  string? State,
  Counter MaximumTransmissionUnit,
  bool IsLoopback,
  int? Index = null,
  string? Kind = null,
  string? Driver = null,
  IReadOnlyList<string>? Addresses = null,
  string? Gateway = null,
  IReadOnlyList<string>? DnsServers = null,
  string? Ssid = null,
  int? SignalDbm = null,
  int? FrequencyMegahertz = null
) {

  /// <summary>Whether this is a wireless adapter, which is what gives it four rows nothing else has.</summary>
  public bool IsWireless => this.Kind == "wireless";

}

/// <summary>
/// One graphics adapter, as one reading saw it (PRD §50).
/// </summary>
/// <remarks>
/// <para>
/// Identity and readings in one record rather than the disk's split between a cached description and
/// sampled counters, because a GPU's readings come from the same handful of files as its name and
/// are read on demand by the page that shows them — never from the sample loop, whose allocation
/// budget is a build gate (PRD §5.4).
/// </para>
/// <para>
/// Not one of these has a default value, deliberately. <c>default(Counter)</c> is a <em>confident
/// zero</em> — a reading of nought that was never taken — so a caller allowed to leave a field out
/// would silently claim a card draws no power and has a nought-watt ceiling. Every reading has to be
/// stated, even if what it states is that nobody knows (PRD §5.3).
/// </para>
/// <para>
/// Every reading is a <see cref="Counter"/>, because which ones exist depends on the vendor and on
/// what is installed. <c>sysfs</c> alone answers almost nothing: it is the vendors' own libraries
/// that have the numbers, which is why every tool that shows a real GPU page loads one. Where a
/// reading cannot be had it says so rather than reading zero (PRD §5.3).
/// </para>
/// </remarks>
/// <param name="PowerLimitMicrowatts">
/// The most the card can ever draw, which is the number that makes <paramref name="PowerMicrowatts"/>
/// mean something: 30 W tells you nothing until you know whether the ceiling is 40 or 400.
/// </param>
/// <param name="PowerCapMicrowatts">
/// What it is allowed to draw <em>at this moment</em>, where that is lower — a laptop's dynamic
/// boost moves this around constantly, and a card capped to 20 W of a possible 130 W is the whole
/// explanation for a 210 MHz clock at full utilisation. Kept apart from the limit because the
/// instantaneous draw can exceed it, the cap being an average and not a fence.
/// </param>
/// <param name="MemoryBusyPercent">
/// How much of the interval the memory bus was being read or written, which is a different question
/// from how full the memory is and often the one that explains a stall.
/// </param>
/// <param name="FanPercent">
/// How fast the fan is turning as a share of what it can do. Not the same reading as
/// <paramref name="FanRpm"/> and not derivable from it: the maximum a card's fan can turn at is not
/// published anywhere, so revolutions cannot be turned into a percentage (PRD §5.3).
/// </param>
/// <param name="FanRpm">
/// Revolutions a minute, where there is a tachometer to read. hwmon publishes them and NVML does
/// not, which is why both readings exist rather than one being computed from the other.
/// </param>
/// <param name="FanCount">
/// How many fans the card has. Nought is a real answer — a laptop card whose cooling belongs to the
/// chassis has none of its own — and is why an unreadable fan speed on such a card is the truth
/// rather than a failure.
/// </param>
/// <param name="EncodePercent">
/// The video engines, which are the two of §50's five that a driver will name separately. The
/// shaders' figure in <paramref name="BusyPercent"/> already has graphics and compute summed and no
/// interface splits them, so those two and the copy engines are unread rather than invented.
/// </param>
public sealed record GpuInfo(
  string Name,
  string? Model,
  string? Driver,
  Counter BusyPercent,
  Counter MemoryUsedBytes,
  Counter MemoryTotalBytes,
  Counter TemperatureMilliCelsius,
  Counter PowerMicrowatts,
  string? PowerState,
  Counter PowerLimitMicrowatts,
  Counter PowerCapMicrowatts,
  Counter MemoryBusyPercent,
  Counter CoreClockHertz,
  Counter MemoryClockHertz,
  Counter FanPercent,
  Counter FanRpm,
  Counter FanCount,
  Counter EncodePercent,
  Counter DecodePercent
);
