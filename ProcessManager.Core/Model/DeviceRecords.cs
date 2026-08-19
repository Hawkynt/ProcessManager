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

}

/// <summary>What a storage device is, as opposed to what it is doing. Read once (PRD §48).</summary>
/// <param name="Rotational">
/// <see langword="true"/> for spinning rust, <see langword="false"/> for solid state, and
/// <see langword="null"/> where the kernel does not say — which is not the same as "no".
/// </param>
public sealed record DiskInfo(
  string Name,
  string? Model,
  bool? Rotational,
  Counter CapacityBytes
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
public sealed record NetworkInterfaceInfo(
  string Name,
  string? MacAddress,
  Counter LinkSpeedBitsPerSecond,
  string? State,
  Counter MaximumTransmissionUnit,
  bool IsLoopback
);

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
/// Every reading is a <see cref="Counter"/> and most of them are unknown on most machines, which is
/// the honest state of the world rather than a gap to be filled in later. AMD's driver publishes
/// utilisation and VRAM through <c>sysfs</c>; Intel's exposes engine busyness only through a perf
/// counter that needs a privileged open; NVIDIA's proprietary driver exposes nothing there at all
/// and wants NVML. A page that showed 0 % for two of those three would be lying (PRD §5.3).
/// </para>
/// </remarks>
public sealed record GpuInfo(
  string Name,
  string? Model,
  string? Driver,
  Counter BusyPercent,
  Counter MemoryUsedBytes,
  Counter MemoryTotalBytes,
  Counter TemperatureMilliCelsius,
  Counter PowerMicrowatts,
  string? PowerState
);
