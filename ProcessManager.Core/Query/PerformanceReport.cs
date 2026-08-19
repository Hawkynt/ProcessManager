using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>One labelled figure on the performance page.</summary>
/// <param name="Label">What it is called.</param>
/// <param name="Value">What it says, already formatted and including the reason when there is none.</param>
public readonly record struct PerformanceRow(string Label, string Value);

/// <summary>A group of figures under one heading.</summary>
public readonly record struct PerformanceSection(string Title, IReadOnlyList<PerformanceRow> Rows);

/// <summary>
/// The performance page's content, as data (PRD §45, §46, §47, §96).
/// </summary>
/// <remarks>
/// Deliberately not a window. The same sections are rendered by <c>--host</c> on a terminal and by
/// the desktop's performance view, which means the two cannot disagree about what the machine is —
/// the same argument that put the process fields in one registry (§5.1). It also means the content
/// is unit-testable, which a window is not.
/// </remarks>
public static class PerformanceReport {

  /// <summary>
  /// Everything the page shows.
  /// </summary>
  /// <param name="delta">
  /// May be <see langword="null"/> before a second sample, in which case the rates say so rather
  /// than reading zero.
  /// </param>
  /// <param name="describeDisk">
  /// What a disk is, by name. Optional: without it the devices still appear with their rates, just
  /// without a model or a capacity.
  /// </param>
  public static IReadOnlyList<PerformanceSection> Build(
    HostInfo host,
    SystemSnapshot snapshot,
    SnapshotDelta? delta = null,
    Func<string, DiskInfo>? describeDisk = null,
    Func<string, NetworkInterfaceInfo>? describeInterface = null
  ) {
    ArgumentNullException.ThrowIfNull(host);
    ArgumentNullException.ThrowIfNull(snapshot);

    var sections = new List<PerformanceSection> {
      new("System", BuildSystem(host, snapshot)),
      new("Processor", BuildProcessor(host, snapshot, delta)),
      new("Memory", BuildMemory(host, snapshot)),
    };

    // One section per device, so a machine with three disks gets three headings rather than one
    // heading with everything in it — which is the rail §45 asks for, in the shape a page can hold.
    foreach (var disk in snapshot.Disks)
      sections.Add(new($"Disk — {disk.Name}", BuildDisk(in disk, delta, describeDisk)));

    foreach (var network in snapshot.Networks) {
      var info = describeInterface?.Invoke(network.Name);
      // Loopback carries real traffic and is not the network; listing it beside the adapters
      // reports a machine talking to itself as bandwidth.
      if (info is { IsLoopback: true })
        continue;

      sections.Add(new($"Network — {network.Name}", BuildNetwork(in network, delta, info)));
    }

    return sections;
  }

  private static PerformanceRow[] BuildDisk(
    in DiskCounters disk,
    SnapshotDelta? delta,
    Func<string, DiskInfo>? describe
  ) {
    var info = describe?.Invoke(disk.Name);
    var rates = delta?.DiskRatesOf(disk.Name);

    var rows = new List<PerformanceRow>();
    if (info?.Model is { Length: > 0 } model)
      rows.Add(new("Model", model));

    if (info is not null) {
      rows.Add(new("Capacity", Humanize.Bytes(info.CapacityBytes)));
      rows.Add(new("Media", info.Rotational switch {
        true => "rotational",
        false => "solid state",
        // The kernel not saying is not the same as "no", and a machine that reports neither should
        // not be described as a hard disk by default.
        null => Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
      }));
    }

    rows.Add(new("Active time", rates is { } active ? Percent(active.BusyPercent) : Pending));
    rows.Add(new("Read rate", rates is { } read ? Humanize.BytesPerSecond(read.ReadBytesPerSecond) : Pending));
    rows.Add(new("Write rate", rates is { } write ? Humanize.BytesPerSecond(write.WriteBytesPerSecond) : Pending));
    rows.Add(new("Read IOPS", rates is { } readOps ? Humanize.Rate(readOps.ReadOperationsPerSecond) : Pending));
    rows.Add(new("Write IOPS", rates is { } writeOps ? Humanize.Rate(writeOps.WriteOperationsPerSecond) : Pending));
    rows.Add(new("Total read", Humanize.Bytes(disk.ReadBytes)));
    rows.Add(new("Total written", Humanize.Bytes(disk.WriteBytes)));
    return [.. rows];
  }

