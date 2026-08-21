using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Every value a process row can show, sort by or be filtered on.
/// </summary>
/// <remarks>
/// One enum for the whole program, deliberately. There used to be three lists — a sort-key enum here,
/// a column set in the window and a third in the terminal — which meant adding a field meant editing
/// three places, and three places is three places to forget one. PRD §5.1 and §103.
/// <para>
/// The order is the default column order, which is Process Explorer's rather than alphabetical.
/// </para>
/// </remarks>
public enum ProcessField : byte {

  Name = 0,
  Pid,
  PidHex,
  ParentPid,
  ParentName,
  UserName,
  State,

  CpuPercent,
  CpuPercentPerCore,
  CpuTime,
  CyclesDelta,
  ContextSwitchesDelta,
  LastCpu,
  SchedulingClass,
  CpuAffinity,
  CpuThrottled,
  CpuHistory,

  PrivateBytes,
  PrivateBytesDelta,
  PrivateWorkingSet,
  ProportionalSet,
  UniqueSet,
  MemoryPercent,
  CpuPercentDelta,
  Nice,
  Terminal,
  ExecutableName,
  ContainerId,
  ProportionalSwap,
  FileBackedSet,
  SharedSet,
  UserTime,
  KernelTime,
  MemoryHistory,
  WorkingSetBytes,
  PeakWorkingSet,
  VirtualBytes,
  PeakVirtualBytes,
  PagedPool,
  PeakPagedPool,
  NonPagedPool,
  PeakNonPagedPool,
  PageFaultsDelta,
  Swap,

  IoTotalRate,
  ReadBytesPerSecond,
  WriteBytesPerSecond,
  IoHistory,

  TcpConnectionCount,
  UdpSocketCount,
  ListeningSocketCount,
  RemoteEndpointCount,

  GpuPercent,
  GpuEngineName,
  GpuEnginePercent,
  GpuAdapter,
  GpuDedicatedMemory,
  GpuSharedMemory,
  GpuTotalMemory,
  GpuDedicatedMemoryDelta,
  GpuGraphicsPercent,
  GpuComputePercent,
  GpuCopyPercent,
  GpuEncodePercent,
  GpuDecodePercent,

  Elevated,
  Integrity,
  Seccomp,
  SeccompFilters,
  NoNewPrivileges,
  Capabilities,
  CapabilitiesHex,
  PermittedCapabilities,
  InheritableCapabilities,
  BoundingCapabilities,
  AmbientCapabilities,
  SecurityContext,
  ConfinementMode,
  SpeculationStoreBypass,
  SpeculationIndirectBranch,
  ThreadFeatures,
  Umask,
  TracerPid,
  PrivilegeChanged,
  EffectiveUserName,
  UserId,
  EffectiveUserId,
  SavedUserId,
  FilesystemUserId,
  GroupId,
  EffectiveGroupId,
  SavedGroupId,
  FilesystemGroupId,
  SupplementaryGroups,
  ImageSha256,
  ImageSha1,
  Package,
  ApplicationId,
  PackageStatus,
  Runtime,
  ImageCreated,

  ThreadCount,
  HandleCount,
  SocketCount,
  FileCount,
  PipeCount,
  DescriptorTableSize,
  Priority,
  SessionId,
  StartTime,
  Container,
  ImagePath,
  CommandLine,

}

/// <summary>
/// What kind of number a field is, which decides whether it may be averaged, summed, or graphed at
/// all (PRD §5.1).
/// </summary>
public enum FieldKind : byte {

  /// <summary>Free text — a name, a path, a command line.</summary>
  Text,

  /// <summary>An identifier that happens to be numeric. Sorts numerically, never summed.</summary>
  Identifier,

  /// <summary>A value that is true right now and has no history of its own.</summary>
  Instant,

  /// <summary>Monotonic since the process started. The interesting figure is its derivative.</summary>
  Cumulative,

  /// <summary>The change in a cumulative counter over one interval.</summary>
  Delta,

  /// <summary>A per-second figure derived from two samples.</summary>
  Rate,

  /// <summary>One of a fixed set of states.</summary>
  State,

  /// <summary>A drawn history rather than a value; has no text and cannot be sorted.</summary>
  Graph,

}

/// <summary>What the number counts, which decides how it is formatted and how a filter parses it.</summary>
public enum FieldUnit : byte {
  None,
  Bytes,
  BytesPerSecond,
  Percent,
  Nanoseconds,
  Count,
  CountPerSecond,
  Timestamp,
}

/// <summary>
/// What reading the field costs, so an expensive one is never made default-visible by accident
/// (PRD §5.4).
/// </summary>
public enum FieldCost : byte {

  /// <summary>Already in the snapshot; showing it costs nothing at all.</summary>
  Free,

  /// <summary>Needs a second sample, and nothing else.</summary>
  Derived,

  /// <summary>Costs a syscall or more per process. Never default-visible.</summary>
  High,

}

/// <summary>Which platforms can fill a field. A platform not listed renders <c>n/a</c>, not zero.</summary>
[Flags]
public enum FieldPlatforms : byte {
  None = 0,
  Windows = 1,
  Linux = 2,
  MacOS = 4,
  All = Windows | Linux | MacOS,
}

/// <summary>
/// Everything the program knows about one field: how to label it, how wide to draw it, what it
/// means, and who can fill it.
/// </summary>
/// <param name="Id">The enum value.</param>
/// <param name="Key">
/// The stable identifier. This is what a saved layout, a <c>--sort</c> argument and a search term all
/// use, and it never changes even when the header does — including when the header differs per
/// platform, which is the point (PRD §5.3).
/// </param>
/// <param name="Header">The full label, for the window.</param>
/// <param name="ShortHeader">The narrow label, for the terminal.</param>
/// <param name="Description">One sentence, for the tooltip and the column chooser.</param>
/// <param name="DesktopWidth">Pixels.</param>
/// <param name="TerminalWidth">Character cells.</param>
/// <param name="PrefersDescending">
/// Whether biggest-first is what a single click should give. Sorting by CPU ascending is not what
/// anybody wants from one keypress, and sorting names descending is not either.
/// </param>
public sealed record FieldDescriptor(
  ProcessField Id,
  string Key,
  string Header,
  string ShortHeader,
  string Description,
  FieldKind Kind,
  FieldUnit Unit,
  FieldPlatforms Platforms,
  FieldCost Cost,
  int DesktopWidth,
  int TerminalWidth,
  bool RightAligned,
  bool PrefersDescending,
  HistorySeries? Series = null,
  string? Aliases = null
) {

  /// <summary>True for the three drawn histories, which have no text and no sort order.</summary>
  public bool IsGraph => this.Kind == FieldKind.Graph;

  /// <summary>False for graphs, true for everything else.</summary>
  public bool IsSortable => this.Kind != FieldKind.Graph;

  /// <summary>
  /// Whether this field can hold a number on this machine at all — used to decide between showing a
  /// value and showing why there is none.
  /// </summary>
  public bool IsSupportedHere => (this.Platforms & CurrentPlatform) != 0;

  /// <summary>Which <see cref="FieldPlatforms"/> flag this machine is.</summary>
  public static FieldPlatforms CurrentPlatform { get; } =
    OperatingSystem.IsWindows() ? FieldPlatforms.Windows
    : OperatingSystem.IsLinux() ? FieldPlatforms.Linux
    : OperatingSystem.IsMacOS() ? FieldPlatforms.MacOS
    : FieldPlatforms.None;

}
