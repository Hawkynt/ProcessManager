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
  Counter FanPercent
);
