using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>One labelled figure on the performance page.</summary>
/// <param name="Label">What it is called.</param>
/// <param name="Value">What it says, already formatted and including the reason when there is none.</param>
public readonly record struct PerformanceRow(string Label, string Value);

/// <summary>
/// A group of figures under one heading, and the one number that stands for the whole group.
/// </summary>
/// <param name="Primary">
/// What the resource's graph plots and what its entry in the rail reads — utilisation for a
/// processor, active time for a disk, throughput for an adapter. Unknown for a section that is a
/// description rather than a measurement.
/// </param>
/// <param name="PrimaryMaximum">
/// The top of the scale, or 0 to scale to whatever has been seen. Percentages are fixed at 100 so
/// two machines' graphs mean the same thing; throughput has no natural ceiling and cannot be.
/// </param>
/// <param name="PrimaryLabel">How the primary reads in the rail, already formatted.</param>
/// <param name="Secondary">
/// A second series plotted underneath the first, on the same scale — kernel time under total CPU,
/// which is the split every reference tool draws and the one that tells "my program is slow" from
/// "the machine is in the kernel" (PRD §46). Absent for everything else.
/// </param>
/// <param name="SecondaryLabel">What the second series is, for the legend.</param>
public readonly record struct PerformanceSection(
  string Title,
  IReadOnlyList<PerformanceRow> Rows,
  Rate Primary = default,
  double PrimaryMaximum = 0,
  string PrimaryLabel = "",
  Rate Secondary = default,
  string SecondaryLabel = ""
) {

  /// <summary>Whether there is a second series worth plotting.</summary>
  public bool HasSecondary => this.SecondaryLabel.Length > 0;

}

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
    Func<string, NetworkInterfaceInfo>? describeInterface = null,
    Func<IReadOnlyList<GpuInfo>>? describeGpus = null
  ) {
    ArgumentNullException.ThrowIfNull(host);
    ArgumentNullException.ThrowIfNull(snapshot);

    var utilisation = delta?.SystemCpuPercent ?? Rate.NotSampledYet;
    var memory = MemoryPercent(snapshot);

    var sections = new List<PerformanceSection> {
      new("System", BuildSystem(host, snapshot)),
      new(
        "Processor",
        BuildProcessor(host, snapshot, delta),
        utilisation,
        100,
        Percent(utilisation),
        delta?.SystemKernelPercent ?? Rate.NotSampledYet,
        "kernel"
      ),
      new("Memory", BuildMemory(host, snapshot), memory, 100, Percent(memory)),
    };

    // One section per logical processor, which is what §46's "logical processors" graph mode asks
    // for in the shape this page holds. A core's own kernel time comes with it: the interesting
    // question about a busy core is almost always which half of it is busy.
    var cores = delta?.PerCoreCount ?? 0;
    for (var core = 0; core < cores; ++core) {
      var busy = delta!.PerCoreBusyPercent(core);
      sections.Add(new(
        $"Core {core}",
        BuildCore(core, delta),
        busy,
        100,
        Percent(busy),
        delta.PerCoreKernelPercent(core),
        "kernel"
      ));
    }

    // One per adapter, and only where there is an adapter: a machine with no discrete graphics gets
    // no heading rather than an empty one (PRD §50).
    foreach (var gpu in describeGpus?.Invoke() ?? []) {
      var busy = gpu.BusyPercent.HasValue
        ? Rate.Of(gpu.BusyPercent.Value)
        : Rate.Unknown(gpu.BusyPercent.Reason);

      sections.Add(new(
        gpu.Model is null ? $"GPU — {gpu.Name}" : $"GPU — {gpu.Model}",
        BuildGpu(gpu),
        busy,
        100,
        Percent(busy)
      ));
    }

    // One section per device, so a machine with three disks gets three headings rather than one
    // heading with everything in it — which is the rail §45 asks for, in the shape a page can hold.
    foreach (var disk in snapshot.Disks) {
      var rates = delta?.DiskRatesOf(disk.Name);
      var busy = rates?.BusyPercent ?? Rate.NotSampledYet;
      sections.Add(new(
        $"Disk — {disk.Name}",
        BuildDisk(in disk, delta, describeDisk),
        busy,
        100,
        Percent(busy)
      ));
    }

    foreach (var network in snapshot.Networks) {
      var info = describeInterface?.Invoke(network.Name);
      // Loopback carries real traffic and is not the network; listing it beside the adapters
      // reports a machine talking to itself as bandwidth.
      if (info is { IsLoopback: true })
        continue;

      // Both directions together: the rail answers "is this adapter busy", and one number does
      // that where two compete for the same line.
      var rates = delta?.NetworkRatesOf(network.Name);
      var throughput = rates is { } moving && moving.ReceivedBytesPerSecond.HasValue && moving.SentBytesPerSecond.HasValue
        ? Rate.Of(moving.ReceivedBytesPerSecond.Value + moving.SentBytesPerSecond.Value)
        : Rate.NotSampledYet;

      sections.Add(new(
        $"Network — {network.Name}",
        BuildNetwork(in network, delta, info),
        throughput,
        // No ceiling: an adapter's link speed is often unknown, and a graph scaled to a guess is
        // worse than one scaled to what it has actually seen.
        0,
        Humanize.BytesPerSecond(throughput)
      ));
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

  /// <summary>How much of the machine's memory is in use, as a percentage.</summary>
  private static Rate MemoryPercent(SystemSnapshot snapshot) {
    var total = snapshot.System.TotalMemoryBytes;
    var available = snapshot.System.AvailableMemoryBytes;
    if (!total.HasValue)
      return Rate.Unknown(total.Reason);

    if (!available.HasValue)
      return Rate.Unknown(available.Reason);

    // A machine that reports zero total memory has a broken counter rather than an unknown one, and
    // it is also the denominator — so it is refused here rather than divided by. Asking for the
    // reason of a counter that has a value would itself throw, which is how this was found.
    if (total.Value == 0)
      return Rate.Unknown(UnknownReason.CounterInvalid);

    var used = total.Value - Math.Min(available.Value, total.Value);
    return Rate.Of(used * 100d / total.Value);
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
      new("User time", Percent(delta?.SystemUserPercent)),
      new("Kernel time", Percent(delta?.SystemKernelPercent)),
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

  /// <summary>
  /// One logical processor's own figures.
  /// </summary>
  /// <remarks>
  /// User and kernel do not add up to busy, and deliberately: steal time is busy from this machine's
  /// point of view and belongs to neither, so a virtual machine losing a third of its core to the
  /// hypervisor shows it as the gap rather than hiding it in one of the two.
  /// </remarks>
  private static PerformanceRow[] BuildCore(int core, SnapshotDelta delta) => [
    new("Logical processor", core.ToString(CultureInfo.InvariantCulture)),
    new("Utilisation", Percent(delta.PerCoreBusyPercent(core))),
    new("User time", Percent(delta.PerCoreUserPercent(core))),
    new("Kernel time", Percent(delta.PerCoreKernelPercent(core))),
  ];

  /// <summary>
  /// One graphics adapter's figures (PRD §50).
  /// </summary>
  /// <remarks>
  /// Most of these are blank on most machines and say so rather than reading zero. Which reading is
  /// missing is itself information: an adapter whose driver publishes nothing renders as a column of
  /// <c>n/i</c> beside its name and its driver, which is a truthful page — and a better one than a
  /// row of confident zeros for a card that is busy.
  /// </remarks>
  private static PerformanceRow[] BuildGpu(GpuInfo gpu) {
    var rows = new List<PerformanceRow> {
      new("Adapter", gpu.Model ?? gpu.Name),
      new("Driver", gpu.Driver ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)),
      new("Utilisation", Percent(gpu.BusyPercent.HasValue ? Rate.Of(gpu.BusyPercent.Value) : Rate.Unknown(gpu.BusyPercent.Reason))),
      new("Dedicated memory", Humanize.Bytes(gpu.MemoryTotalBytes)),
      new("Memory in use", Humanize.Bytes(gpu.MemoryUsedBytes)),
      new("Temperature", Celsius(gpu.TemperatureMilliCelsius)),
      new("Power", Watts(gpu.PowerMicrowatts)),
    };

    if (gpu.PowerState is { } state)
      rows.Add(new("Power state", state));

    return [.. rows];
  }

  /// <summary>hwmon counts temperature in thousandths of a degree, which nobody wants to read.</summary>
  private static string Celsius(Counter milliCelsius)
    => milliCelsius.HasValue
      ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} °C", milliCelsius.Value / 1000d)
      : Humanize.Placeholder(milliCelsius.Reason);

  /// <summary>And power in microwatts, for the same reason.</summary>
  private static string Watts(Counter microwatts)
    => microwatts.HasValue
      ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} W", microwatts.Value / 1_000_000d)
      : Humanize.Placeholder(microwatts.Reason);

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

  /// <summary>
  /// A percentage, with its sign — but never a unit on a placeholder. "n/i %" reads as a
  /// measurement of nothing rather than as an admission that nothing was measured.
  /// </summary>
  private static string Percent(Rate? rate) {
    if (rate is not { } value)
      return Humanize.Placeholder(UnknownReason.NotSampledYet);

    return value.HasValue ? Humanize.Percent(value) + " %" : Humanize.Placeholder(value.Reason);
  }

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
