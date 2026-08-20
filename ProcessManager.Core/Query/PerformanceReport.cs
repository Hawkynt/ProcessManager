using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>One labelled figure on the performance page.</summary>
/// <param name="Label">What it is called.</param>
/// <param name="Value">What it says, already formatted and including the reason when there is none.</param>
/// <param name="IsHardware">
/// Whether this describes the hardware rather than what it is doing. The two go in different columns
/// (PRD §45.1): a reader glancing at a page wants the live measurements, and the specifications are
/// what they look at once, when they first meet the machine. Reading them as one list is what makes
/// a performance page look like a data dump.
/// </param>
public readonly record struct PerformanceRow(string Label, string Value, bool IsHardware = false);

/// <summary>
/// One plotted series on a resource's page.
/// </summary>
/// <param name="Label">Its heading, which is also how a reader tells two of them apart without
/// relying on colour (PRD §45.9).</param>
/// <param name="Value">The current reading.</param>
/// <param name="Maximum">The top of its scale, or 0 to fit what it has seen.</param>
/// <param name="ValueLabel">That reading, formatted — including the ceiling where there is one.</param>
/// <param name="Accent">Which colour it takes: <c>cpu</c>, <c>memory</c>, <c>temperature</c>,
/// <c>fan</c>, <c>power</c>, <c>io</c>. Temperature especially, so it never reads as another
/// utilisation figure (PRD §50.1).</param>
public readonly record struct PerformanceGraph(
  string Label,
  Rate Value,
  double Maximum,
  string ValueLabel,
  string Accent
);

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
/// <param name="PartOf">
/// The section this one belongs under, or empty when it stands on its own.
/// </param>
/// <param name="Graphs">
/// The series this resource stacks, where one plot is not enough. A GPU is the case that forces it:
/// six readings that genuinely move independently — a card can be at full utilisation and cold, or
/// idle and hot, and only seeing both at once explains either (PRD §50.1). Null means the resource
/// has the one graph its primary describes.
/// </param>
/// <param name="Composition">
/// How the resource divides up, where that is a question worth a picture. Memory only: the bar that
/// explains why a machine with no free memory is healthy (PRD §14).
/// </param>
/// <param name="RailTitle">
/// What the rail calls this, where the full title is too long for a 230-pixel column. "GPU 0" in
/// the rail and "NVIDIA RTX A5000 Laptop GPU" in the header says more than one truncated string
/// does — the rail is for finding the resource, the header for identifying the hardware (PRD §45.1).
/// Empty means the title serves.
/// </param>
/// <param name="RailDetail">
/// A second reading for the rail, where one is worth having: the clock beside the processor's
/// utilisation, the total beside the memory in use, the temperature beside the GPU's load
/// (PRD §45.1). Short — it shares a line with the headline figure.
/// </param>
/// <remarks>
/// <paramref name="PartOf"/> is what keeps the window's rail readable: a machine with twenty cores
/// would otherwise put twenty entries in it and bury the disks below them. The cores belong under
/// the processor, where a checkbox switches between the whole and the parts — the shape Task Manager
/// uses. A terminal has no checkbox and prints them all, which is why the grouping is a property of
/// the data rather than a decision taken in the report.
/// </remarks>
public readonly record struct PerformanceSection(
  string Title,
  IReadOnlyList<PerformanceRow> Rows,
  Rate Primary = default,
  double PrimaryMaximum = 0,
  string PrimaryLabel = "",
  Rate Secondary = default,
  string SecondaryLabel = "",
  string PartOf = "",
  string RailDetail = "",
  string RailTitle = "",
  IReadOnlyList<PerformanceGraph>? Graphs = null,
  MemoryComposition Composition = default
) {

  /// <summary>Whether there is a second series worth plotting.</summary>
  public bool HasSecondary => this.SecondaryLabel.Length > 0;

  /// <summary>Whether this stands on its own in a list of resources.</summary>
  public bool IsTopLevel => this.PartOf.Length == 0;

  /// <summary>What to put in the rail: the short name where there is one.</summary>
  public string RailName => this.RailTitle.Length > 0 ? this.RailTitle : this.Title;

  /// <summary>
  /// Every series this page plots. A resource that named none plots the one its primary describes,
  /// so a caller never has to ask which shape it is dealing with.
  /// </summary>
  public IReadOnlyList<PerformanceGraph> Series
    => this.Graphs ?? [new(this.Title, this.Primary, this.PrimaryMaximum, this.PrimaryLabel, DefaultAccent(this.Title))];

  private static string DefaultAccent(string title) => title switch {
    "Processor" => "cpu",
    "Memory" => "memory",
    _ when title.StartsWith("Core ", StringComparison.Ordinal) => "cpu",
    _ when title.StartsWith("Disk", StringComparison.Ordinal) => "io",
    _ => "network",
  };

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
        "kernel",
        RailDetail: host.CpuCurrentHertz.HasValue ? Hertz(host.CpuCurrentHertz) : string.Empty
      ),
      new(
        "Memory",
        BuildMemory(host, snapshot),
        memory,
        100,
        Percent(memory),
        RailDetail: MemoryDetail(snapshot),
        Graphs: MemoryGraphs(snapshot),
        Composition: MemoryComposition.Of(in snapshot.System)
      ),
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
        "kernel",
        PartOf: "Processor"
      ));
    }

    // One per adapter, and only where there is an adapter: a machine with no discrete graphics gets
    // no heading rather than an empty one (PRD §50).
    var adapter = 0;
    foreach (var gpu in describeGpus?.Invoke() ?? []) {
      var busy = gpu.BusyPercent.HasValue
        ? Rate.Of(gpu.BusyPercent.Value)
        : Rate.Unknown(gpu.BusyPercent.Reason);

      sections.Add(new(
        gpu.Model is null ? $"GPU — {gpu.Name}" : $"GPU — {gpu.Model}",
        BuildGpu(gpu),
        busy,
        100,
        Percent(busy),
        RailDetail: gpu.TemperatureMilliCelsius.HasValue ? Celsius(gpu.TemperatureMilliCelsius) : string.Empty,
        RailTitle: $"GPU {adapter++}",
        Graphs: GpuGraphs(gpu)
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
        Percent(busy),
        // Reads and writes together: which one a disk is doing is a question for its own page, and
        // the rail has room for one number.
        RailDetail: rates is { } r ? Humanize.BytesPerSecond(Sum(r.ReadBytesPerSecond, r.WriteBytesPerSecond)) : string.Empty,
        RailTitle: $"Disk {disk.Name}",
        // Two, not one: active time says a disk is busy, and only the transfer rate says whether
        // that is a hundred large reads or a hundred thousand small ones (PRD §48).
        Graphs: [
          new("Active time", busy, 100, Percent(busy), "io"),
          new(
            "Transfer rate",
            rates is { } t ? Sum(t.ReadBytesPerSecond, t.WriteBytesPerSecond) : Rate.NotSampledYet,
            0,
            rates is { } shown
              ? $"{Humanize.BytesPerSecond(shown.ReadBytesPerSecond)} read, {Humanize.BytesPerSecond(shown.WriteBytesPerSecond)} write"
              : Pending,
            "io"
          ),
        ]
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
        Humanize.BytesPerSecond(throughput),
        RailDetail: rates is { } n ? $"↓ {Humanize.BytesPerSecond(n.ReceivedBytesPerSecond)}" : string.Empty,
        RailTitle: $"Net {network.Name}"
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
      rows.Add(new("Model", model, IsHardware: true));

    if (info is not null) {
      rows.Add(new("Capacity", Humanize.Bytes(info.CapacityBytes), IsHardware: true));
      rows.Add(new("Media", IsHardware: true, Value: info.Rotational switch {
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
      rows.Add(new("State", info.State ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), IsHardware: true));
      rows.Add(new("Link speed", Bits(info.LinkSpeedBitsPerSecond), IsHardware: true));
      rows.Add(new("MAC address", info.MacAddress ?? Humanize.Placeholder(UnknownReason.NotPermitted), IsHardware: true));
      rows.Add(new("MTU", Humanize.Count(info.MaximumTransmissionUnit), IsHardware: true));
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
      // Live: what it is doing. Utilisation and speed are the two largest figures on the page.
      new("Utilisation", Percent(delta?.SystemCpuPercent)),
      new("Current speed", Hertz(host.CpuCurrentHertz)),
      new("User time", Percent(delta?.SystemUserPercent)),
      new("Kernel time", Percent(delta?.SystemKernelPercent)),
      new("Processes", Humanize.Count(Counter.Of((ulong)snapshot.ProcessCount))),
      // Hardware: what it is.
      new("Model", host.CpuModel ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), IsHardware: true),
      new("Vendor", host.CpuVendor ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), IsHardware: true),
      new("Base speed", Hertz(host.CpuBaseHertz), IsHardware: true),
      new("Sockets", Humanize.Count(host.Sockets), IsHardware: true),
      new("Physical cores", Humanize.Count(host.PhysicalCores), IsHardware: true),
      new("Logical processors", Humanize.Count(host.LogicalProcessors), IsHardware: true),
      new("NUMA nodes", Humanize.Count(host.NumaNodes), IsHardware: true),
      new("L1 data", Humanize.Bytes(host.L1DataBytes), IsHardware: true),
      new("L1 instruction", Humanize.Bytes(host.L1InstructionBytes), IsHardware: true),
      new("L2", Humanize.Bytes(host.L2Bytes), IsHardware: true),
      new("L3", Humanize.Bytes(host.L3Bytes), IsHardware: true),
    };

    if (host.CpuSignature is { } signature)
      rows.Add(new("Signature", signature, IsHardware: true));

    // What the silicon can actually do, from CPUID — grouped rather than listed, because sixty rows
    // of one word each is a data dump and five sentences is a specification (PRD §46).
    AddFeatures(rows, host, "Instruction sets", CpuFeatureKind.InstructionSet);
    AddFeatures(rows, host, "Cryptography", CpuFeatureKind.Cryptography);
    AddFeatures(rows, host, "Security", CpuFeatureKind.Security);
    AddFeatures(rows, host, "Virtualisation", CpuFeatureKind.Virtualisation);
    AddFeatures(rows, host, "Other features", CpuFeatureKind.Other);

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
  /// One line of features, or none at all where the processor reports none of that kind.
  /// </summary>
  /// <remarks>
  /// An empty line is worse than no line: "Cryptography: " reads as a processor that has no crypto
  /// instructions, when on a machine without CPUID it means nobody asked.
  /// </remarks>
  private static void AddFeatures(List<PerformanceRow> rows, HostInfo host, string label, CpuFeatureKind kind) {
    var names = new List<string>();
    foreach (var feature in host.CpuFeatures)
      if (feature.Kind == kind)
        names.Add(feature.Name);

    if (names.Count > 0)
      rows.Add(new(label, string.Join(", ", names), IsHardware: true));
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
  /// <summary>
  /// What the memory rail row says beside its percentage: how much of how much, which is the figure
  /// people actually check.
  /// </summary>
  /// <summary>
  /// Two rates added, which is only possible when both were measured — an unknown plus a number is
  /// an unknown, not the number (PRD §5.3).
  /// </summary>
  private static Rate Sum(Rate first, Rate second)
    => first.HasValue && second.HasValue
      ? Rate.Of(first.Value + second.Value)
      : Rate.Unknown(first.HasValue ? second.Reason : first.Reason);

  private static string MemoryDetail(SystemSnapshot snapshot) {
    var system = snapshot.System;
    if (!system.TotalMemoryBytes.HasValue || !system.AvailableMemoryBytes.HasValue)
      return string.Empty;

    var total = system.TotalMemoryBytes.Value;
    var used = total - Math.Min(system.AvailableMemoryBytes.Value, total);
    return $"{Humanize.Bytes(used)} / {Humanize.Bytes(total)}";
  }

  /// <summary>
  /// The stack of series a GPU page shows (PRD §50.1).
  /// </summary>
  /// <remarks>
  /// A card whose driver does not report a reading gets no graph for it, rather than an empty one:
  /// §45.6's rule that a category the hardware does not have is hidden and not emptied. A laptop
  /// card with no fan of its own should not have a permanently flat fan graph implying it has one
  /// that never spins.
  /// <para>
  /// Utilisation is always here even when it is unknown, because it is what the page is about and
  /// its absence is itself the finding — that is the difference between "this card has no fan" and
  /// "nobody can tell you what this card is doing".
  /// </para>
  /// </remarks>
  private static PerformanceGraph[] GpuGraphs(GpuInfo gpu) {
    var graphs = new List<PerformanceGraph> {
      new("Utilisation", AsRate(gpu.BusyPercent), 100, AsPercent(gpu.BusyPercent), "gpu"),
    };

    if (gpu.MemoryUsedBytes.HasValue)
      graphs.Add(new(
        "Dedicated memory",
        Rate.Of(gpu.MemoryUsedBytes.Value),
        // Scaled to the card's own VRAM, so the height of the fill is the fraction in use.
        gpu.MemoryTotalBytes.HasValue ? gpu.MemoryTotalBytes.Value : 0,
        gpu.MemoryTotalBytes.HasValue
          ? $"{Humanize.Bytes(gpu.MemoryUsedBytes)} / {Humanize.Bytes(gpu.MemoryTotalBytes)}"
          : Humanize.Bytes(gpu.MemoryUsedBytes),
        "gpu"
      ));

    if (gpu.MemoryBusyPercent.HasValue)
      graphs.Add(new("Memory bus", AsRate(gpu.MemoryBusyPercent), 100, AsPercent(gpu.MemoryBusyPercent), "gpu"));

    if (gpu.PowerMicrowatts.HasValue)
      graphs.Add(new(
        "Power",
        Rate.Of(gpu.PowerMicrowatts.Value / 1_000_000d),
        gpu.PowerLimitMicrowatts.HasValue ? gpu.PowerLimitMicrowatts.Value / 1_000_000d : 0,
        PowerDraw(gpu.PowerMicrowatts, gpu.PowerLimitMicrowatts),
        "power"
      ));

    if (gpu.TemperatureMilliCelsius.HasValue)
      graphs.Add(new(
        "Temperature",
        Rate.Of(gpu.TemperatureMilliCelsius.Value / 1000d),
        // A fixed hundred rather than the hottest yet: a card idling between 40 and 42 °C would
        // otherwise fill its graph and look alarming (PRD §45.4).
        100,
        Celsius(gpu.TemperatureMilliCelsius),
        "temperature"
      ));

    if (gpu.FanPercent.HasValue)
      graphs.Add(new("Fan", AsRate(gpu.FanPercent), 100, AsPercent(gpu.FanPercent), "fan"));

    return [.. graphs];
  }

  private static Rate AsRate(Counter counter)
    => counter.HasValue ? Rate.Of(counter.Value) : Rate.Unknown(counter.Reason);

  private static PerformanceRow[] BuildGpu(GpuInfo gpu) {
    var rows = new List<PerformanceRow> {
      new("Utilisation", AsPercent(gpu.BusyPercent)),
      new("Memory bus", AsPercent(gpu.MemoryBusyPercent)),
      new("Memory in use", Humanize.Bytes(gpu.MemoryUsedBytes)),
      new("Temperature", Celsius(gpu.TemperatureMilliCelsius)),
      // Draw and cap on one line, because neither means much alone.
      new("Power", PowerDraw(gpu.PowerMicrowatts, gpu.PowerLimitMicrowatts)),
      new("Core clock", Hertz(gpu.CoreClockHertz)),
      new("Memory clock", Hertz(gpu.MemoryClockHertz)),
      new("Fan", AsPercent(gpu.FanPercent)),
      new("Adapter", gpu.Model ?? gpu.Name, IsHardware: true),
      new("Driver", gpu.Driver ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), IsHardware: true),
      new("Dedicated memory", Humanize.Bytes(gpu.MemoryTotalBytes), IsHardware: true),
      new("Power cap", Watts(gpu.PowerCapMicrowatts), IsHardware: true),
    };

    if (gpu.PowerState is { } state)
      rows.Add(new("Power state", state, IsHardware: true));

    return [.. rows];
  }

  /// <summary>A counter that is already a percentage, rendered like every other percentage.</summary>
  private static string AsPercent(Counter counter)
    => Percent(counter.HasValue ? Rate.Of(counter.Value) : Rate.Unknown(counter.Reason));

  /// <summary>
  /// The draw against the cap: "30.2 W of 130.0 W". A card drawing thirty watts is doing something
  /// very different at a forty-watt ceiling than at a four-hundred-watt one, and the number on its
  /// own cannot say which.
  /// </summary>
  private static string PowerDraw(Counter microwatts, Counter limit) {
    var draw = Watts(microwatts);
    return limit.HasValue ? $"{draw} of {Watts(limit)}" : draw;
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

  /// <summary>
  /// The memory page's two series (PRD §47).
  /// </summary>
  /// <remarks>
  /// Physical memory is scaled to what the machine has rather than to a hundred, because the useful
  /// question is how much of it is gone and not what fraction — 60 % means nothing until you know
  /// whether the machine has 8 GB or 128.
  /// <para>
  /// Commit is its own series and not a second line on the first: it counts what has been asked for
  /// rather than what has been taken, routinely exceeds physical memory, and drawn on the same axis
  /// would either be clipped or would squash the physical line into the floor.
  /// </para>
  /// </remarks>
  private static PerformanceGraph[] MemoryGraphs(SystemSnapshot snapshot) {
    var system = snapshot.System;
    if (!system.TotalMemoryBytes.HasValue)
      return [new("Memory", MemoryPercent(snapshot), 100, Percent(MemoryPercent(snapshot)), "memory")];

    var total = system.TotalMemoryBytes.Value;
    var used = system.AvailableMemoryBytes.HasValue
      ? Rate.Of(total - Math.Min(system.AvailableMemoryBytes.Value, total))
      : Rate.Unknown(system.AvailableMemoryBytes.Reason);

    var graphs = new List<PerformanceGraph> {
      new(
        "Physical memory",
        used,
        total,
        used.HasValue ? $"{Humanize.Bytes(Counter.Of((ulong)used.Value))} of {Humanize.Bytes(system.TotalMemoryBytes)}" : Pending,
        "memory"
      ),
    };

    if (system.CommittedBytes.HasValue)
      graphs.Add(new(
        "Committed",
        Rate.Of(system.CommittedBytes.Value),
        system.CommitLimitBytes.HasValue ? system.CommitLimitBytes.Value : 0,
        Pair(system.CommittedBytes, system.CommitLimitBytes),
        "memory"
      ));

    return [.. graphs];
  }

  private static PerformanceRow[] BuildMemory(HostInfo host, SystemSnapshot snapshot) {
    var system = snapshot.System;
    var used = system.TotalMemoryBytes.HasValue && system.AvailableMemoryBytes.HasValue
      ? Counter.Of(system.TotalMemoryBytes.Value - Math.Min(system.AvailableMemoryBytes.Value, system.TotalMemoryBytes.Value))
      : Counter.Unknown(system.TotalMemoryBytes.HasValue ? system.AvailableMemoryBytes.Reason : system.TotalMemoryBytes.Reason);

    return [
      new("In use", Humanize.Bytes(used)),
      new("Available", Humanize.Bytes(system.AvailableMemoryBytes)),
      new("Free", Humanize.Bytes(system.FreeMemoryBytes)),
      new("Cached", Humanize.Bytes(system.CachedMemoryBytes)),
      new("Buffers", Humanize.Bytes(system.BufferMemoryBytes)),
      new("Modified", Humanize.Bytes(system.ModifiedMemoryBytes)),
      new("Shared", Humanize.Bytes(system.SharedMemoryBytes)),
      // Committed against its limit on one line: how much every process together has asked for,
      // against how much the kernel will ever agree to. Either alone says very little (PRD §16).
      new("Committed", Pair(system.CommittedBytes, system.CommitLimitBytes)),
      new("Swap", Pair(system.UsedSwapBytes, system.TotalSwapBytes)),
      new("Kernel, reclaimable", Humanize.Bytes(system.ReclaimableKernelBytes)),
      new("Kernel, fixed", Humanize.Bytes(system.UnreclaimableKernelBytes)),
      new("Page tables", Humanize.Bytes(system.PageTableBytes)),
      new("Kernel stacks", Humanize.Bytes(system.KernelStackBytes)),
      new("Total", Humanize.Bytes(system.TotalMemoryBytes), IsHardware: true),
      new("Speed", Transfers(host.MemoryTransfersPerSecond), IsHardware: true),
      new("Form factor", host.MemoryFormFactor ?? Humanize.Placeholder(UnknownReason.NotPermitted), IsHardware: true),
      new("Slots used", Slots(host.MemorySlotsUsed, host.MemorySlotsTotal), IsHardware: true),
    ];
  }

  /// <summary>
  /// "28.9G of 126G" — a figure against its ceiling, or just the figure when there is no ceiling to
  /// put it against.
  /// </summary>
  private static string Pair(Counter value, Counter limit)
    => limit.HasValue ? $"{Humanize.Bytes(value)} of {Humanize.Bytes(limit)}" : Humanize.Bytes(value);

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
