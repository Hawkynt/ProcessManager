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
  Category,
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
  CpuSets,
  CpuThrottled,
  CpuHistory,

  PrivateBytes,
  PrivateBytesDelta,
  PeakPrivateBytes,
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
  ShareableWorkingSet,
  MappedFileBytes,
  StackBytes,
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
  PagePriority,
  Swap,

  IoTotalRate,
  ReadBytesTotal,
  WriteBytesTotal,
  OtherBytesTotal,
  ReadBytesPerSecond,
  WriteBytesPerSecond,
  OtherBytesPerSecond,

  /// <summary>When a process that has ended did so (PRD §14, §87).</summary>
  ExitTime,

  /// <summary>What it exited with, where anybody could have known (PRD §14).</summary>
  ExitCode,
  ReadOperations,
  ReadOperationsDelta,
  WriteOperations,
  WriteOperationsDelta,
  OtherOperations,
  BlockIoWait,
  IoPriority,
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
  Protected,
  ProtectionLevel,
  AppContainer,
  DataExecutionPrevention,
  AddressSpaceRandomisation,
  ControlFlowGuard,
  ShadowStackPolicy,
  ArbitraryCodeGuard,
  CodeIntegrityGuard,
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
  ApplicationName,
  PackageStatus,
  ImageSignature,
  ImageSigner,
  CertificateSubject,
  CertificateIssuer,
  SignatureTimestamp,
  TrustChain,
  Reputation,
  Runtime,
  ImageCreated,
  ImageDescription,
  ImageCompany,
  ImageProduct,
  ImageProductVersion,
  ImageFileVersion,
  Subsystem,
  Emulation,

  BackgroundQualityOfService,
  EcoMode,

  ThreadCount,
  HandleCount,
  SocketCount,
  FileCount,
  PipeCount,
  EventObjectCount,
  SemaphoreObjectCount,
  MutexObjectCount,
  SectionObjectCount,
  RegistryKeyCount,
  UserObjectCount,
  GdiObjectCount,
  DescriptorTableSize,
  Priority,
  PriorityClass,
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

/// <summary>
/// What authority reading a field needs, which is what turns an em dash into an explanation
/// (PRD §5.1, §7).
/// </summary>
/// <remarks>
/// <para>
/// Two levels and not a list of capability names, because two is what a reader can act on: there is
/// nothing to do, or there is the elevated helper. Which capability the kernel is actually applying
/// — it is <c>ptrace_may_access</c> for every one of these on Linux, and a stronger handle right on
/// Windows — belongs in the field's description where it is worth naming, and is not something this
/// program can hand anybody.
/// </para>
/// <para>
/// A field declares the most it can need on a platform that supports it: the I/O counters are free
/// on Windows, where one system-wide query fills them, and behind the owner's authority on Linux,
/// where the file has been mode 0400 since 5.12. <see cref="Owner"/> is what that field declares,
/// because the reader who will find an em dash in the column is the reason the declaration exists.
/// </para>
/// <para>
/// There is no third level, and that is a finding rather than an omission: no field in the catalogue
/// needs elevation to be read about a process of your own. The things that do — a thread's kernel
/// stack, its current system call — are not fields of the table and are refused with their own
/// reasons (PRD §29, §30).
/// </para>
/// </remarks>
public enum FieldPrivilege : byte {

  /// <summary>Asks for nothing beyond being logged in. Most of a process table is this.</summary>
  Ordinary,

  /// <summary>
  /// Yours for the asking; another user's needs the elevated helper. The shape of every <c>/proc</c>
  /// file the kernel gates with <c>ptrace_may_access</c>, and of a Windows handle that wants more
  /// than limited information.
  /// </summary>
  Owner,

}

/// <summary>
/// How a machine-readable export writes a field (PRD §5.1, §61, §76).
/// </summary>
/// <remarks>
/// Derived from the kind and the unit rather than declared a hundred and fifty times, so that a
/// field cannot be declared a byte count in one place and serialised as a string in another — which
/// is exactly the drift one catalogue exists to prevent.
/// </remarks>
public enum FieldSerialisation : byte {

  /// <summary>Nothing to write. A drawn history has no cell in a file.</summary>
  None,

  /// <summary>The underlying string, as it is held rather than as it was abbreviated for a column.</summary>
  Text,

  /// <summary>The raw number in the field's own unit — bytes as bytes, nanoseconds as nanoseconds.</summary>
  Number,

