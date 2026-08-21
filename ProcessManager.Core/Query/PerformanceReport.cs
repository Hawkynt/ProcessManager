using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Which of §45.2's levels a figure belongs to, and therefore where it is read.
/// </summary>
/// <remarks>
/// Four levels, and nothing may jump one. The two that a page separates into columns are here; the
/// immediate status above them is the graphs rather than a row.
/// </remarks>
public enum PerformanceRowLevel {

  /// <summary>What the resource is doing now — the left column.</summary>
  Live,

  /// <summary>
  /// What the resource is. The right column: a reader glancing at a page wants the measurements, and
  /// the specifications are what they look at once, when they first meet the machine. Reading the
  /// two as one list is what makes a performance page look like a data dump (PRD §45.1).
  /// </summary>
  Hardware,

  /// <summary>
  /// Engineering diagnostics — level four, and collapsed until asked for.
  /// </summary>
  /// <remarks>
  /// Kernel pools, page tables, reclaim lists, huge pages. Every one of them answers a question
  /// somebody eventually has, and all of them at once are what turns a page into a wall. They are in
  /// the report whatever the window does with them, so <c>--host</c> and an export still carry the
  /// whole machine (PRD §58).
  /// </remarks>
  Diagnostic,

}

/// <summary>One labelled figure on the performance page.</summary>
/// <param name="Label">What it is called.</param>
/// <param name="Value">What it says, already formatted and including the reason when there is none.</param>
/// <param name="Level">Which of §45.2's levels it belongs to, and so where it is shown.</param>
public readonly record struct PerformanceRow(
  string Label,
  string Value,
  PerformanceRowLevel Level = PerformanceRowLevel.Live
) {

  /// <summary>Whether this describes the hardware rather than what it is doing.</summary>
  public bool IsHardware => this.Level == PerformanceRowLevel.Hardware;

  /// <summary>Whether it belongs in the collapsed block rather than in either column.</summary>
  public bool IsDiagnostic => this.Level == PerformanceRowLevel.Diagnostic;

}

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
/// <param name="Unit">
/// What one sample of it is, so a front-end can render a sample it was not given a label for — the
/// reading under a hover cursor, an exported point. Without it a plot can only guess from its own
/// scale, and guessing turns 1.2 MB/s into "1.2M" (PRD §45.4, §76).
/// </param>
/// <param name="Companion">
/// A second line on the same axis, where one quantity is genuinely two — a disk's reads against its
/// writes, an adapter's receive against its send (PRD §48, §49). Only ever read when
/// <paramref name="CompanionLabel"/> names it, because <c>default(Rate)</c> is a confident zero and
/// a graph that did not ask for a second line must not be given one drawn along the floor (§5.3).
/// </param>
/// <param name="CompanionLabel">What the second line is, and the flag that there is one.</param>
/// <param name="SeriesLabel">
/// What the <em>first</em> line is called where that differs from the graph's own heading. A plot
/// headed "Transfer rate" has lines called "read" and "write"; one headed "Temperature" has one line
/// and needs no other name for it.
/// </param>
public readonly record struct PerformanceGraph(
  string Label,
  Rate Value,
  double Maximum,
  string ValueLabel,
  string Accent,
  PerformanceUnit Unit = PerformanceUnit.Percent,
  Rate Companion = default,
  string CompanionLabel = "",
  string SeriesLabel = ""
) {

  /// <summary>Whether a second line was asked for, which is the only thing that makes one exist.</summary>
  public bool HasCompanion => this.CompanionLabel.Length > 0;

  /// <summary>What to call the first line: its own name where it has one, the heading otherwise.</summary>
  public string FirstLabel => this.SeriesLabel.Length > 0 ? this.SeriesLabel : this.Label;

}

/// <summary>What a plotted sample is measured in (PRD §76).</summary>
public enum PerformanceUnit {

  Percent,
  Bytes,
  BytesPerSecond,
  Celsius,
  Watts,

