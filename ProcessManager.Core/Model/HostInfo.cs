namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// What the machine is, as opposed to what it is doing: the facts that do not change between
/// samples (PRD §46, §47, §96).
/// </summary>
/// <remarks>
/// Read once and cached, not per sample. None of it moves — a processor does not change its cache
/// size while the program is running — and several of the reads are expensive enough that doing
/// them every second would be indefensible against §71. The one exception is
/// <see cref="CpuCurrentHertz"/>, which is a live reading and is refreshed on demand.
/// </remarks>
public sealed record HostInfo {

  public string HostName { get; init; } = string.Empty;

  /// <summary>The OS as a person would name it: "Arch Linux", "Windows 11 Pro".</summary>
  public string OperatingSystem { get; init; } = string.Empty;

  /// <summary>Kernel or build version.</summary>
  public string OperatingSystemVersion { get; init; } = string.Empty;

  /// <summary>x86-64, ARM64, and so on.</summary>
  public string Architecture { get; init; } = string.Empty;

  #region processor

  /// <summary>The marketing name, e.g. "11th Gen Intel(R) Core(TM) i9-11950H @ 2.60GHz".</summary>
  public string? CpuModel { get; init; }

  public string? CpuVendor { get; init; }

  /// <summary>
  /// Family, model and stepping, as <c>CPUID</c> encodes them — which silicon this is, as opposed to
  /// what it is called (PRD §46).
  /// </summary>
  public string? CpuSignature { get; init; }

  /// <summary>
  /// What the processor reports it can do.
  /// </summary>
  /// <remarks>
  /// Carried on the host record rather than read where it is rendered, so a report built from a
  /// recorded machine describes that machine and not the one running the program. Reading
  /// <c>CPUID</c> inside the renderer made <c>--probe-root</c> replay show this laptop's feature
  /// list beside a fixture's core count, which is two machines in one table (PRD §9.4).
  /// </remarks>
  public IReadOnlyList<Query.CpuFeature> CpuFeatures { get; init; } = [];

  /// <summary>
  /// The rated speed, which is the number on the box and the one Task Manager calls "Base speed".
  /// </summary>
  public Counter CpuBaseHertz { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>
  /// What the cores are actually running at, averaged. Moves constantly on any modern part, which is
  /// why it is separate from <see cref="CpuBaseHertz"/> rather than replacing it.
  /// </summary>
  public Counter CpuCurrentHertz { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>Physical packages — two on a dual-socket board, one on anything portable.</summary>
  public Counter Sockets { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>Real cores, which is not the same as the thread count on anything with SMT.</summary>
  public Counter PhysicalCores { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>What the scheduler sees, and what the per-core meters count.</summary>
  public Counter LogicalProcessors { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  public Counter NumaNodes { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>Per-core data and instruction caches, and the shared ones.</summary>
  public Counter L1DataBytes { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);
  public Counter L1InstructionBytes { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);
  public Counter L2Bytes { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);
  public Counter L3Bytes { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>Whether the machine is virtualised, when that can be told at all.</summary>
  public string? Virtualisation { get; init; }

  #endregion

  #region memory

  public Counter TotalMemoryBytes { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>
  /// What is physically in the machine, which is not what the kernel can use.
  /// </summary>
  /// <remarks>
  /// The firmware keeps some of every machine — the framebuffer on an integrated part, the ACPI
  /// tables, whatever the platform reserved before the kernel was loaded — and the difference
  /// between this and the usable total is what Task Manager calls hardware-reserved. It is a
  /// firmware fact and comes from the same root-only SMBIOS tables as the three below, so
  /// unelevated it is <see cref="UnknownReason.NotPermitted"/> and the reserved figure is refused
  /// rather than computed as zero (PRD §47).
  /// </remarks>
  public Counter InstalledMemoryBytes { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>
  /// How much memory each NUMA node has, node 0 first.
  /// </summary>
  /// <remarks>
  /// The distribution rather than the count: two nodes with half each and two nodes with all of it
  /// on one are very different machines to run a thread on, and <see cref="NumaNodes"/> cannot tell
  /// them apart. Empty where the kernel was built without NUMA.
  /// </remarks>
  public IReadOnlyList<Counter> NumaMemoryBytes { get; init; } = [];

  /// <summary>
  /// Transfer rate of the installed modules — the "4800 MT/s" Task Manager shows.
  /// </summary>
  /// <remarks>
  /// This and the three below come from the firmware's SMBIOS tables, which on Linux are readable
  /// only by root. Unelevated they are <see cref="UnknownReason.NotPermitted"/>, which is the honest
  /// answer and not a zero (PRD §47).
  /// </remarks>
  public Counter MemoryTransfersPerSecond { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>DIMM, SODIMM, and so on.</summary>
  public string? MemoryFormFactor { get; init; }

  public Counter MemorySlotsUsed { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);
  public Counter MemorySlotsTotal { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  /// <summary>How many channels the modules are spread over — two on most desktops, eight on a server.</summary>
  public Counter MemoryChannels { get; init; } = Counter.Unknown(UnknownReason.NotSampledYet);

  #endregion

}