  /// <summary>ISO 8601 in UTC, which sorts as text and imports as a date everywhere.</summary>
  Timestamp,

}

/// <summary>
/// Which rings keep a field's readings over time (PRD §5.1, §8.2, §28).
/// </summary>
/// <remarks>
/// Eligibility, not a promise that a ring exists: history is kept for the rows a front-end says are
/// on screen and for the process a properties window is pinned to, and for nothing else (PRD §5.4).
/// </remarks>
[Flags]
public enum FieldHistory : byte {

  /// <summary>Not kept. The value is whatever the last sample said and nothing remembers the one before.</summary>
  None = 0,

  /// <summary>
  /// Kept in the short shared rings behind the table's sparklines — sixty samples, one scale for
  /// every row so that rows compare (PRD §8.2).
  /// </summary>
  Row = 1,

  /// <summary>
  /// Kept for as long as a properties window pinned to the process stays open, which is where the
  /// hour of §28's plots comes from.
  /// </summary>
  Process = 2,

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
/// <param name="Series">
/// Which of the shared row rings this field's readings go into, for the fields any history is kept
/// for. Both the drawn column and the number it is drawn from name the same ring, which is what
/// stops a sparkline being plotted from a different reading than the column beside it.
/// </param>
/// <param name="Privilege">What reading it needs beyond being logged in.</param>
/// <param name="History">Which rings keep it, if any.</param>
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
  string? Aliases = null,
  FieldPrivilege Privilege = FieldPrivilege.Ordinary,
  FieldHistory History = FieldHistory.None
) {

  /// <summary>True for the three drawn histories, which have no text and no sort order.</summary>
  public bool IsGraph => this.Kind == FieldKind.Graph;

  /// <summary>
  /// How a machine format writes this field (PRD §5.1, §61).
  /// </summary>
  /// <remarks>
  /// One rule read off the kind and the unit rather than a hundred and fifty separate declarations
  /// that would each be a chance to disagree with the column. It is what the exporter dispatches on,
  /// so a field added to the catalogue is serialised correctly on the day it is added — which the
  /// two timestamp fields added after the exporter was written were not: everything but the start
  /// time exported as <c>null</c>, because the timestamp branch named one field instead of asking
  /// what the field was.
  /// </remarks>
  public FieldSerialisation Serialisation =>
    this.Kind == FieldKind.Graph ? FieldSerialisation.None
    : this.Unit == FieldUnit.Timestamp ? FieldSerialisation.Timestamp
    : this.Kind is FieldKind.Text or FieldKind.State ? FieldSerialisation.Text
    : FieldSerialisation.Number;

  /// <summary>The value of <see cref="Precision"/> for a field whose formatter chooses by magnitude.</summary>
  /// <remarks>
  /// A byte count is written "512 B", "1.5K" and "4.0G" — one decimal until the mantissa reaches a
  /// hundred and none after, so that a column keeps its width where it is widest. There is no single
  /// number to declare for that, and declaring the wrong one would be worse than declaring none.
  /// </remarks>
  public const int ByMagnitude = -1;

  /// <summary>The value of <see cref="Precision"/> for a field that is not a number at all.</summary>
  public const int NotNumeric = -2;

  /// <summary>
  /// How many decimals this field's value is written with (PRD §5.1).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Read off the unit, because that is where it is decided: every percentage on the machine is
  /// written to the same precision, every byte count scales the same way, and a count is a count.
  /// Repeating the number on every entry would put a hundred and fifty copies of one rule in a file
  /// whose whole purpose is that there is one — and the copies would be the thing that drifted.
  /// </para>
  /// <para>
  /// Percentages follow <see cref="Humanize.PercentDecimals"/>, which is a setting: somebody who has
  /// asked for two decimals has asked every front-end for two, and this answers with what they will
  /// actually see rather than with the default they changed.
  /// </para>
  /// </remarks>
  public int Precision => this.Kind switch {
    FieldKind.Graph or FieldKind.Text or FieldKind.State => NotNumeric,
    // A pid is a whole number and never anything else, whatever unit it claims.
    FieldKind.Identifier => 0,
    _ => this.Unit switch {
      FieldUnit.Percent => Humanize.PercentDecimals,
      FieldUnit.Bytes or FieldUnit.BytesPerSecond or FieldUnit.CountPerSecond => ByMagnitude,
      // A duration is written h:mm:ss and a timestamp to the second; neither carries a fraction.
      _ => 0,
    },
  };

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