  private static PerformanceRow[] BuildNetwork(
    in NetworkCounters network,
    SnapshotDelta? delta,
    NetworkInterfaceInfo? info
  ) {
    var rates = delta?.NetworkRatesOf(network.Name);
    var rows = new List<PerformanceRow>();

    if (info is not null) {
      rows.Add(new("State", info.State ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)));
      rows.Add(new("Link speed", Bits(info.LinkSpeedBitsPerSecond)));
      rows.Add(new("MAC address", info.MacAddress ?? Humanize.Placeholder(UnknownReason.NotPermitted)));
      rows.Add(new("MTU", Humanize.Count(info.MaximumTransmissionUnit)));
    }

    rows.Add(new("Receive rate", rates is { } received ? Humanize.BytesPerSecond(received.ReceivedBytesPerSecond) : Pending));
    rows.Add(new("Send rate", rates is { } sent ? Humanize.BytesPerSecond(sent.SentBytesPerSecond) : Pending));
    rows.Add(new("Received", Humanize.Bytes(network.ReceivedBytes)));
    rows.Add(new("Sent", Humanize.Bytes(network.SentBytes)));

    // Errors and drops are two different failures, and both are almost always zero — which is why
    // they are worth a row: a non-zero one is the whole reason somebody opened this page.
    rows.Add(new("Errors", $"{Humanize.Count(network.ReceiveErrors)} in, {Humanize.Count(network.SendErrors)} out"));
    rows.Add(new("Dropped", $"{Humanize.Count(network.ReceiveDropped)} in, {Humanize.Count(network.SendDropped)} out"));
    return [.. rows];
  }

  private static string Pending => Humanize.Placeholder(UnknownReason.NotSampledYet);

  private static string Bits(Counter counter) {
    if (!counter.HasValue)
      return Humanize.Placeholder(counter.Reason);

    var bits = (double)counter.Value;
    return bits >= 1_000_000_000
      ? (bits / 1_000_000_000).ToString("0.#", CultureInfo.InvariantCulture) + " Gb/s"
      : (bits / 1_000_000).ToString("0", CultureInfo.InvariantCulture) + " Mb/s";
  }

  private static PerformanceRow[] BuildSystem(HostInfo host, SystemSnapshot snapshot) {
    var rows = new List<PerformanceRow> {
      new("Host", host.HostName),
      new("OS", host.OperatingSystem),
      new("Kernel", host.OperatingSystemVersion),
      new("Architecture", host.Architecture),
      new("Uptime", Uptime(snapshot.System.UptimeSeconds)),
      new("Processes", snapshot.ProcessCount.ToString(CultureInfo.InvariantCulture)),
      new("Threads", snapshot.System.TotalThreads.ToString(CultureInfo.InvariantCulture)),
    };

    // Only when there is something to say: a physical machine should not carry a row reading "no".
    if (host.Virtualisation is { } virtualisation)
      rows.Add(new("Virtualised", virtualisation));

    return [.. rows];
  }

  private static PerformanceRow[] BuildProcessor(HostInfo host, SystemSnapshot snapshot, SnapshotDelta? delta) {
    var rows = new List<PerformanceRow> {
      new("Model", host.CpuModel ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)),
      new("Vendor", host.CpuVendor ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)),
      new("Utilisation", Percent(delta?.SystemCpuPercent)),
      new("Base speed", Hertz(host.CpuBaseHertz)),
      new("Current speed", Hertz(host.CpuCurrentHertz)),
      new("Sockets", Humanize.Count(host.Sockets)),
      new("Physical cores", Humanize.Count(host.PhysicalCores)),
      new("Logical processors", Humanize.Count(host.LogicalProcessors)),
      new("NUMA nodes", Humanize.Count(host.NumaNodes)),
      new("L1 data", Humanize.Bytes(host.L1DataBytes)),
      new("L1 instruction", Humanize.Bytes(host.L1InstructionBytes)),
      new("L2", Humanize.Bytes(host.L2Bytes)),
      new("L3", Humanize.Bytes(host.L3Bytes)),
    };

    // Load average is a Unix idea and reads as three numbers or not at all; a machine that does not
    // publish it gets no row rather than three zeros.
    if (snapshot.System.LoadAverage1 > 0 || snapshot.System.LoadAverage15 > 0)
      rows.Add(new(
        "Load average",
        string.Format(
          CultureInfo.InvariantCulture,
          "{0:0.00}  {1:0.00}  {2:0.00}",
          snapshot.System.LoadAverage1,
          snapshot.System.LoadAverage5,
          snapshot.System.LoadAverage15
        )
      ));

    return [.. rows];
  }

  private static PerformanceRow[] BuildMemory(HostInfo host, SystemSnapshot snapshot) {
    var system = snapshot.System;
    var used = system.TotalMemoryBytes.HasValue && system.AvailableMemoryBytes.HasValue
      ? Counter.Of(system.TotalMemoryBytes.Value - Math.Min(system.AvailableMemoryBytes.Value, system.TotalMemoryBytes.Value))
      : Counter.Unknown(system.TotalMemoryBytes.HasValue ? system.AvailableMemoryBytes.Reason : system.TotalMemoryBytes.Reason);

    return [
      new("Total", Humanize.Bytes(system.TotalMemoryBytes)),
      new("In use", Humanize.Bytes(used)),
      new("Available", Humanize.Bytes(system.AvailableMemoryBytes)),
      new("Cached", Humanize.Bytes(system.CachedMemoryBytes)),
      new("Swap used", Humanize.Bytes(system.UsedSwapBytes)),
      new("Swap total", Humanize.Bytes(system.TotalSwapBytes)),
      new("Speed", Transfers(host.MemoryTransfersPerSecond)),
      new("Form factor", host.MemoryFormFactor ?? Humanize.Placeholder(UnknownReason.NotPermitted)),
      new("Slots used", Slots(host.MemorySlotsUsed, host.MemorySlotsTotal)),
    ];
  }

  #region formatting

  private static string Percent(Rate? rate)
    => rate is { } value ? Humanize.Percent(value) + " %" : Humanize.Placeholder(UnknownReason.NotSampledYet);

  private static string Hertz(Counter counter) {
    if (!counter.HasValue)
      return Humanize.Placeholder(counter.Reason);

    var hertz = (double)counter.Value;
    return hertz >= 1_000_000_000
      ? (hertz / 1_000_000_000).ToString("0.00", CultureInfo.InvariantCulture) + " GHz"
      : (hertz / 1_000_000).ToString("0", CultureInfo.InvariantCulture) + " MHz";
  }

  private static string Transfers(Counter counter) => counter.HasValue
    ? (counter.Value / 1_000_000d).ToString("0", CultureInfo.InvariantCulture) + " MT/s"
    : Humanize.Placeholder(counter.Reason);

  private static string Slots(Counter used, Counter total) {
    if (!used.HasValue)
      return Humanize.Placeholder(used.Reason);

    return total.HasValue
      ? $"{used.Value} of {total.Value}"
      : used.Value.ToString(CultureInfo.InvariantCulture);
  }

  private static string Uptime(double seconds) {
    if (!double.IsFinite(seconds) || seconds <= 0)
      return Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

    var span = TimeSpan.FromSeconds(seconds);
    return span.TotalDays >= 1
      ? $"{(int)span.TotalDays}d {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
      : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
  }

  #endregion

}
