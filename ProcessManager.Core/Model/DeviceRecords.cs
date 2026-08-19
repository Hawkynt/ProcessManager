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