  /// <summary>
  /// A plain count of things: descriptors held, threads running.
  /// </summary>
  /// <remarks>
  /// Added last on purpose. Without it a plot of thread count had to declare itself a percentage,
  /// and its hover readings said "42 %" of nothing at all (PRD §28, §76).
  /// </remarks>
  Count,

}

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

  /// <summary>
  /// Whether this section measures anything at all.
  /// </summary>
  /// <remarks>
  /// Not <c>Primary.HasValue</c>, which is the trap: <c>default(Rate)</c> is a confident zero, so a
  /// section that never named a primary — the host description, the activity lists — answered that
  /// it was measured and idle. The rail drew each of them a sparkline flat along the floor, which is
  /// a graph of something nobody measured (PRD §5.3, §72.3).
  /// </remarks>
  public bool HasPrimary => this.PrimaryLabel.Length > 0 || this.Graphs is { Count: > 0 };

  /// <summary>Whether this stands on its own in a list of resources.</summary>
  public bool IsTopLevel => this.PartOf.Length == 0;

  /// <summary>What to put in the rail: the short name where there is one.</summary>
  public string RailName => this.RailTitle.Length > 0 ? this.RailTitle : this.Title;

  /// <summary>
  /// Every series this page plots. A resource that named none plots the one its primary describes,
  /// so a caller never has to ask which shape it is dealing with — and a section that measures
  /// nothing plots nothing, rather than a line along the floor.
  /// </summary>
  public IReadOnlyList<PerformanceGraph> Series => this.Graphs
    ?? (this.HasPrimary
      ? [new(
          this.Title,
          this.Primary,
          this.PrimaryMaximum,
          this.PrimaryLabel,
          DefaultAccent(this.Title),
          // A fixed hundred is a percentage and anything without a ceiling is a throughput, which
          // between them are every primary a resource has: utilisation, active time, bytes a second.
          this.PrimaryMaximum == 100 ? PerformanceUnit.Percent : PerformanceUnit.BytesPerSecond
        )]
      : []);

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
  /// <param name="topology">
  /// How the logical processors are arranged. Optional: without it a core says what it is doing and
  /// not where it sits, and the machine gets no per-node view — which is the honest shape for a
  /// machine that does not publish one (PRD §46).
  /// </param>
  public static IReadOnlyList<PerformanceSection> Build(
    HostInfo host,
    SystemSnapshot snapshot,
    SnapshotDelta? delta = null,
    Func<string, DiskInfo>? describeDisk = null,
    Func<string, NetworkInterfaceInfo>? describeInterface = null,
    Func<IReadOnlyList<GpuInfo>>? describeGpus = null,
    Func<IReadOnlyList<BatteryInfo>>? describeBatteries = null,
    Func<IReadOnlyList<SensorGroup>>? describeSensors = null,
    CpuTopology? topology = null
  ) {
    ArgumentNullException.ThrowIfNull(host);
    ArgumentNullException.ThrowIfNull(snapshot);

    var utilisation = delta?.SystemCpuPercent ?? Rate.NotSampledYet;
    var memory = MemoryPercent(snapshot);

    var sections = new List<PerformanceSection> {
      new("System", BuildSystem(host, snapshot)),
      // What is using the machine, rather than how much of it is used. A sorted table answers this
      // only if somebody already sorted it by the right column, and answering three resources means
      // sorting three times and losing their place each time (PRD §51).
      new("Activity", BuildActivity(snapshot, delta)),
      new(
        "Processor",
        BuildProcessor(host, snapshot, delta, topology),
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
        BuildCore(core, delta, topology),
        busy,
        100,
        Percent(busy),
        delta.PerCoreKernelPercent(core),
        "kernel",
        PartOf: "Processor"
      ));
    }

    AddNodes(sections, delta, topology);

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

    // One per battery, and only where there is one: a desktop gets no heading rather than a battery
    // reported at nought per cent, which is what a page that assumed one would draw (PRD §45.3).
    var pack = 0;
    foreach (var battery in SensorSources.Ask(describeBatteries ?? SensorSources.Batteries)) {
      var charge = battery.ChargePercent.HasValue
        ? Rate.Of(battery.ChargePercent.Value)
        : Rate.Unknown(battery.ChargePercent.Reason);

      sections.Add(new(
        battery.Model is null ? $"Battery — {battery.Name}" : $"Battery — {battery.Model}",
        BuildBattery(battery),
        charge,
        100,
        Percent(charge),
        RailDetail: BatteryDetail(battery),
        RailTitle: pack++ == 0 ? "Battery" : $"Battery {pack}"
      ));
    }

    // One per sensor chip. Grouped the way the machine groups them, because seventy degrees means
    // nothing until you know whether it came from the processor, an SSD or the wireless card.
    foreach (var group in SensorSources.Ask(describeSensors ?? SensorSources.Sensors)) {
      var hottest = Hottest(group);
      sections.Add(new(
        $"Sensors — {group.Name}",
        BuildSensors(group),
        hottest,
        100,
        hottest.HasValue ? Celsius(Counter.Of((ulong)(hottest.Value * 1000))) : string.Empty,
        RailDetail: hottest.HasValue ? Celsius(Counter.Of((ulong)(hottest.Value * 1000))) : string.Empty,
        RailTitle: group.Name
      ));
    }

    // One section per device, so a machine with three disks gets three headings rather than one
    // heading with everything in it — which is the rail §45 asks for, in the shape a page can hold.
    foreach (var disk in snapshot.Disks) {
      var rates = delta?.DiskRatesOf(disk.Name);
      var busy = rates?.BusyPercent ?? Rate.NotSampledYet;
      sections.Add(new(
        $"Disk — {disk.Name}",
        BuildDisk(in disk, delta, describeDisk, snapshot.System.IoPressure),
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
          // Two lines rather than their sum: a disk reading at 500 MB/s and one writing at 500 MB/s
          // draw the same combined line and are not the same machine, and the direction is most of
          // what somebody looking at a busy disk wants to know (PRD §48).
          new(
            "Transfer rate",
            rates is { } t ? t.ReadBytesPerSecond : Rate.NotSampledYet,
            0,
            rates is { } shown
              ? $"{Humanize.BytesPerSecond(shown.ReadBytesPerSecond)} read, {Humanize.BytesPerSecond(shown.WriteBytesPerSecond)} write"
              : Pending,
            "io",
            PerformanceUnit.BytesPerSecond,
            Companion: rates is { } w ? w.WriteBytesPerSecond : Rate.NotSampledYet,
            CompanionLabel: "write",
            SeriesLabel: "read"
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
        RailTitle: $"Net {network.Name}",
        // Receive and send as two lines rather than their sum: an adapter pulling a hundred megabits
        // and one pushing them are the same combined line and very different machines, and which
        // direction is moving is the first thing anybody asks of a busy adapter (PRD §49).
        Graphs: [
          new(
            "Throughput",
            rates is { } r ? r.ReceivedBytesPerSecond : Rate.NotSampledYet,
            0,
            rates is { } shown
              ? $"↓ {Humanize.BytesPerSecond(shown.ReceivedBytesPerSecond)}  ↑ {Humanize.BytesPerSecond(shown.SentBytesPerSecond)}"
              : Pending,
            "network",
            PerformanceUnit.BytesPerSecond,
            Companion: rates is { } s ? s.SentBytesPerSecond : Rate.NotSampledYet,
            CompanionLabel: "send",
            SeriesLabel: "receive"
          ),
        ]
      ));
    }

    return sections;
  }

  private static PerformanceRow[] BuildDisk(
    in DiskCounters disk,
    SnapshotDelta? delta,
    Func<string, DiskInfo>? describe,
    PressureReading pressure
  ) {
    var info = describe?.Invoke(disk.Name);
    var rates = delta?.DiskRatesOf(disk.Name);

    var rows = new List<PerformanceRow>();
    if (info?.Model is { Length: > 0 } model)
      rows.Add(new("Model", model, Level: PerformanceRowLevel.Hardware));

    if (info is not null) {
      rows.Add(new("Capacity", Humanize.Bytes(info.CapacityBytes), Level: PerformanceRowLevel.Hardware));
      rows.Add(new("Media", Level: PerformanceRowLevel.Hardware, Value: info.Rotational switch {
        true => "rotational",
        false => "solid state",
        // The kernel not saying is not the same as "no", and a machine that reports neither should
        // not be described as a hard disk by default.
        null => Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
      }));

      if (info.Bus is { Length: > 0 } bus)
        rows.Add(new("Interface", bus, Level: PerformanceRowLevel.Hardware));

      if (info.Serial is { Length: > 0 } serial)
        rows.Add(new("Serial", serial, Level: PerformanceRowLevel.Hardware));

      // What this disk is to the machine, which is the pair of facts somebody about to unplug it
      // wants: whether the system is on it, and whether it is swapping to it.
      rows.Add(new("Role", Role(info), Level: PerformanceRowLevel.Hardware));
      rows.Add(new("Mounted at", Mounted(info.Volumes), Level: PerformanceRowLevel.Hardware));
    }

    rows.Add(new("Active time", rates is { } active ? Percent(active.BusyPercent) : Pending));
    // Machine-wide rather than this device's: the kernel does not attribute stall time per disk.
    rows.Add(new("Stalled on I/O", Pressure(pressure.Some)));
    rows.Add(new("Read rate", rates is { } read ? Humanize.BytesPerSecond(read.ReadBytesPerSecond) : Pending));
    rows.Add(new("Write rate", rates is { } write ? Humanize.BytesPerSecond(write.WriteBytesPerSecond) : Pending));
    rows.Add(new("Read IOPS", rates is { } readOps ? Humanize.Rate(readOps.ReadOperationsPerSecond) : Pending));
    rows.Add(new("Write IOPS", rates is { } writeOps ? Humanize.Rate(writeOps.WriteOperationsPerSecond) : Pending));
    // Active time says the disk is busy; only these say whether it is keeping up. A queue of one at
    // full active time is a disk saturated by a single client, and a queue of thirty at the same
    // active time is a disk being asked for far more than it can do (PRD §48).
    rows.Add(new("Response time", rates is { } response ? Milliseconds(response.ResponseTimeMilliseconds) : Pending));
    rows.Add(new("Queue length", rates is { } queue ? Depth(queue.QueueLength) : Pending));
    rows.Add(new(
      "Latency",
      rates is { } latency
        ? $"{Milliseconds(latency.ReadLatencyMilliseconds)} read, {Milliseconds(latency.WriteLatencyMilliseconds)} write"
        : Pending
    ));

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
      rows.Add(new("State", info.State ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), Level: PerformanceRowLevel.Hardware));
      rows.Add(new("Type", Description(info), Level: PerformanceRowLevel.Hardware));
      rows.Add(new("Link speed", Bits(info.LinkSpeedBitsPerSecond), Level: PerformanceRowLevel.Hardware));
      rows.Add(new("MAC address", info.MacAddress ?? Humanize.Placeholder(UnknownReason.NotPermitted), Level: PerformanceRowLevel.Hardware));
      rows.Add(new("MTU", Humanize.Count(info.MaximumTransmissionUnit), Level: PerformanceRowLevel.Hardware));
      rows.Add(new(
        "Index",
        info.Index is { } index ? index.ToString(CultureInfo.InvariantCulture) : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
        Level: PerformanceRowLevel.Hardware
      ));

      rows.Add(new("Address", Addresses(info.Addresses), Level: PerformanceRowLevel.Hardware));
      rows.Add(new("Gateway", info.Gateway ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), Level: PerformanceRowLevel.Hardware));
      rows.Add(new("DNS", Addresses(info.DnsServers), Level: PerformanceRowLevel.Hardware));
    }

    rows.Add(new("Receive rate", rates is { } received ? Humanize.BytesPerSecond(received.ReceivedBytesPerSecond) : Pending));
    rows.Add(new("Send rate", rates is { } sent ? Humanize.BytesPerSecond(sent.SentBytesPerSecond) : Pending));
    // Only where the link speed is known, which excludes Wi-Fi and everything virtual. A utilisation
    // against a guessed denominator is worse than none at all (PRD §49).
    if (info is not null && info.LinkSpeedBitsPerSecond.HasValue)
      rows.Add(new("Utilisation", Percent(Utilisation(rates, info.LinkSpeedBitsPerSecond))));

    rows.Add(new("Received", Humanize.Bytes(network.ReceivedBytes)));
    rows.Add(new("Sent", Humanize.Bytes(network.SentBytes)));

    // The four rows a wireless adapter has and a wired one does not (PRD §49).
    if (info is { IsWireless: true }) {
      rows.Add(new("Network", info.Ssid ?? "not associated"));
      rows.Add(new("Signal", Signal(info.SignalDbm)));
      rows.Add(new("Channel", Channel(info.FrequencyMegahertz), Level: PerformanceRowLevel.Hardware));
      // 802.11n against 802.11ax is in the association's own rate table, which needs nl80211 and is
      // not read: an adapter's own capability is not what it negotiated (PRD §5.3).
      rows.Add(new("Protocol", Humanize.Placeholder(UnknownReason.NotImplementedHere), Level: PerformanceRowLevel.Hardware));
    }

    // Errors and drops are two different failures, and both are almost always zero — which is why
    // they are worth a row: a non-zero one is the whole reason somebody opened this page.
    rows.Add(new("Errors", $"{Humanize.Count(network.ReceiveErrors)} in, {Humanize.Count(network.SendErrors)} out"));
    rows.Add(new("Dropped", $"{Humanize.Count(network.ReceiveDropped)} in, {Humanize.Count(network.SendDropped)} out"));
    return [.. rows];
  }

  /// <summary>
  /// What the machine uses a disk for: "system, swap", "swap", or nothing much (PRD §48).
  /// </summary>
  /// <remarks>
  /// One row for the two indicators, because a disk that is neither would otherwise carry two rows
  /// reading "no". Unknown where the mount table could not be read at all — which is a machine
  /// nobody asked rather than a disk nothing is on (§5.3).
  /// </remarks>
  private static string Role(DiskInfo info) {
    if (info.IsSystemDisk is not { } system || info.HoldsSwap is not { } swap)
      return Humanize.Placeholder(UnknownReason.NotPermitted);

    return (system, swap) switch {
      (true, true) => "system disk, swap",
      (true, false) => "system disk",
      (false, true) => "swap",
      _ => "data",
    };
  }

  /// <summary>
  /// Where a disk is mounted, or the fact that it is not.
  /// </summary>
  /// <remarks>
  /// A disk with a dozen subvolumes would fill the column, so the list stops at four and says how
  /// many it did not name — the point of the row is to identify the disk, and four mount points do
  /// that as well as twelve.
  /// </remarks>
  private static string Mounted(IReadOnlyList<string>? volumes) {
    if (volumes is null)
      return Humanize.Placeholder(UnknownReason.NotPermitted);

    if (volumes.Count == 0)
      return "not mounted";

    const int Shown = 4;
    var text = new System.Text.StringBuilder();
    for (var i = 0; i < Math.Min(Shown, volumes.Count); ++i) {
      if (text.Length > 0)
        text.Append(", ");

      text.Append(volumes[i]);
    }

    if (volumes.Count > Shown)
      text.Append(", +").Append((volumes.Count - Shown).ToString(CultureInfo.InvariantCulture)).Append(" more");

    return text.ToString();
  }

  /// <summary>A duration in the unit a disk's response times actually fall in.</summary>
  /// <remarks>
  /// Microseconds under a millisecond, because an NVMe device answers in about a hundred of them
  /// and "0.1 ms" throws away the digit that distinguishes a fast device from a slow one.
  /// </remarks>
  private static string Milliseconds(Rate rate) {
    if (!rate.HasValue)
      return Humanize.Placeholder(rate.Reason);

    return rate.Value < 1
      ? (rate.Value * 1000).ToString("0", CultureInfo.InvariantCulture) + " µs"
      : rate.Value.ToString(rate.Value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " ms";
  }

  /// <summary>An average queue depth, which is a count and routinely a fraction of one.</summary>
  private static string Depth(Rate rate)
    => rate.HasValue
      ? rate.Value.ToString(rate.Value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture)
      : Humanize.Placeholder(rate.Reason);

  private static string Pending => Humanize.Placeholder(UnknownReason.NotSampledYet);

  /// <summary>"wireless · iwlwifi" — what the interface is, and what is driving it.</summary>
  private static string Description(NetworkInterfaceInfo info) {
    var kind = info.Kind ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);
    return info.Driver is { Length: > 0 } driver ? $"{kind} · {driver}" : kind;
  }

  /// <summary>
  /// Addresses on one line, or the reason there are none to show.
  /// </summary>
  /// <remarks>
  /// Three separate statements, and they must not collapse into one: null is nobody asked, empty is
  /// an interface that genuinely carries none — an unconfigured adapter, a bridge port — and a list
  /// is a list (PRD §5.3).
  /// </remarks>
  private static string Addresses(IReadOnlyList<string>? addresses) {
    if (addresses is null)
      return Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

    return addresses.Count == 0 ? "none" : string.Join(", ", addresses);
  }

  /// <summary>
  /// How much of the link the traffic is using, both directions against the one negotiated speed.
  /// </summary>
  /// <remarks>
  /// Bytes into bits, because a link speed is quoted in bits and getting that wrong is a factor of
  /// eight. Full duplex is not modelled: a gigabit link can carry a gigabit each way at once, so a
  /// saturated pair would read 200 %, and every tool that shows this figure sums them anyway — the
  /// question being asked is how loaded the link is.
  /// </remarks>
  private static Rate Utilisation(SnapshotDelta.NetworkRates? rates, Counter linkSpeedBits) {
    if (rates is not { } moving)
      return Rate.NotSampledYet;

    if (!moving.ReceivedBytesPerSecond.HasValue)
      return Rate.Unknown(moving.ReceivedBytesPerSecond.Reason);

    if (!moving.SentBytesPerSecond.HasValue)
      return Rate.Unknown(moving.SentBytesPerSecond.Reason);

    if (!linkSpeedBits.HasValue || linkSpeedBits.Value == 0)
      return Rate.Unknown(linkSpeedBits.HasValue ? UnknownReason.CounterInvalid : linkSpeedBits.Reason);

    var bits = (moving.ReceivedBytesPerSecond.Value + moving.SentBytesPerSecond.Value) * 8;
    return Rate.Of(bits * 100d / linkSpeedBits.Value);
  }

  /// <summary>"−61 dBm  (good)" — the number, and what it means to somebody who does not read dBm.</summary>
  /// <remarks>
  /// The thresholds are the ones every wireless tool uses: −50 and better is as good as it gets,
  /// −70 is the edge of comfortable, and below −80 a connection drops under any load at all.
  /// </remarks>
  private static string Signal(int? dbm) {
    if (dbm is not { } level)
      return Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

    var quality = level switch {
      >= -50 => "excellent",
      >= -60 => "good",
      >= -70 => "fair",
      >= -80 => "weak",
      _ => "very weak",
    };

    // A typographic minus, like every other signed figure in the program: a hyphen in front of a
    // number reads as a dash between two of them at small sizes.
    return string.Format(
      CultureInfo.InvariantCulture,
      "{0}{1} dBm  ({2})",
      level < 0 ? "−" : string.Empty,
      Math.Abs(level),
      quality
    );
  }

  /// <summary>"6  (2.4 GHz, 2437 MHz)" — the channel, its band, and the frequency behind both.</summary>
  private static string Channel(int? megahertz) {
    if (megahertz is not { } frequency)
      return Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

    var placement = WirelessChannel.Of(frequency);
    var band = placement is { } known
      ? string.Format(CultureInfo.InvariantCulture, "{0}  ({1}, {2} MHz)", known.Channel, known.Band, frequency)
      : string.Format(CultureInfo.InvariantCulture, "{0} MHz", frequency);

    return band;
  }

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

  /// <summary>
  /// The four "what is using this" lists, and how fast the machine is churning (PRD §51).
  /// </summary>
  /// <remarks>
  /// The entries are flattened into rows so this page needs no new shape — the label carries the
  /// place in the list and the value carries the process, which reads the way a top-five list reads
  /// aloud. A resource nothing is using at all gets one row saying so rather than five blank ones.
  /// </remarks>
  private static PerformanceRow[] BuildActivity(SystemSnapshot snapshot, SnapshotDelta? delta) {
    var rows = new List<PerformanceRow>();
    foreach (var (heading, id) in new (string, ProcessField)[] {
      ("Processor", ProcessField.CpuPercent),
      ("Memory", ProcessField.WorkingSetBytes),
      ("Reading", ProcessField.ReadBytesPerSecond),
      ("Writing", ProcessField.WriteBytesPerSecond),
    }) {
      var top = SystemActivity.Top(snapshot, delta, id);
      if (top.Count == 0) {
        rows.Add(new(heading, delta is null ? Pending : "nothing"));
        continue;
      }

      for (var i = 0; i < top.Count; ++i)
        rows.Add(new(i == 0 ? heading : string.Empty, $"{top[i].Name}  ({top[i].Value})"));
    }

    rows.AddRange(SystemActivity.Rates(snapshot, delta));
    return [.. rows];
  }

  private static PerformanceRow[] BuildProcessor(
    HostInfo host,
    SystemSnapshot snapshot,
    SnapshotDelta? delta,
    CpuTopology? topology
  ) {
    var rows = new List<PerformanceRow> {
      // Live: what it is doing. Utilisation and speed are the two largest figures on the page.
      new("Utilisation", Percent(delta?.SystemCpuPercent)),
      new("Current speed", Hertz(host.CpuCurrentHertz)),
      new("User time", Percent(delta?.SystemUserPercent)),
      new("Kernel time", Percent(delta?.SystemKernelPercent)),
      new("Processes", Humanize.Count(Counter.Of((ulong)snapshot.ProcessCount))),
      new("Threads", Humanize.Count(Counter.Of((ulong)Math.Max(0, snapshot.System.TotalThreads)))),
      // §46's handle count, in this kernel's own word for it. Descriptors rather than handles,
      // because that is what Linux hands out and calling them handles would be describing this
      // machine in another system's vocabulary (PRD §5.3).
      new("Descriptors", Humanize.Count(snapshot.System.OpenDescriptors)),
      new("Uptime", Uptime(snapshot.System.UptimeSeconds)),
      // Pressure, not utilisation: a processor at 100 % is not in trouble if nothing is waiting for
      // it, and one at 60 % with things queued behind it is (PRD §46).
      new("Stalled on CPU", Pressure(snapshot.System.CpuPressure.Some)),
      // Hardware: what it is.
      new("Model", host.CpuModel ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), Level: PerformanceRowLevel.Hardware),
      new("Vendor", host.CpuVendor ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), Level: PerformanceRowLevel.Hardware),
      new("Base speed", Hertz(host.CpuBaseHertz), Level: PerformanceRowLevel.Hardware),
      new("Sockets", Humanize.Count(host.Sockets), Level: PerformanceRowLevel.Hardware),
      new("Physical cores", Humanize.Count(host.PhysicalCores), Level: PerformanceRowLevel.Hardware),
      new("Logical processors", Humanize.Count(host.LogicalProcessors), Level: PerformanceRowLevel.Hardware),
      new("NUMA nodes", Humanize.Count(host.NumaNodes), Level: PerformanceRowLevel.Hardware),
      new("L1 data", Humanize.Bytes(host.L1DataBytes), Level: PerformanceRowLevel.Hardware),
      new("L1 instruction", Humanize.Bytes(host.L1InstructionBytes), Level: PerformanceRowLevel.Hardware),
      new("L2", Humanize.Bytes(host.L2Bytes), Level: PerformanceRowLevel.Hardware),
      new("L3", Humanize.Bytes(host.L3Bytes), Level: PerformanceRowLevel.Hardware),
    };

    if (host.CpuSignature is { } signature)
      rows.Add(new("Signature", signature, Level: PerformanceRowLevel.Hardware));

    // What the clock is allowed to do, and who is deciding. Only where cpufreq is present at all: a
    // virtual machine whose clock its host owns has no policy, and three unknown rows would say the
    // machine had one nobody could read (PRD §5.3).
    if (host.CpuMinimumHertz.HasValue || host.CpuMaximumHertz.HasValue)
      rows.Add(new("Speed range", $"{Hertz(host.CpuMinimumHertz)} – {Hertz(host.CpuMaximumHertz)}", Level: PerformanceRowLevel.Hardware));

    if (host.CpuGovernor is { Length: > 0 } governor)
      rows.Add(new(
        "Governor",
        host.CpuScalingDriver is { Length: > 0 } driver ? $"{governor}  ({driver})" : governor,
        Level: PerformanceRowLevel.Hardware
      ));

    // Only on a part that has both kinds. On anything else the row would answer a question the
    // machine does not raise (PRD §46).
    if (topology is { IsHybrid: true })
      rows.Add(new("Core kinds", CoreKinds(topology), Level: PerformanceRowLevel.Hardware));

    AddProcessorCounters(rows, snapshot, delta);

    // What the silicon can actually do, from CPUID — grouped rather than listed, because sixty rows
    // of one word each is a data dump and five sentences is a specification (PRD §46). Level four
    // rather than level three: these are five of the longest lines on the page and the ones read
    // least often, and leaving them in the hardware column sets the height of the statistics for
    // every other resource as well (PRD §45.2).
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
  /// "8 performance, 8 efficiency" — the two halves of a hybrid part, counted in logical processors.
  /// </summary>
  private static string CoreKinds(CpuTopology topology) {
    var performance = 0;
    var efficiency = 0;
    foreach (var core in topology.Cores)
      switch (core.Kind) {
        case CoreKind.Performance: ++performance; break;
        case CoreKind.Efficiency: ++efficiency; break;
        default: break;
      }

    return $"{performance.ToString(CultureInfo.InvariantCulture)} performance, {efficiency.ToString(CultureInfo.InvariantCulture)} efficiency";
  }

  /// <summary>
  /// The machine's own bookkeeping: switches, interrupts and deferred work (PRD §46).
  /// </summary>
  /// <remarks>
  /// Level four rather than beside the utilisation, which is where §46 puts them: these are
  /// diagnostics and not status, and a reader glancing at the page is asking how busy the processor
  /// is rather than how many times a second it changed its mind.
  /// <para>
  /// Each is the rate with its cumulative total beside it, because the two answer different
  /// questions — the rate says what the machine is doing now, and the total says how much of it the
  /// machine has done since it booted, which is the figure a second reading is differenced against.
  /// </para>
  /// <para>
  /// The interrupt shares are already inside the kernel time above and are broken out for the one
  /// question kernel time cannot answer: whether the kernel is in there for a process or for a
  /// device. A machine at 30 % kernel of which 25 is soft IRQ is an adapter drowning it, not a
  /// program doing system calls.
  /// </para>
  /// </remarks>
  private static void AddProcessorCounters(List<PerformanceRow> rows, SystemSnapshot snapshot, SnapshotDelta? delta) {
    void Add(string label, string value) => rows.Add(new(label, value, PerformanceRowLevel.Diagnostic));

    var system = snapshot.System;
    Add("Context switches", Churn(delta?.SystemContextSwitchesPerSecond, system.ContextSwitches));
    Add("Interrupts", Churn(delta?.SystemInterruptsPerSecond, system.Interrupts));
    // What Windows calls a DPC. The name is this machine's rather than that one's, with the
    // equivalence in the tooltip's place — the heading — instead of in the figure (PRD §5.3).
    Add("Soft interrupts (deferred)", Churn(delta?.SystemSoftInterruptsPerSecond, system.SoftInterrupts));
    Add("Interrupt time", $"{Percent(delta?.SystemInterruptPercent)} hard, {Percent(delta?.SystemSoftInterruptPercent)} deferred");
    // Linux keeps no machine-wide system-call counter. It can be had per process with ptrace or a
    // BPF probe, and both are far more intrusive than a performance page has any business being —
    // so this is refused rather than approximated from the context-switch count, which is a
    // different number that happens to move at a similar speed (PRD §5.3, §46).
    Add("System calls", Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform));
    Add("Descriptor limit", DescriptorCeiling(system.DescriptorLimit));
    Add("Forks", Humanize.Count(system.ProcessesCreated));
  }

  /// <summary>A rate with the counter it was differenced from: "12.4k/s  (3.52G since boot)".</summary>
  private static string Churn(Rate? perSecond, Counter total) {
    var rate = Humanize.Rate(perSecond ?? Rate.NotSampledYet);
    return total.HasValue ? $"{rate}/s  ({Humanize.Rate(Rate.Of(total.Value))} since boot)" : rate + "/s";
  }

  /// <summary>
  /// The kernel's ceiling on open descriptors, where it is one worth printing.
  /// </summary>
  /// <remarks>
  /// <c>fs.file-max</c> is derived from how much memory the machine has and on anything modern is
  /// nine quintillion, which is not a limit anybody is going to reach and reads as a typographical
  /// error next to a five-figure count. Above a billion it is reported as unlimited in words rather
  /// than as a number nobody can use.
  /// </remarks>
  private static string DescriptorCeiling(Counter limit) {
    if (!limit.HasValue)
      return Humanize.Placeholder(limit.Reason);

    return limit.Value > 1_000_000_000 ? "no practical limit" : Humanize.Count(limit);
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
      rows.Add(new(label, string.Join(", ", names), Level: PerformanceRowLevel.Diagnostic));
  }

  /// <summary>
  /// What a section holding a NUMA node is grouped under.
  /// </summary>
  /// <remarks>
  /// Not <c>Processor</c>, which the cores already use: a front-end asking for the processor's parts
  /// wants one of the two lists and never both interleaved, and the two views are a choice rather
  /// than a sum (PRD §46).
  /// </remarks>
  public const string NodeGroup = "Processor nodes";

  /// <summary>
  /// One section per NUMA node, where the machine has more than one (PRD §46).
  /// </summary>
  /// <remarks>
  /// Aggregated from the per-core figures rather than read separately, because the kernel publishes
  /// no per-node CPU line: a node's utilisation is the mean of its processors', which is the same
  /// arithmetic the machine-wide figure is.
  /// <para>
  /// Only where there is more than one node. "Node 0 is the whole machine" is not a distribution,
  /// and offering a per-node view of it would put a second copy of the processor's own graph on the
  /// page (§47 makes the same argument about per-node memory).
  /// </para>
  /// </remarks>
  private static void AddNodes(List<PerformanceSection> sections, SnapshotDelta? delta, CpuTopology? topology) {
    if (delta is null || topology is null)
      return;

    var nodes = topology.Nodes;
    if (nodes.Count < 2)
      return;

    foreach (var node in nodes) {
      var members = topology.OnNode(node);
      var busy = MeanOf(members, delta.PerCoreBusyPercent);
      sections.Add(new(
        $"Node {node.ToString(CultureInfo.InvariantCulture)}",
        BuildNode(node, members, delta),
        busy,
        100,
        Percent(busy),
        MeanOf(members, delta.PerCoreKernelPercent),
        "kernel",
        PartOf: NodeGroup
      ));
    }
  }

  private static PerformanceRow[] BuildNode(int node, IReadOnlyList<CoreDescriptor> members, SnapshotDelta delta) {
    var rows = new List<PerformanceRow> {
      new("NUMA node", node.ToString(CultureInfo.InvariantCulture)),
      new("Utilisation", Percent(MeanOf(members, delta.PerCoreBusyPercent))),
      new("User time", Percent(MeanOf(members, delta.PerCoreUserPercent))),
      new("Kernel time", Percent(MeanOf(members, delta.PerCoreKernelPercent))),
      new("Logical processors", members.Count.ToString(CultureInfo.InvariantCulture), PerformanceRowLevel.Hardware),
    };

    // Which processors they are, so somebody about to pin a thread to this node knows what to pin it
    // to. Ranges rather than a list of forty numbers.
    if (members.Count > 0)
      rows.Add(new("Processors", Ranges(members), PerformanceRowLevel.Hardware));

    return [.. rows];
  }

  /// <summary>
  /// The mean of one reading across a group of processors.
  /// </summary>
  /// <remarks>
  /// Unknown when every member is, rather than nought: a node whose cores have not been sampled yet
  /// has no utilisation, and averaging no readings into a zero would draw an idle node (PRD §5.3).
  /// Members that are individually unreadable are left out of the mean rather than counted as idle —
  /// a hot-unplugged core must not halve the node's figure.
  /// </remarks>
  private static Rate MeanOf(IReadOnlyList<CoreDescriptor> members, Func<int, Rate> reading) {
    var total = 0d;
    var counted = 0;
    var reason = UnknownReason.NotSampledYet;
    foreach (var member in members) {
      var value = reading(member.Logical);
      if (!value.HasValue) {
        reason = value.Reason;
        continue;
      }

      total += value.Value;
      ++counted;
    }

    return counted > 0 ? Rate.Of(total / counted) : Rate.Unknown(reason);
  }

  /// <summary>"0-7, 16-23" — a run of processor numbers written the way the kernel writes them.</summary>
  private static string Ranges(IReadOnlyList<CoreDescriptor> members) {
    var numbers = new List<int>(members.Count);
    foreach (var member in members)
      numbers.Add(member.Logical);

    numbers.Sort();
    var text = new System.Text.StringBuilder();
    for (var i = 0; i < numbers.Count;) {
      var start = i;
      while (i + 1 < numbers.Count && numbers[i + 1] == numbers[i] + 1)
        ++i;

      if (text.Length > 0)
        text.Append(", ");

      text.Append(numbers[start].ToString(CultureInfo.InvariantCulture));
      if (i > start)
        text.Append('-').Append(numbers[i].ToString(CultureInfo.InvariantCulture));

      ++i;
    }

    return text.ToString();
  }

  /// <summary>
  /// One logical processor's own figures, and where it sits (PRD §46).
  /// </summary>
  /// <remarks>
  /// User and kernel do not add up to busy, and deliberately: steal time is busy from this machine's
  /// point of view and belongs to neither, so a virtual machine losing a third of its core to the
  /// hypervisor shows it as the gap rather than hiding it in one of the two.
  /// <para>
  /// The placement rows are what turn a number into a reading: two logical processors sharing one
  /// physical core do not do twice the work of one, and a saturated efficiency core beside an idle
  /// performance core is a scheduling problem rather than a busy machine. A machine that publishes
  /// no topology gets no such rows rather than a column of unknowns (§5.3).
  /// </para>
  /// </remarks>
  private static PerformanceRow[] BuildCore(int core, SnapshotDelta delta, CpuTopology? topology) {
    var rows = new List<PerformanceRow> {
      new("Logical processor", core.ToString(CultureInfo.InvariantCulture)),
      new("Utilisation", Percent(delta.PerCoreBusyPercent(core))),
      new("User time", Percent(delta.PerCoreUserPercent(core))),
      new("Kernel time", Percent(delta.PerCoreKernelPercent(core))),
    };

    if (Placement(topology, core) is not { } placement)
      return [.. rows];

    if (placement.Package >= 0)
      rows.Add(new("Socket", placement.Package.ToString(CultureInfo.InvariantCulture), PerformanceRowLevel.Hardware));

    if (placement.Core >= 0) {
      rows.Add(new("Physical core", placement.Core.ToString(CultureInfo.InvariantCulture), PerformanceRowLevel.Hardware));
      var siblings = SiblingsOf(topology!, placement);
      if (siblings.Length > 0)
        rows.Add(new("SMT sibling", siblings, PerformanceRowLevel.Hardware));
    }

    if (placement.Kind != CoreKind.Unknown)
      rows.Add(new("Kind", placement.Kind == CoreKind.Performance ? "performance" : "efficiency", PerformanceRowLevel.Hardware));

    if (placement.Node >= 0)
      rows.Add(new("NUMA node", placement.Node.ToString(CultureInfo.InvariantCulture), PerformanceRowLevel.Hardware));

    return [.. rows];
  }

  private static CoreDescriptor? Placement(CpuTopology? topology, int logical) {
    foreach (var core in topology?.Cores ?? [])
      if (core.Logical == logical)
        return core;

    return null;
  }

  /// <summary>
  /// The other logical processors on the same physical core, which is what SMT means here.
  /// </summary>
  /// <remarks>
  /// Empty on a part with no SMT, and empty rather than "none": a row saying a core has no sibling
  /// on a machine where nothing does is a row on every page for no reason.
  /// </remarks>
  private static string SiblingsOf(CpuTopology topology, CoreDescriptor of) {
    var siblings = new List<int>();
    foreach (var core in topology.Cores)
      if (core.Package == of.Package && core.Core == of.Core && core.Logical != of.Logical)
        siblings.Add(core.Logical);

    if (siblings.Count == 0)
      return string.Empty;

    siblings.Sort();
    var text = new System.Text.StringBuilder();
    foreach (var sibling in siblings) {
      if (text.Length > 0)
        text.Append(", ");

      text.Append(sibling.ToString(CultureInfo.InvariantCulture));
    }

    return text.ToString();
  }

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
        "gpu",
        PerformanceUnit.Bytes
      ));

    if (gpu.MemoryBusyPercent.HasValue)
      graphs.Add(new("Memory bus", AsRate(gpu.MemoryBusyPercent), 100, AsPercent(gpu.MemoryBusyPercent), "gpu"));

    if (gpu.PowerMicrowatts.HasValue)
      graphs.Add(new(
        "Power",
        Rate.Of(gpu.PowerMicrowatts.Value / 1_000_000d),
        gpu.PowerLimitMicrowatts.HasValue ? gpu.PowerLimitMicrowatts.Value / 1_000_000d : 0,
        PowerDraw(gpu.PowerMicrowatts, gpu.PowerLimitMicrowatts),
        "power",
        PerformanceUnit.Watts
      ));

    if (gpu.TemperatureMilliCelsius.HasValue)
      graphs.Add(new(
        "Temperature",
        Rate.Of(gpu.TemperatureMilliCelsius.Value / 1000d),
        // A fixed hundred rather than the hottest yet: a card idling between 40 and 42 °C would
        // otherwise fill its graph and look alarming (PRD §45.4).
        100,
        Celsius(gpu.TemperatureMilliCelsius),
        "temperature",
        PerformanceUnit.Celsius
      ));

    // The video engines, as one plot with two lines: a card that is transcoding uses both, and the
    // pair against each other is what says whether it is encoding, decoding or doing both.
    if (gpu.EncodePercent.HasValue || gpu.DecodePercent.HasValue)
      graphs.Add(new(
        "Video engines",
        AsRate(gpu.EncodePercent),
        100,
        $"{AsPercent(gpu.EncodePercent)} encode, {AsPercent(gpu.DecodePercent)} decode",
        "gpu",
        PerformanceUnit.Percent,
        Companion: AsRate(gpu.DecodePercent),
        CompanionLabel: "decode",
        SeriesLabel: "encode"
      ));

    if (gpu.FanPercent.HasValue)
      graphs.Add(new("Fan", AsRate(gpu.FanPercent), 100, FanSpeed(gpu), "fan"));

    return [.. graphs];
  }

  private static Rate AsRate(Counter counter)
    => counter.HasValue ? Rate.Of(counter.Value) : Rate.Unknown(counter.Reason);

  /// <summary>
  /// A battery: what it is doing, then what it is (PRD §45.3).
  /// </summary>
  /// <remarks>
  /// Health is a hardware fact rather than a live one even though it is computed from two live
  /// figures: it answers "is this pack worn out", which nobody asks twice a second.
  /// </remarks>
  private static PerformanceRow[] BuildBattery(BatteryInfo battery) {
    var rows = new List<PerformanceRow> {
      new("Charge", AsPercent(battery.ChargePercent)),
      // The driver's own word where it used one, because a driver that says something outside the
      // documented five knows something about its hardware that a fixed list does not.
      new("State", battery.StateText ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)),
      new("Supply", battery.OnExternalPower ? "mains" : "battery"),
      new("Draw", Watts(battery.PowerMicrowatts)),
      new("Remaining", Remaining(battery)),
      new("Energy", WattHours(battery.EnergyNowMicrowattHours)),
      new("Voltage", Volts(battery.VoltageMicrovolts)),
      new("Health", AsPercent(battery.HealthPercent), Level: PerformanceRowLevel.Hardware),
      new("Full charge", WattHours(battery.EnergyFullMicrowattHours), Level: PerformanceRowLevel.Hardware),
      new("Design charge", WattHours(battery.EnergyDesignMicrowattHours), Level: PerformanceRowLevel.Hardware),
      new("Cycles", Humanize.Count(battery.CycleCount), Level: PerformanceRowLevel.Hardware),
      new("Chemistry", battery.Technology ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), Level: PerformanceRowLevel.Hardware),
      new("Manufacturer", battery.Manufacturer ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), Level: PerformanceRowLevel.Hardware),
      new("Model", battery.Model ?? battery.Name, Level: PerformanceRowLevel.Hardware),
    };

    return [.. rows];
  }

  /// <summary>
  /// Every channel a sensor chip publishes, in its own units.
  /// </summary>
  /// <remarks>
  /// A chip's channels are all live readings; none of them describes the hardware, and a chip with
  /// forty voltage rails would bury the two temperatures somebody opened the page for — so the
  /// temperatures and fans come first and the electrical channels go into the collapsed block.
  /// </remarks>
  private static PerformanceRow[] BuildSensors(SensorGroup group) {
    var rows = new List<PerformanceRow>();
    foreach (var reading in group.Readings)
      if (reading.Kind is SensorKind.Temperature or SensorKind.Fan)
        rows.Add(new(reading.Label, SensorValue(reading)));

    foreach (var reading in group.Readings)
      if (reading.Kind is not (SensorKind.Temperature or SensorKind.Fan))
        rows.Add(new(reading.Label, SensorValue(reading), Level: PerformanceRowLevel.Diagnostic));

    return [.. rows];
  }

  private static string SensorValue(SensorReading reading) => reading.Kind switch {
    SensorKind.Temperature => Celsius(reading.Value),
    SensorKind.Fan => reading.Value.HasValue
      ? string.Format(CultureInfo.InvariantCulture, "{0} rpm", reading.Value.Value)
      : Humanize.Placeholder(reading.Value.Reason),
    // Millivolts and milliamps here, where the battery's own attributes are micro. The chip and the
    // power supply publish the same rail in units a thousand apart.
    SensorKind.Voltage => reading.Value.HasValue
      ? string.Format(CultureInfo.InvariantCulture, "{0:0.00} V", reading.Value.Value / 1_000d)
      : Humanize.Placeholder(reading.Value.Reason),
    SensorKind.Current => reading.Value.HasValue
      ? string.Format(CultureInfo.InvariantCulture, "{0:0.000} A", reading.Value.Value / 1_000d)
      : Humanize.Placeholder(reading.Value.Reason),
    SensorKind.Power => Watts(reading.Value),
    _ => Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
  };

  /// <summary>
  /// The hottest thing a chip reports, for the rail.
  /// </summary>
  /// <remarks>
  /// A chip with no temperature at all — a fan controller, a voltage monitor — measures nothing this
  /// can summarise, and says so rather than plotting a line along the floor (PRD §72.3).
  /// </remarks>
  private static Rate Hottest(SensorGroup group) {
    var hottest = Rate.Unknown(UnknownReason.NotSupportedOnPlatform);
    foreach (var reading in group.Readings) {
      if (reading.Kind != SensorKind.Temperature || !reading.Value.HasValue)
        continue;

      var degrees = reading.Value.Value / 1000d;
      if (!hottest.HasValue || degrees > hottest.Value)
        hottest = Rate.Of(degrees);
    }

    return hottest;
  }

  private static string BatteryDetail(BatteryInfo battery) {
    if (!battery.ChargePercent.HasValue)
      return string.Empty;

    var charge = string.Format(CultureInfo.InvariantCulture, "{0} %", battery.ChargePercent.Value);
    return battery.OnExternalPower ? charge + ", mains" : charge;
  }

  /// <summary>
  /// How long the battery has left, in words a reader can act on.
  /// </summary>
  /// <remarks>
  /// Nothing at all while the machine is plugged in and full: "0:00 remaining" on a full battery is
  /// alarming and wrong, and there is genuinely no answer to give.
  /// </remarks>
  private static string Remaining(BatteryInfo battery) {
    if (battery.HoursRemaining is not { } hours)
      return Humanize.Placeholder(
        battery.State == ChargeState.Full ? UnknownReason.NotSupportedOnPlatform : UnknownReason.NotSampledYet
      );

    var whole = (int)hours;
    var minutes = (int)Math.Round((hours - whole) * 60);
    return string.Format(
      CultureInfo.InvariantCulture,
      "{0}:{1:00} {2}",
      whole,
      minutes,
      battery.State == ChargeState.Charging ? "to full" : "remaining"
    );
  }

  private static string WattHours(Counter microwattHours)
    => microwattHours.HasValue
      ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} Wh", microwattHours.Value / 1_000_000d)
      : Humanize.Placeholder(microwattHours.Reason);

  private static string Volts(Counter microvolts)
    => microvolts.HasValue
      ? string.Format(CultureInfo.InvariantCulture, "{0:0.00} V", microvolts.Value / 1_000_000d)
      : Humanize.Placeholder(microvolts.Reason);

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
      new("Fan", FanSpeed(gpu)),
      // The two engines a driver will name apart from the shaders. Shown together because a card
      // that is transcoding is using both, and either alone reads as an idle video block.
      new("Video engines", $"{AsPercent(gpu.EncodePercent)} encode, {AsPercent(gpu.DecodePercent)} decode"),
      new("Adapter", gpu.Model ?? gpu.Name, Level: PerformanceRowLevel.Hardware),
      new("Driver", gpu.Driver ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform), Level: PerformanceRowLevel.Hardware),
      new("Dedicated memory", Humanize.Bytes(gpu.MemoryTotalBytes), Level: PerformanceRowLevel.Hardware),
      new("Power cap", Watts(gpu.PowerCapMicrowatts), Level: PerformanceRowLevel.Hardware),
    };

    if (gpu.PowerState is { } state)
      rows.Add(new("Power state", state, Level: PerformanceRowLevel.Hardware));

    // Only where the card says: nought fans is a fact about a laptop card and worth a row, and an
    // unknown count is a driver that was never asked and is not (PRD §5.3).
    if (gpu.FanCount.HasValue)
      rows.Add(new("Fans", Humanize.Count(gpu.FanCount), Level: PerformanceRowLevel.Hardware));

    // Shared memory — what a card borrows from the machine's own — is what Task Manager's second
    // memory figure is. Nothing on Linux publishes it: NVML's BAR1 is an aperture rather than an
    // allocation, and the DRM accounting is per client. Refused rather than approximated (PRD §50.1).
    rows.Add(new("Shared memory", Humanize.Placeholder(UnknownReason.NotImplementedHere), Level: PerformanceRowLevel.Hardware));

    return [.. rows];
  }

  /// <summary>
  /// The fan, in whichever units the card publishes — and both where it publishes both.
  /// </summary>
  /// <remarks>
  /// A percentage is a duty cycle and revolutions are a measurement, and neither can be computed
  /// from the other: nothing publishes the speed a fan turns at when it is driven flat out. A card
  /// with no fan of its own says so, which is a different sentence from a fan nobody can read.
  /// </remarks>
  private static string FanSpeed(GpuInfo gpu) {
    if (gpu.FanCount is { HasValue: true, Value: 0 })
      return "none";

    var percent = AsPercent(gpu.FanPercent);
    if (!gpu.FanRpm.HasValue)
      return percent;

    var revolutions = string.Format(CultureInfo.InvariantCulture, "{0} rpm", gpu.FanRpm.Value);
    return gpu.FanPercent.HasValue ? $"{percent}  ({revolutions})" : revolutions;
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
  /// The memory page's series (PRD §47).
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
  /// <para>
  /// Swap and cache are the two that say which way a machine is going. Cache falling while physical
  /// memory stays put is the kernel giving up its cache to something; swap rising after that is the
  /// point where it has run out of cache to give. Neither is visible in the physical-memory series,
  /// which stays pinned near the top through all of it.
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
        "memory",
        PerformanceUnit.Bytes
      ),
    };

    if (system.CommittedBytes.HasValue)
      graphs.Add(new(
        "Committed",
        Rate.Of(system.CommittedBytes.Value),
        system.CommitLimitBytes.HasValue ? system.CommitLimitBytes.Value : 0,
        Pair(system.CommittedBytes, system.CommitLimitBytes),
        "memory",
        PerformanceUnit.Bytes
      ));

    // The file cache, on the machine's own scale so it can be read against the physical series
    // directly above it. Buffers included, because the split between them is a kernel-internal
    // detail and the question here is how much memory the cache is holding.
    var cache = Sum(AsRate(system.CachedMemoryBytes), AsRate(system.BufferMemoryBytes));
    if (cache.HasValue)
      graphs.Add(new(
        "Cache",
        cache,
        total,
        $"{Humanize.Bytes(Counter.Of((ulong)cache.Value))} of {Humanize.Bytes(system.TotalMemoryBytes)}",
        "memory",
        PerformanceUnit.Bytes
      ));

    // Only where there is somewhere to swap to. A machine with no swap device gets no swap graph
    // rather than a flat line at the floor of a scale of zero — the graph would be drawing the
    // absence of a device as an idle one (PRD §45.6).
    if (system.TotalSwapBytes is { HasValue: true, Value: > 0 } swap)
      graphs.Add(new(
        "Swap",
        AsRate(system.UsedSwapBytes),
        swap.Value,
        Pair(system.UsedSwapBytes, system.TotalSwapBytes),
        "memory",
        PerformanceUnit.Bytes
      ));

    return [.. graphs];
  }

  /// <summary>
  /// The memory page's figures (PRD §47).
  /// </summary>
  /// <remarks>
  /// Three levels, in the order §45.2 puts them. The measurements a reader came for are first; what
  /// the sticks are is beside them; and the twenty-odd figures that answer "why" — the reclaim
  /// lists, the pools, the huge pages — are marked as diagnostics and collapsed, because all of them
  /// at once is what turns a page into a wall.
  /// <para>
  /// <b>Free is not available and available is not free.</b> A healthy machine keeps almost nothing
  /// free, because it caches with the rest and hands that cache back the moment anything asks. The
  /// two rows sit next to each other for exactly that reason.
  /// </para>
  /// </remarks>
  private static PerformanceRow[] BuildMemory(HostInfo host, SystemSnapshot snapshot) {
    var system = snapshot.System;
    var used = system.TotalMemoryBytes.HasValue && system.AvailableMemoryBytes.HasValue
      ? Counter.Of(system.TotalMemoryBytes.Value - Math.Min(system.AvailableMemoryBytes.Value, system.TotalMemoryBytes.Value))
      : Counter.Unknown(system.TotalMemoryBytes.HasValue ? system.AvailableMemoryBytes.Reason : system.TotalMemoryBytes.Reason);

    var rows = new List<PerformanceRow> {
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
    };

    // Only on a machine that compresses. Where it does, the pair is the whole point: a gigabyte
    // holding two and a half is a machine that has saved itself the difference in swapping.
    if (system.CompressedBytes.HasValue)
      rows.Add(new("Compressed", Compressed(system.CompressedBytes, system.CompressedOriginalBytes)));

    rows.Add(new("Stalled on memory", Pressure(system.MemoryPressure.Some)));
    // Full pressure is the serious one: not "something waited" but "nothing ran at all". More than a
    // few percent of it means the machine is thrashing rather than busy.
    rows.Add(new("Stalled completely", Pressure(system.MemoryPressure.Full)));

    // What the machine is, on the right. "Installed" is a firmware fact and "usable" is a kernel
    // one, and they are not the same number: the difference is what the firmware kept for itself.
    // Nothing readable without root says what is installed, so the difference is refused rather
    // than guessed — a hardware-reserved figure of zero would be a claim, not a reading (PRD §47).
    rows.Add(new("Installed", Humanize.Bytes(host.InstalledMemoryBytes), Level: PerformanceRowLevel.Hardware));
    rows.Add(new("Usable", Humanize.Bytes(system.TotalMemoryBytes), Level: PerformanceRowLevel.Hardware));
    rows.Add(new("Hardware reserved", Humanize.Bytes(Reserved(host.InstalledMemoryBytes, system.TotalMemoryBytes)), Level: PerformanceRowLevel.Hardware));
    rows.Add(new("Speed", Transfers(host.MemoryTransfersPerSecond), Level: PerformanceRowLevel.Hardware));
    rows.Add(new("Channels", Humanize.Count(host.MemoryChannels), Level: PerformanceRowLevel.Hardware));
    rows.Add(new("Form factor", host.MemoryFormFactor ?? Humanize.Placeholder(UnknownReason.NotPermitted), Level: PerformanceRowLevel.Hardware));
    rows.Add(new("Slots used", Slots(host.MemorySlotsUsed, host.MemorySlotsTotal), Level: PerformanceRowLevel.Hardware));

    // How the memory is spread across the nodes, where there is more than one of them. A single-node
    // machine gets no rows: "node 0 has all of it" is not a distribution (PRD §47).
    if (host.NumaMemoryBytes.Count > 1)
      for (var node = 0; node < host.NumaMemoryBytes.Count; ++node)
        rows.Add(new($"Node {node}", Humanize.Bytes(host.NumaMemoryBytes[node]), Level: PerformanceRowLevel.Hardware));

    AddMemoryDiagnostics(rows, system);
    return [.. rows];
  }

  /// <summary>
  /// The collapsed block: what the kernel is doing with the memory, rather than how much is gone
  /// (PRD §45.2 level 4, §47).
  /// </summary>
  /// <remarks>
  /// Paired onto single lines wherever two figures only mean something together — active against
  /// inactive, dirty against in-writeback, reclaimable slab against fixed. Twenty rows of one number
  /// each is a data dump; ten rows of a comparison is a diagnosis.
  /// </remarks>
  private static void AddMemoryDiagnostics(List<PerformanceRow> rows, in SystemCounters system) {
    void Add(string label, string value) => rows.Add(new(label, value, PerformanceRowLevel.Diagnostic));

    // Anonymous against file-backed is the split that decides what happens under pressure: file
    // pages can be dropped and read back, anonymous ones can only be compressed or swapped, and on
    // a machine with no swap they cannot go anywhere at all.
    Add("Anonymous", Humanize.Bytes(system.AnonymousBytes));
    Add("Mapped", Humanize.Bytes(system.MappedBytes));
    Add("Anonymous, by list", Lists(system.ActiveAnonymousBytes, system.InactiveAnonymousBytes));
    Add("File, by list", Lists(system.ActiveFileBytes, system.InactiveFileBytes));
    // The two halves of "modified": what has not started moving, and what is moving now.
    Add("Dirty", Humanize.Bytes(system.DirtyBytes));
    Add("In writeback", Humanize.Bytes(system.WritebackBytes));
    Add("Swap cached", Humanize.Bytes(system.SwapCachedBytes));
    // Linux's answer to the paged and non-paged pools: what the kernel can hand back under pressure
    // and what it cannot. Named for what they are here rather than for what Windows calls them —
    // the equivalence is real but the words are not this machine's (PRD §5.3, §47).
    Add("Kernel, reclaimable", Humanize.Bytes(system.ReclaimableKernelBytes));
    Add("Kernel, fixed", Humanize.Bytes(system.UnreclaimableKernelBytes));
    Add("Slab, total", Humanize.Bytes(system.SlabBytes));
    Add("Page tables", Humanize.Bytes(system.PageTableBytes));
    Add("Kernel stacks", Humanize.Bytes(system.KernelStackBytes));
    Add("Per-CPU", Humanize.Bytes(system.PerCpuBytes));
    Add("Vmalloc used", Humanize.Bytes(system.VmallocUsedBytes));
    // Pages that cannot be reclaimed at all, and the part of them somebody asked for by name.
    Add("Unevictable", Humanize.Bytes(system.UnevictableBytes));
    Add("Locked", Humanize.Bytes(system.LockedBytes));
    // Reserved huge pages are gone from every other figure on this page the moment they are
    // reserved, whether or not anything has ever touched them.
    Add("Huge pages", HugePages(in system));
    Add("Transparent huge", Transparent(in system));
    // Almost always zero, and worth a row for exactly that reason: anything else is a failing DIMM.
    Add("Hardware corrupted", Humanize.Bytes(system.HardwareCorruptedBytes));
  }

  /// <summary>"1.1G holding 2.4G  (2.2×)" — the pool, what is in it, and whether it is worth it.</summary>
  private static string Compressed(Counter pool, Counter original) {
    if (!pool.HasValue)
      return Humanize.Placeholder(pool.Reason);

    if (!original.HasValue)
      return Humanize.Bytes(pool);

    var ratio = pool.Value > 0
      ? string.Format(CultureInfo.InvariantCulture, "  ({0:0.0}×)", (double)original.Value / pool.Value)
      : string.Empty;

    return $"{Humanize.Bytes(pool)} holding {Humanize.Bytes(original)}{ratio}";
  }

  /// <summary>"29.3G active, 4.7G inactive" — the reclaim lists, which only mean anything as a pair.</summary>
  private static string Lists(Counter active, Counter inactive)
    => active.HasValue || inactive.HasValue
      ? $"{Humanize.Bytes(active)} active, {Humanize.Bytes(inactive)} inactive"
      : Humanize.Placeholder(active.Reason);

  /// <summary>"0 of 0 reserved, 2.0M each", or the reason this kernel has no such thing.</summary>
  private static string HugePages(in SystemCounters system) {
    if (!system.HugePagesTotal.HasValue)
      return Humanize.Placeholder(system.HugePagesTotal.Reason);

    var each = system.HugePageSizeBytes.HasValue ? $", {Humanize.Bytes(system.HugePageSizeBytes)} each" : string.Empty;
    var used = system.HugePagesFree.HasValue
      ? Counter.Of(system.HugePagesTotal.Value - Math.Min(system.HugePagesFree.Value, system.HugePagesTotal.Value))
      : Counter.Unknown(system.HugePagesFree.Reason);

    return $"{Humanize.Count(used)} of {Humanize.Count(system.HugePagesTotal)} in use{each}";
  }

  /// <summary>The huge pages the kernel arranged by itself, which are not reserved out of anything.</summary>
  private static string Transparent(in SystemCounters system)
    => system.AnonymousHugePagesBytes.HasValue
      ? $"{Humanize.Bytes(system.AnonymousHugePagesBytes)} anonymous, {Humanize.Bytes(system.SharedHugePagesBytes)} shared, {Humanize.Bytes(system.FileHugePagesBytes)} file"
      : Humanize.Placeholder(system.AnonymousHugePagesBytes.Reason);

  /// <summary>
  /// What the firmware kept: installed less usable.
  /// </summary>
  /// <remarks>
  /// Unknown unless both halves are, and unknown rather than zero when they are equal in a way that
  /// cannot be right — a subtraction of two figures one of which nobody read is not a small number,
  /// it is not a number (PRD §5.3).
  /// </remarks>
  private static Counter Reserved(Counter installed, Counter usable) {
    if (!installed.HasValue)
      return Counter.Unknown(installed.Reason);

    if (!usable.HasValue)
      return Counter.Unknown(usable.Reason);

    return installed.Value >= usable.Value
      ? Counter.Of(installed.Value - usable.Value)
      : Counter.Unknown(UnknownReason.CounterInvalid);
  }

  /// <summary>
  /// "28.9G of 126G" — a figure against its ceiling, or just the figure when there is no ceiling to
  /// put it against.
  /// </summary>
  /// <summary>
  /// The three windows on one line — "37.8 % · 55.8 % · 48.4 %" for ten seconds, a minute and five.
  /// </summary>
  /// <remarks>
  /// All three together because the shape between them is the information: ten above sixty is a
  /// spike starting, ten below sixty is one ending, and all three alike is a machine that has been
  /// like this for a while.
  /// </remarks>
  private static string Pressure(PressureShare share) {
    if (!share.HasValue)
      return Humanize.Placeholder(share.Average10.Reason);

    return $"{Percent(share.Average10)} · {Percent(share.Average60)} · {Percent(share.Average300)}";
  }

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
