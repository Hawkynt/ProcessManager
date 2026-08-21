using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.App;

/// <summary>Which face of the program the arguments asked for.</summary>
internal enum RunMode : byte { Desktop, Terminal, List, Find, Kill, EndTask, Restart, Scheduling, Signal, ResourceLimit, OutOfMemory, Freezer, SelfTest, HelperCheck, Help, HelpFields, Host, Startup, Users, Services, ServiceControl, Connections, Limits, Run, Version, Settings }

/// <summary>
/// What <see cref="RunMode.Settings"/> was asked to do to the settings file (PRD §67).
/// </summary>
/// <remarks>
/// Four verbs on one mode rather than four modes, because they share everything that matters: each
/// one resolves the same file, does one thing to it and exits without ever bringing a front-end up.
/// </remarks>
internal enum SettingsAction : byte { None, Show, Export, Import, Reset }

/// <summary>
/// Which sockets <c>--connections</c> lists.
/// </summary>
/// <remarks>
/// Internet sockets by default because a desktop holds several hundred Unix sockets and a dozen
/// internet ones, and the dozen are what somebody asking "what is this machine talking to" means.
/// The others are one word away rather than absent, because "which process is on the session bus"
/// is a real question with no other answer here (PRD §5.2).
/// </remarks>
internal enum ConnectionScope : byte { Internet, Unix, All }

/// <summary>
/// The whole command line, parsed once into a value.
/// </summary>
/// <remarks>
/// Hand-written rather than reached for a parser package: the surface is a dozen switches, it has to
/// stay reflection-free for NativeAOT (PRD §2), and a dependency that pulls in reflection to read a
/// dozen switches is a poor trade.
/// </remarks>
internal sealed record CommandLineOptions {

  public RunMode Mode { get; init; } = RunMode.Desktop;
  public ProcessField SortColumn { get; init; } = ProcessField.CpuPercent;
  public bool SortDescending { get; init; } = true;
  public bool TreeMode {
    get => this.Grouping == ProcessGrouping.ParentTree;
    init => this.Grouping = value ? ProcessGrouping.ParentTree : ProcessGrouping.None;
  }

  /// <summary>What the rows are grouped by (PRD §83). The tree is one of the answers.</summary>
  public ProcessGrouping Grouping { get; init; }

  /// <summary>True when --flat was given, so the desktop's tree default can be overridden.</summary>
  public bool FlatRequested { get; init; }

  /// <summary>
  /// Draw the terminal's in-row history with the ASCII ramp rather than the eighth-block characters.
  /// Detected from the locale otherwise; this is for a terminal the detection gets wrong.
  /// </summary>
  public bool AsciiOnly { get; init; }
  public bool Json { get; init; }
  public bool AllUsers { get; init; } = true;
  public bool KillTree { get; init; }
  public int TargetPid { get; init; }

  /// <summary>The scheduler class --scheduling asked for, and its static priority (PRD §25.2).</summary>
  public SchedulingPolicy SchedulingClass { get; init; } = SchedulingPolicy.Unknown;

  public int SchedulingPriority { get; init; }

  /// <summary>The signal number --signal asked for; never a name, because the number is what the
  /// kernel takes and the mapping is the architecture's (PRD §25.1).</summary>
  public int Signal { get; init; }

  /// <summary>Which ceiling --rlimit sets, and to what (PRD §25.2).</summary>
  public ResourceLimitKind LimitKind { get; init; }

  /// <summary>Null is <c>RLIM_INFINITY</c> — no limit, which is not a quantity.</summary>
  public ulong? LimitSoft { get; init; }

  public ulong? LimitHard { get; init; }

  /// <summary>What --oom sets the out-of-memory adjustment to (PRD §25.5).</summary>
  public int OomAdjustment { get; init; }

  /// <summary>Whether --freeze or --thaw was given; the two are one action with two directions.</summary>
  public bool Freeze { get; init; }


  /// <summary>The program to start and its arguments, for <c>--run</c> (PRD §54).</summary>
  public IReadOnlyList<string>? LaunchCommand { get; init; }

  /// <summary>Start it stopped, so something can attach before it runs (PRD §54).</summary>
  public bool LaunchSuspended { get; init; }

  /// <summary>The directory to start it in.</summary>
  public string? LaunchDirectory { get; init; }
  public string? Pattern { get; init; }

  /// <summary>A filter in the query language of PRD §56, applied to --list and the two UIs.</summary>
  public string? Filter { get; init; }

  /// <summary>What to do to a unit, and which one, for <c>--service</c> (PRD §41).</summary>
  public string? ServiceVerb { get; init; }

  /// <summary>The unit <c>--service</c> names.</summary>
  public string? ServiceUnit { get; init; }

  /// <summary>Which sockets --connections lists (PRD §40).</summary>
  public ConnectionScope ConnectionScope { get; init; } = ConnectionScope.Internet;

  /// <summary>
  /// Turn addresses into hostnames, which asks a resolver about every address on the machine.
  /// </summary>
  /// <remarks>
  /// Off unless asked for. On some networks a reverse lookup tells whoever runs the resolver which
  /// addresses this machine is talking to, and that is not a disclosure to make on somebody's behalf
  /// (PRD §40).
  /// </remarks>
  public bool ResolveHostnames { get; init; }

  /// <summary>
  /// Leave ports as numbers rather than naming them from <c>/etc/services</c>, the way <c>ss -n</c>
  /// and <c>netstat -n</c> do.
  /// </summary>
  public bool NumericEndpoints { get; init; }

  /// <summary>What --list writes: text, csv, tsv, json, jsonl or markdown (PRD §61).</summary>
  public ExportFormat Format { get; init; } = ExportFormat.Text;

  /// <summary>Which fields --list writes, in order. Null means the default set.</summary>
  public ProcessField[]? Fields { get; init; }

  /// <summary>
  /// The columns the terminal opens with, which are not quite the columns a file gets.
  /// </summary>
  /// <remarks>
  /// A drawn history is a column in a terminal and nothing at all in a CSV, so <c>--columns</c> keeps
  /// the graphs here and drops them from <see cref="Fields"/>. Null leaves the terminal to pick the
  /// set that fits its width (PRD §57.1).
  /// </remarks>
  public ProcessField[]? TerminalColumns { get; init; }

  /// <summary>
  /// How many leading columns the terminal pins (PRD §11, §57.2).
  /// </summary>
  /// <remarks>
  /// From the settings file only. <c>#</c> moves the boundary in a running terminal and there is no
  /// flag for it, because a pinned run is a layout decision like a column width rather than
  /// something anybody wants to retype on every invocation.
  /// </remarks>
  public int PinnedTerminalColumns { get; init; } = 1;

  /// <summary>Whether the interactive front-ends open with the tick off (PRD §12).</summary>
  /// <remarks>
  /// From the settings file's <c>interval=manual</c>. A <c>--interval</c> on the command line is a
  /// rate and therefore an answer to a different question, so it says nothing about this either way.
  /// </remarks>
  public bool ManualRefresh { get; init; }

  /// <summary>
  /// The window's saved column layout, which is a request for those fields exactly as naming them on
  /// the command line would be.
  /// </summary>
  /// <remarks>
  /// Deliberately not folded into <see cref="Fields"/>: that is what <c>--list</c> writes, and a
  /// saved window layout has no business changing what a file export contains. This exists only so
  /// that the sampler is told to collect what the window is about to show — without it, somebody
  /// whose layout includes an opt-in column sees "not sampled yet" in it for the whole session, and
  /// nothing they can type will fix it because they already asked (PRD §5.4).
  /// </remarks>
  public ProcessField[]? DesktopColumns { get; init; }

  /// <summary>
  /// Whether anything this run asked for needs the LSM label, which costs a file per process.
  /// </summary>
  /// <remarks>
  /// Inferred rather than flagged: naming the field in --columns or in --filter is already a clear
  /// request for it, and a separate --security switch would only be a way to get an empty column
  /// by forgetting it (PRD §5.4).
  /// <para>
  /// The confinement mode is here rather than in a switch of its own because it is the same read:
  /// the bracketed word comes out of the label the file already carries, so asking for either buys
  /// both. Leaving it out is how a field ships whose read nothing ever turns on, and the column is
  /// then permanently empty while the document claims it works.
  /// </para>
  /// </remarks>
  public bool WantsSecurityContext
    => this.Wants(ProcessField.SecurityContext) || this.Wants(ProcessField.ConfinementMode);

  /// <summary>
  /// Whether the proportional set size is worth the file read it costs (PRD §5.4).
  /// </summary>
  /// <remarks>
  /// Same rule as the security context, and the same reasoning: <c>smaps_rollup</c> makes the kernel
  /// walk a process's page tables, so it is not something to do for four hundred processes a second
  /// unless somebody has said they want the number.
  /// <para>
  /// The swap figure comes from the same file, so asking for either buys both.
  /// </para>
  /// </remarks>
  public bool WantsProportionalSetSize
    => this.Wants(ProcessField.ProportionalSet) || this.Wants(ProcessField.ProportionalSwap);

  /// <summary>
  /// Whether the supplementary groups are worth the string they cost (PRD §5.4).
  /// </summary>
  /// <remarks>
  /// The line is already in front of the sampler, so this buys no read — it buys one allocation per
  /// process per sample, against a budget of zero (PRD §4). Same rule as the two above, for a
  /// different resource.
  /// </remarks>
  public bool WantsSupplementaryGroups => this.Wants(ProcessField.SupplementaryGroups);

  /// <summary>
  /// Whether the mitigation, umask, tracer and descriptor-table lines are worth recognising
  /// (PRD §5.4, §20, §21).
  /// </summary>
  /// <remarks>
  /// The same rule again, and for a cost that is neither a read nor an allocation: the lines are in
  /// a file the sampler already has open, but five more labels to recognise in a loop that runs
  /// fifty times per process cost a measurable share of the sample when every run paid it. So no run
  /// pays it unless a column or a filter names one of the six.
  /// </remarks>
  public bool WantsSecurityStatus
    => this.Wants(ProcessField.SpeculationStoreBypass)
    || this.Wants(ProcessField.SpeculationIndirectBranch)
    || this.Wants(ProcessField.ThreadFeatures)
    || this.Wants(ProcessField.Umask)
    || this.Wants(ProcessField.TracerPid)
    || this.Wants(ProcessField.DescriptorTableSize);

  /// <summary>
  /// Whether anything this run asked for needs a third sample (PRD §15).
  /// </summary>
  /// <remarks>
  /// The change in a process's CPU share is the difference between two intervals, and two samples
  /// are only one of them. The same rule as the expensive reads, for a cost measured in seconds
  /// rather than in syscalls: a <c>--list</c> that waited an extra interval for a column nobody
  /// named would take twice as long for nothing.
  /// </remarks>
  public bool WantsCpuPercentDelta => this.Wants(ProcessField.CpuPercentDelta);

  /// <summary>
  /// Whether the affinity list is worth the string it costs (PRD §5.4, §15).
  /// </summary>
  /// <remarks>
  /// The group list's rule for the group list's reason: the line is already in front of the sampler
  /// and keeping it is an allocation per process per sample.
  /// </remarks>
  public bool WantsCpuAffinity => this.Wants(ProcessField.CpuAffinity);

  /// <summary>
  /// Whether anything this run asked for needs each process's I/O scheduling class (PRD §5.4, §17).
  /// </summary>
  /// <remarks>
  /// The same rule again, for the one reading that is a syscall rather than a file: the kernel
  /// publishes the I/O priority nowhere under <c>/proc</c>, so a column nobody named would cost six
  /// hundred <c>ioprio_get</c> calls a second for nothing.
  /// </remarks>
  public bool WantsIoPriority => this.Wants(ProcessField.IoPriority);

  /// <summary>
  /// Whether anything this run asked for needs each process's cgroup read (PRD §5.4, §15).
  /// </summary>
  /// <remarks>
  /// A file outside <c>/proc</c> per cgroup per sample. Cheaper than it sounds, because the answer
  /// belongs to the group rather than to the process — but not free, and not worth paying for a
  /// column nobody named.
  /// </remarks>
  public bool WantsCpuThrottling => this.Wants(ProcessField.CpuThrottled);

  /// <summary>
  /// Whether anything this run asked for needs per-process graphics accounting (PRD §5.4, §19).
  /// </summary>
  /// <remarks>
  /// Inferred like the other two, and with one switch of its own besides. The inference cannot see a
  /// saved desktop column set, and a window is where somebody watching a GPU actually wants to be —
  /// so <c>--gpu</c> exists to say it out loud rather than making people pass <c>--columns</c> to a
  /// front-end that reads its columns from a file.
  /// </remarks>
  public bool WantsGpuUsage
    => this.Gpu
    || this.Wants(ProcessField.GpuPercent)
    || this.Wants(ProcessField.GpuEngineName)
    || this.Wants(ProcessField.GpuEnginePercent)
    || this.Wants(ProcessField.GpuAdapter)
    || this.Wants(ProcessField.GpuDedicatedMemory)
    || this.Wants(ProcessField.GpuSharedMemory)
    || this.Wants(ProcessField.GpuTotalMemory)
    || this.Wants(ProcessField.GpuDedicatedMemoryDelta)
    || this.Wants(ProcessField.GpuGraphicsPercent)
    || this.Wants(ProcessField.GpuComputePercent)
    || this.Wants(ProcessField.GpuCopyPercent)
    || this.Wants(ProcessField.GpuEncodePercent)
    || this.Wants(ProcessField.GpuDecodePercent);

  /// <summary>Collect per-process graphics figures, whether or not a column names one.</summary>
  public bool Gpu { get; init; }

  /// <summary>
  /// Whether the descriptor count is worth the directory listing it costs (PRD §5.4).
  /// </summary>
  /// <remarks>
  /// The most expensive read in the sampler: one <c>getdents</c> loop over <c>/proc/[pid]/fd</c> for
  /// every process, every sample. Same rule as the three above — and until this existed there was no
  /// rule at all, so the column could be asked for and came back empty however it was asked for.
  /// </remarks>
  public bool WantsHandleCount => this.Wants(ProcessField.HandleCount) || this.WantsDescriptorKinds;

  /// <summary>
  /// Whether the per-kind descriptor tally is worth the link it resolves per descriptor (PRD §5.4,
  /// §20).
  /// </summary>
  /// <remarks>
  /// The descriptor scan plus a <c>readlink</c> for every descriptor it finds — the most expensive
  /// read the sampler can be asked for, and the reason §20 kept the tallies out of the sample loop
  /// until there was a switch that only somebody naming a column could flip.
  /// </remarks>
  public bool WantsDescriptorKinds
    => this.Wants(ProcessField.SocketCount)
    || this.Wants(ProcessField.FileCount)
    || this.Wants(ProcessField.PipeCount);

  /// <summary>
  /// Whether anything this run asked for needs the images hashed (PRD §5.4, §21, §70).
  /// </summary>
  /// <remarks>
  /// The one read whose cost is the size of a file rather than a syscall, which is why §21 says "on
  /// demand only" — and naming the column is the demand. Asking for either digest buys both: they
  /// come from one read of the same bytes.
  /// </remarks>
  public bool WantsImageHashes
    => this.Wants(ProcessField.ImageSha256) || this.Wants(ProcessField.ImageSha1);

  /// <summary>
  /// Whether anything this run asked for needs the package databases read (PRD §5.4, §14).
  /// </summary>
  /// <remarks>
  /// The index costs thirty megabytes of file lists to build, once. Asking which package a process
  /// belongs to and asking whether that package's file has been changed are the same lookup, so the
  /// check implies the identity — and inferred the same way as everything else here, from the
  /// column or the filter naming it.
  /// </remarks>
  public bool WantsPackageIdentity
    => this.Wants(ProcessField.Package)
    || this.Wants(ProcessField.ApplicationId)
    // The chain is read out of the package's own database entry and needs no hash of the image, so
    // it costs the index and nothing beyond it — which puts it here rather than with the check.
    || this.Wants(ProcessField.TrustChain)
    // Grouping by package is somebody naming the column as much as a --columns argument is: the
    // headings are the field, and a run that did not collect it would head every row "package not
    // looked up" (PRD §83).
    || this.Grouping == ProcessGrouping.Package
    || this.WantsPackageVerification;

  /// <summary>
  /// Whether anything this run asked for needs each image checked against its package (PRD §70).
  /// </summary>
  /// <remarks>
  /// The identity plus a hash of every distinct image, because a comparison against a recorded
  /// digest needs a digest. The hash comes from the same per-image cache the digest columns use, so
  /// asking for the check and for the digests together costs one read of each file.
  /// </remarks>
  public bool WantsPackageVerification => this.Wants(ProcessField.PackageStatus);

  /// <summary>
  /// Whether anything this run asked for needs each process's module list (PRD §5.4, §14).
  /// </summary>
  /// <remarks>
  /// <c>maps</c> is a page-at-a-time read of tens of kilobytes for a browser tab, which is why the
  /// runtime is worked out only when somebody names the column — and once per process rather than
  /// once per sample, because a process does not change what is running inside it.
  /// </remarks>
  public bool WantsRuntime => this.Wants(ProcessField.Runtime);

  /// <summary>
  /// Whether anything this run asked for needs the machine's desktop entries read (PRD §5.4, §14).
  /// </summary>
  /// <remarks>
  /// Around three hundred small files, once. Nothing else on the machine wants them, so nothing but
  /// the column naming it turns them on.
  /// </remarks>
  public bool WantsApplicationName => this.Wants(ProcessField.ApplicationName);

  /// <summary>
  /// Whether anything this run asked for needs the image's birth time (PRD §5.4, §14).
  /// </summary>
  /// <remarks>
  /// One <c>statx</c> per process, on a path nothing else reads. Cheap next to the others here and
  /// still not free, so it follows the same rule.
  /// </remarks>
  public bool WantsImageCreationTime => this.Wants(ProcessField.ImageCreated);

  /// <summary>
  /// Whether anything this run asked for needs the sockets each process holds counted (PRD §18).
  /// </summary>
  /// <remarks>
  /// Dearer than the descriptor count and inferred the same way: the join from a socket to a process
  /// is a <c>readlink</c> for every open descriptor on the machine, on top of the directory listing
  /// that finds them. No switch of its own, because unlike the graphics columns these are asked for
  /// by name or not at all — there is no "show me the network page" that implies them.
  /// </remarks>
  public bool WantsSocketCounts
    => this.Wants(ProcessField.TcpConnectionCount)
    || this.Wants(ProcessField.UdpSocketCount)
    || this.Wants(ProcessField.ListeningSocketCount)
    || this.Wants(ProcessField.RemoteEndpointCount);

  /// <summary>
  /// Whether a field was asked for, by column or by filter.
  /// </summary>
  /// <remarks>
  /// Inferred rather than flagged: naming the field in --columns or in --filter is already a clear
  /// request for it, and a separate switch would only be a way to get an empty column by forgetting
  /// it (PRD §5.4).
  /// </remarks>
  /// <summary>
  /// Whether this run is the self-test, which asks for every Windows-only reading there is.
  /// </summary>
  /// <remarks>
  /// The four switches below are what §5.4 asks for: nothing pays for a reading unless a column or a
  /// filter names it. The consequence, though, is that the interop behind those columns would never
  /// run anywhere — <c>--self-test</c> names no columns, and it is the only thing in the whole
  /// pipeline that executes the Windows probe against a real kernel (PRD §9.4). So the self-test
  /// names all of them. It costs a walk of the handle table and a file read per image, once, in a
  /// diagnostic that already takes two samples a second apart; and on any other platform these four
  /// switches are read by nothing at all.
  /// </remarks>
  private bool WantsEverythingWindowsCanAnswer => this.Mode == RunMode.SelfTest;

  /// <summary>
  /// Whether anything this run asked for needs the Windows mitigation policies (PRD §5.4, §21).
  /// </summary>
  /// <remarks>
  /// A second <c>OpenProcess</c> per process with a stronger access right than anything else in the
  /// sampler takes, plus six calls on it. Once per process rather than once per sample, because a
  /// process's mitigation policy does not change except by its own hand — but still not something to
  /// do for a column nobody opened.
  /// </remarks>
  public bool WantsWindowsMitigations
    => this.WantsEverythingWindowsCanAnswer
    || this.Wants(ProcessField.DataExecutionPrevention)
    || this.Wants(ProcessField.AddressSpaceRandomisation)
    || this.Wants(ProcessField.ControlFlowGuard)
    || this.Wants(ProcessField.ShadowStackPolicy)
    || this.Wants(ProcessField.ArbitraryCodeGuard)
    || this.Wants(ProcessField.CodeIntegrityGuard);

  /// <summary>
  /// Whether anything this run asked for needs the machine's handle table tallied by type
  /// (PRD §5.4, §20).
  /// </summary>
  /// <remarks>
  /// One query for the whole machine rather than one per process, because Windows has no per-process
  /// handle query at all — which makes it cheaper than the Linux equivalent and still megabytes of
  /// table per sample. Naming any of the five buys all five: they come out of one pass.
  /// </remarks>
  public bool WantsObjectCounts
    => this.WantsEverythingWindowsCanAnswer
    || this.Wants(ProcessField.EventObjectCount)
    || this.Wants(ProcessField.SemaphoreObjectCount)
    || this.Wants(ProcessField.MutexObjectCount)
    || this.Wants(ProcessField.SectionObjectCount)
    || this.Wants(ProcessField.RegistryKeyCount);

  /// <summary>
  /// Whether anything this run asked for needs the desktop object quotas (PRD §5.4, §20, §39).
  /// </summary>
  /// <remarks>
  /// Its own switch rather than sharing the one above, because the cost has a different shape: two
  /// calls per process per <em>sample</em>, uncacheable, since the whole point of the column is that
  /// the number moves.
  /// </remarks>
  public bool WantsGuiObjectCounts
    => this.WantsEverythingWindowsCanAnswer
    || this.Wants(ProcessField.UserObjectCount)
    || this.Wants(ProcessField.GdiObjectCount);

  /// <summary>
  /// Whether anything this run asked for needs each image's version resource read (PRD §5.4, §14).
  /// </summary>
  /// <remarks>
  /// The cost is the size of a file rather than a syscall, and the subsystem is bought by the same
  /// read because it is in the same file's header — so naming any one of the six turns on the one
  /// pass that answers all of them.
  /// </remarks>
  public bool WantsImageVersions
    => this.WantsEverythingWindowsCanAnswer
    || this.Wants(ProcessField.ImageDescription)
    || this.Wants(ProcessField.ImageCompany)
    || this.Wants(ProcessField.ImageProduct)
    || this.Wants(ProcessField.ImageProductVersion)
    || this.Wants(ProcessField.ImageFileVersion)
    || this.Wants(ProcessField.Subsystem);

  private bool Wants(ProcessField wanted) {
    // Both lists, because both are somebody naming the column. The terminal's differs from the
    // file's — it keeps the drawn histories, and it can come from the settings file rather than from
    // this command line — and a saved terminal column that the sampler was never told to collect is
    // a column that says "not sampled" for the whole session (PRD §5.4).
    foreach (var list in (ReadOnlySpan<ProcessField[]?>)[this.Fields, this.TerminalColumns, this.DesktopColumns]) {
      if (list is not { } fields)
        continue;

      // Not "field": in C# 14 that is a keyword inside a property accessor and binds to the
      // synthesised backing field rather than to the loop variable.
      foreach (var candidate in fields)
        if (candidate == wanted)
          return true;
    }

    var key = FieldRegistry.Get(wanted).Key;
    return this.Filter is { } filter && filter.Contains(key, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Every field, printed from the registry rather than from a list kept alongside it — which is how
  /// the old help text came to name ten sort keys when there were seventeen (PRD §5.1).
  /// </summary>
  public static string FieldHelpText {
    get {
      var text = new System.Text.StringBuilder();
      text.AppendLine("Fields. Any of these can be used with --sort, --filter and --find,");
      text.AppendLine("by the key or by any of its aliases.");
      text.AppendLine();
      text.AppendLine($"  {"KEY",-20} {"ALIASES",-24} DESCRIPTION");
      foreach (var descriptor in FieldRegistry.All) {
        var aliases = descriptor.Aliases?.Replace(' ', ',') ?? "";
        var note = descriptor.Platforms == FieldPlatforms.All
          ? string.Empty
          : $" [{descriptor.Platforms.ToString().Replace(", ", "/", StringComparison.Ordinal)} only]";

        text.AppendLine($"  {descriptor.Key,-20} {aliases,-24} {descriptor.Description}{note}");
      }

      text.AppendLine();
      text.AppendLine("Filters: field:value  field=value  field>value  field>=value  field<value");
      text.AppendLine("""         field!=value  field:/regex/  "quoted text"  /regex/""");
      text.AppendLine("         AND OR NOT  &&  ||  !  ( )   — terms side by side mean AND");
      text.AppendLine("Sizes:   1024  1K  1KiB  1kB  1MiB  1GB      Times: 500ms  1.5s  2h");
      text.AppendLine();
      text.AppendLine("  procman --filter 'cpu:>50 AND user:alice'");
      text.AppendLine("  procman --filter 'memory:>1GiB NOT name:chrome'");
      return text.ToString();
    }
  }
  public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

  /// <summary>Read a recorded /proc tree instead of the live one (PRD §9.1).</summary>
  public string? ProbeRoot { get; init; }

  /// <summary>Compose one frame, write it here as text, and exit (PRD §9.6).</summary>
  public string? CaptureFramePath { get; init; }

  /// <summary>Compare the captured frame against this file and exit non-zero on a difference.</summary>
  public string? GoldenFramePath { get; init; }

  /// <summary>
  /// How many samples to take before capturing. Two is the minimum for any rate to exist at all; a
  /// screenshot wants a dozen so the in-row plots have a shape rather than a single mark.
  /// </summary>
  public int CaptureSamples { get; init; } = 2;

  /// <summary>
  /// How large the captured frame is, in cells (PRD §57.1).
  /// </summary>
  /// <remarks>
  /// The responsive layout is decided by the width, so a golden frame at one size proves nothing
  /// about the other two: 80×24 drops columns a 160×50 frame keeps, and only a capture at each size
  /// shows whether what is left still lines up.
  /// </remarks>
  public int CaptureWidth { get; init; } = 120;

  public int CaptureHeight { get; init; } = 40;

  /// <summary>
  /// Keys to press before the frame is captured, so a capture can photograph a page that is two
  /// keystrokes in (PRD §9.6). <c>\t</c>, <c>\n</c>, <c>\e</c> and <c>\s</c> name the four that
  /// cannot be written literally.
  /// </summary>
  public string? CaptureKeys { get; init; }

  /// <summary>How the terminal draws its history columns (PRD §57.4).</summary>
  public GraphStyle GraphStyle { get; init; } = GraphStyle.Blocks;

  /// <summary>
  /// Whether anybody actually chose that style, as opposed to it being the default.
  /// </summary>
  /// <remarks>
  /// The two are not the same and the difference matters at exactly one point: <c>Blocks</c> as a
  /// default means "let the terminal work out what it can draw", and <c>Blocks</c> because somebody
  /// said so means blocks. Without this the settings file could ask for blocks and be read as having
  /// asked for nothing, because the flag it lands in already held that value.
  /// </remarks>
  public bool GraphStyleWasStated { get; init; }

  /// <summary>Whether the terminal asks for mouse reports (PRD §57.5).</summary>
  public bool UseMouse { get; init; } = true;

  /// <summary>Bring the window up, photograph it and exit — the CI desktop smoke leg.</summary>
  public string? ShootPath { get; init; }

  /// <summary>
  /// Hold the window open this many seconds before exiting, so something outside can photograph it.
  /// Zero keeps the smoke run's behaviour: up, described, gone.
  /// </summary>
  public double ShootHoldSeconds { get; init; }

  /// <summary>Also write the captured terminal frame as an SVG picture of itself.</summary>
  public string? CaptureSvgPath { get; init; }

  /// <summary>
  /// Whether the privileged helper may be started at all. It is never started without a request that
  /// needs it, so the flag is for people who would rather it could not happen (PRD §8.1).
  /// </summary>
  public bool UseHelper { get; init; } = true;

  public string? Error { get; init; }

  /// <summary>Where the settings were read from, and where --save-settings will write them.</summary>
  public string? SettingsPath { get; init; }

  /// <summary>Write the options this run ended up with back to the settings file, then carry on.</summary>
  public bool SaveSettings { get; init; }

  /// <summary>What is being done to the settings file itself, rather than with it (PRD §67).</summary>
  public SettingsAction SettingsAction { get; init; }

  /// <summary>The other file an export writes to or an import reads from.</summary>
  public string? SettingsTransferPath { get; init; }

  /// <summary>Which CPU convention percentages are expressed in (PRD §3.2).</summary>
  public CpuPercentMode CpuMode { get; init; } = CpuPercentMode.Normalized;

  /// <summary>
  /// Parses the command line over the saved settings, so a flag beats a setting and a setting beats
  /// the built-in default — the layering every program of this shape has (PRD §67).
  /// </summary>
  public static CommandLineOptions Parse(string[] args, UserSettings? settings) {
    settings ??= new();
    var seeded = new CommandLineOptions {
      Interval = TimeSpan.FromSeconds(settings.IntervalSeconds),
      SortColumn = settings.SortField,
      SortDescending = settings.SortDescending,
      Grouping = settings.Grouping,
      CpuMode = settings.CpuMode,
      AsciiOnly = !settings.BlockCharacters,
      // A stated style beats the blocks flag, which only ever said "these two or those two". Left as
      // it was when the file states nothing, so the terminal still reads its own locale.
      GraphStyle = settings.TerminalGraphs ?? (settings.BlockCharacters ? GraphStyle.Blocks : GraphStyle.Ascii),
      GraphStyleWasStated = settings.TerminalGraphs is not null,
      TerminalColumns = settings.TerminalColumns.Length > 0 ? settings.TerminalColumns : null,
      PinnedTerminalColumns = settings.PinnedTerminalColumns,
      ManualRefresh = settings.ManualRefresh,
      DesktopColumns = settings.DesktopColumns.Length > 0 ? settings.DesktopColumns : null,
      UseMouse = settings.TerminalMouse,
    };

    return Parse(args, seeded, settings);
  }

  public static CommandLineOptions Parse(string[] args) => Parse(args, new CommandLineOptions(), new());

  private static CommandLineOptions Parse(string[] args, CommandLineOptions seed, UserSettings settings) {
    var options = seed;
    var explicitMode = false;

    for (var i = 0; i < args.Length; ++i) {
      var argument = args[i];
      var (name, inlineValue) = Split(argument);

      switch (name) {
        case "--tui" or "-t":
          options = options with { Mode = RunMode.Terminal };
          explicitMode = true;
          break;
        case "--list" or "-l":
          options = options with { Mode = RunMode.List };
          explicitMode = true;
          break;
        case "--format": {
          if (!TryValue(args, ref i, inlineValue, out var formatName))
            return options with { Error = "--format needs one of: text, csv, tsv, json, jsonl, markdown" };
          if (!Exporter.TryParseFormat(formatName, out var format))
            return options with { Error = $"unknown format '{formatName}'; try text, csv, tsv, json, jsonl or markdown" };

          options = options with { Format = format };
          break;
        }

        case "--columns": {
          if (!TryValue(args, ref i, inlineValue, out var list))
            return options with { Error = "--columns needs a comma-separated list of fields" };

          // "@name" is a saved or built-in column set, which is the whole point of naming them.
          if (list.StartsWith('@')) {
            var setName = list[1..];
            if (!settings.TryGetColumnSet(setName, out var named))
              return options with {
                Error = $"there is no column set called '{setName}'; try {string.Join(", ", settings.ColumnSetNames())}",
              };

            // A set is written for the window, where a drawn history is the point of the column. A
            // file has no cell to draw one in, so they are dropped here rather than written as an
            // empty column — which is the same rule --columns applies to a named graph, reached the
            // other way round.
            var writable = new List<ProcessField>(named.Length);
            foreach (var candidate in named)
              if (!FieldRegistry.Get(candidate).IsGraph)
                writable.Add(candidate);

            if (writable.Count == 0)
              return options with { Error = $"the column set '{setName}' is nothing but drawn histories" };

            options = options with { Fields = [.. writable], TerminalColumns = named };
            break;
          }

          if (!Exporter.TryParseFields(list, out var fields, out var reason))
            return options with { Error = $"--columns: {reason}" };

          options = options with { Fields = fields, TerminalColumns = fields };
          break;
        }

        case "--settings": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--settings needs a path" };

          options = options with { SettingsPath = path };
          break;
        }

        case "--save-settings":
          options = options with { SaveSettings = true };
          break;

        case "--settings-path":
          return options with { Mode = RunMode.Settings, SettingsAction = SettingsAction.Show };

        case "--export-settings": {
          if (!TryValue(args, ref i, inlineValue, out var destination))
            return options with { Error = "--export-settings needs a path to write to" };

          return options with {
            Mode = RunMode.Settings,
            SettingsAction = SettingsAction.Export,
            SettingsTransferPath = destination,
          };
        }

        case "--import-settings": {
          if (!TryValue(args, ref i, inlineValue, out var source))
            return options with { Error = "--import-settings needs a path to read from" };

          return options with {
            Mode = RunMode.Settings,
            SettingsAction = SettingsAction.Import,
            SettingsTransferPath = source,
          };
        }

        case "--reset-settings":
          return options with { Mode = RunMode.Settings, SettingsAction = SettingsAction.Reset };

        case "--filter": {
          if (!TryValue(args, ref i, inlineValue, out var query))
            return options with { Error = "--filter needs a query" };

          // Parsed here rather than at first use so a typo is reported before the screen clears,
          // and reported with the reason rather than as an empty list.
          if (!ProcessQuery.TryParse(query, out _, out var problem))
            return options with { Error = $"--filter: {problem}" };

          options = options with { Filter = query };
          break;
        }

        case "--find" or "-f": {
          if (!TryValue(args, ref i, inlineValue, out var pattern))
            return options with { Error = "--find needs a pattern" };

          options = options with { Mode = RunMode.Find, Pattern = pattern };
          explicitMode = true;
          break;
        }
        case "--kill": {
          if (!TryValue(args, ref i, inlineValue, out var pid) || !int.TryParse(pid, out var target))
            return options with { Error = "--kill needs a pid" };

          options = options with { Mode = RunMode.Kill, TargetPid = target };
          explicitMode = true;
          break;
        }
        case "--end-task": {
          if (!TryValue(args, ref i, inlineValue, out var pid) || !int.TryParse(pid, out var asked))
            return options with { Error = "--end-task needs a pid" };

          options = options with { Mode = RunMode.EndTask, TargetPid = asked };
          explicitMode = true;
          break;
        }
        case "--restart": {
          if (!TryValue(args, ref i, inlineValue, out var pid) || !int.TryParse(pid, out var again))
            return options with { Error = "--restart needs a pid" };

          options = options with { Mode = RunMode.Restart, TargetPid = again };
          explicitMode = true;
          break;
        }
        case "--scheduling": {
          // Two values, like --limits takes one: the pid and the class are one request and giving
          // either without the other is not a thing anybody means.
          if (i + 2 >= args.Length || !int.TryParse(args[i + 1], out var scheduled))
            return options with { Error = $"--scheduling needs a pid and a class ({SchedulingClasses.Vocabulary})" };

          if (!SchedulingClasses.TryParse(args[i + 2], out var policy, out var priority))
            return options with { Error = $"unknown scheduler class '{args[i + 2]}'; it is one of {SchedulingClasses.Vocabulary}" };

          options = options with {
            Mode = RunMode.Scheduling,
            TargetPid = scheduled,
            SchedulingClass = policy,
            SchedulingPriority = priority,
          };

          explicitMode = true;
          i += 2;
          break;
        }
        case "--signal": {
          // Two values, like --scheduling: the pid and the signal are one request, and either
          // without the other is not something anybody means.
          if (i + 2 >= args.Length || !int.TryParse(args[i + 1], out var signalled))
            return options with { Error = "--signal needs a pid and a signal, by name or by number: --signal 412 TERM" };

          if (!Signals.TryParse(args[i + 2], out var number))
            return options with {
              Error = Signals.NumbersAreKnownHere
                ? $"unknown signal '{args[i + 2]}'; it is a name such as TERM or HUP, or a number from 1 to 64"
                : Signals.UnknownArchitecture,
            };

          options = options with { Mode = RunMode.Signal, TargetPid = signalled, Signal = number };
          explicitMode = true;
          i += 2;
          break;
        }

        case "--rlimit": {
          // Three, because a ceiling is a pid, a name and a value and none of the three has a
          // default that would be safe to invent.
          if (i + 3 >= args.Length || !int.TryParse(args[i + 1], out var capped))
            return options with { Error = $"--rlimit needs a pid, a limit and a value: --rlimit 412 nofile 1024 (limits: {ResourceLimits.Vocabulary})" };

          if (!ResourceLimits.TryParse(args[i + 2], out var kind))
            return options with {
              Error = ResourceLimits.NumbersAreKnownHere
                ? $"unknown limit '{args[i + 2]}'; it is one of {ResourceLimits.Vocabulary}"
                : ResourceLimits.UnknownArchitecture,
            };

          if (!TryLimitValues(args[i + 3], out var soft, out var hard))
            return options with { Error = $"--rlimit takes a value, or soft:hard, or the word unlimited; '{args[i + 3]}' is none of them" };

          options = options with { Mode = RunMode.ResourceLimit, TargetPid = capped, LimitKind = kind, LimitSoft = soft, LimitHard = hard };
          explicitMode = true;
          i += 3;
          break;
        }

        case "--oom": {
          if (i + 2 >= args.Length || !int.TryParse(args[i + 1], out var scored) || !int.TryParse(args[i + 2], out var adjustment))
            return options with { Error = "--oom needs a pid and an adjustment from -1000 to 1000" };

          options = options with { Mode = RunMode.OutOfMemory, TargetPid = scored, OomAdjustment = adjustment };
          explicitMode = true;
          i += 2;
          break;
        }

        case "--freeze":
        case "--thaw": {
          if (!TryValue(args, ref i, inlineValue, out var pid) || !int.TryParse(pid, out var member))
            return options with { Error = $"{name} needs a pid" };

          options = options with { Mode = RunMode.Freezer, TargetPid = member, Freeze = name == "--freeze" };
          explicitMode = true;
          break;
        }

        case "--tree":
          options = options with { TreeMode = true, KillTree = true };
          break;
        case "--group": {
          if (!TryValue(args, ref i, inlineValue, out var grouping))
            return options with {
              Error = "--group needs one of: none, tree, user, session, service, executable, container, cgroup, package",
            };
          if (!UserSettings.TryParseGrouping(grouping, out var parsedGrouping))
            return options with { Error = $"there is no grouping called '{grouping}'" };

          options = options with { Grouping = parsedGrouping };
          break;
        }
        case "--flat":
          // The desktop opens as a tree, which is what the reference tools do. This starts it flat
          // and sorted, which is what somebody looking for the busiest process wants — and what a
          // screenshot of a process manager should show.
          options = options with { TreeMode = false, FlatRequested = true };
          break;
        case "--json":
          options = options with { Json = true };
          break;
        case "--sort": {
          if (!TryValue(args, ref i, inlineValue, out var column))
            return options with { Error = "--sort needs a column" };
          if (!FieldRegistry.TryParse(column, out var parsed))
            return options with { Error = $"unknown sort column '{column}'" };

          options = options with { SortColumn = parsed, SortDescending = parsed.PrefersDescending() };
          break;
        }
        case "--interval": {
          if (!TryValue(args, ref i, inlineValue, out var text) || !double.TryParse(text, out var seconds) || seconds <= 0)
            return options with { Error = "--interval needs a positive number of seconds" };

          options = options with { Interval = TimeSpan.FromSeconds(seconds) };
          break;
        }
        case "--user":
          options = options with { AllUsers = false };
          break;
        case "--ascii":
          options = options with { AsciiOnly = true };
          break;
        case "--resolve":
          options = options with { ResolveHostnames = true };
          break;
        case "-n":
        case "--numeric":
          options = options with { NumericEndpoints = true };
          break;
        case "--no-helper":
          options = options with { UseHelper = false };
          break;
        case "--gpu":
          options = options with { Gpu = true };
          break;
        case "--probe-root": {
          if (!TryValue(args, ref i, inlineValue, out var root))
            return options with { Error = "--probe-root needs a directory" };

          options = options with { ProbeRoot = root };
          break;
        }
        case "--capture-frame": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--capture-frame needs a file" };

          options = options with { Mode = RunMode.Terminal, CaptureFramePath = path };
          explicitMode = true;
          break;
        }
        case "--capture-size": {
          if (!TryValue(args, ref i, inlineValue, out var size) || !TryParseSize(size, out var width, out var height))
            return options with { Error = "--capture-size needs WIDTHxHEIGHT, such as 120x40" };

          options = options with { CaptureWidth = width, CaptureHeight = height };
          break;
        }
        case "--capture-keys": {
          if (!TryValue(args, ref i, inlineValue, out var keys))
            return options with { Error = "--capture-keys needs the keys to press" };

          options = options with { CaptureKeys = keys };
          break;
        }
        case "--graph-style": {
          if (!TryValue(args, ref i, inlineValue, out var style) || !Enum.TryParse<GraphStyle>(style, true, out var graph))
            return options with { Error = "--graph-style needs one of blocks, braille, ascii or numbers" };

          options = options with { GraphStyle = graph, GraphStyleWasStated = true };
          break;
        }
        case "--no-mouse":
          options = options with { UseMouse = false };
          break;
        case "--capture-samples": {
          if (!TryValue(args, ref i, inlineValue, out var text) || !int.TryParse(text, out var samples) || samples < 2)
            return options with { Error = "--capture-samples needs a number of at least 2" };

          options = options with { CaptureSamples = samples };
          break;
        }
        case "--shoot-hold": {
          if (!TryValue(args, ref i, inlineValue, out var text) || !double.TryParse(text, out var seconds) || seconds < 0)
            return options with { Error = "--shoot-hold needs a number of seconds" };

          options = options with { ShootHoldSeconds = seconds };
          break;
        }
        case "--capture-svg": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--capture-svg needs a file" };

          options = options with { Mode = RunMode.Terminal, CaptureSvgPath = path };
          explicitMode = true;
          break;
        }
        case "--compare-golden": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--compare-golden needs a file" };

          options = options with { GoldenFramePath = path };
          break;
        }
        case "--shoot": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--shoot needs a directory" };

          options = options with { Mode = RunMode.Desktop, ShootPath = path };
          explicitMode = true;
          break;
        }
        case "--self-test":
          options = options with { Mode = RunMode.SelfTest };
          explicitMode = true;
          break;
        case "--helper-check":
          options = options with { Mode = RunMode.HelperCheck };
          explicitMode = true;
          break;
        case "--services":
          options = options with { Mode = RunMode.Services };
          explicitMode = true;
          break;
        case "--service":
          // Two words: what to do, and to which unit. Both required, because a verb with no unit and
          // a unit with no verb are each a way of asking for something nobody meant.
          if (i + 2 >= args.Length)
            return options with { Error = "--service needs a command and a unit: --service restart nginx.service" };

          options = options with {
            Mode = RunMode.ServiceControl,
            ServiceVerb = args[++i],
            ServiceUnit = args[++i],
          };

          explicitMode = true;
          break;

        case "--connections": {
          // The value is only read inline. Taking the next argument instead would make a bare
          // --connections at the end of the line an error and --connections --json swallow the
          // switch after it.
          var scope = inlineValue switch {
            null or "inet" or "internet" => ConnectionScope.Internet,
            "unix" or "local" => ConnectionScope.Unix,
            "all" => ConnectionScope.All,
            _ => (ConnectionScope?)null,
          };

          if (scope is not { } wanted)
            return options with { Error = "--connections takes inet (the default), unix or all" };

          options = options with { Mode = RunMode.Connections, ConnectionScope = wanted };
          explicitMode = true;
          break;
        }

        case "--users":
          options = options with { Mode = RunMode.Users };
          explicitMode = true;
          break;

        case "--startup":
          options = options with { Mode = RunMode.Startup };
          explicitMode = true;
          break;

        case "--run":
          // Everything after --run belongs to the program being started, including anything that
          // looks like one of our own switches. A launcher that ate its child's --help would be
          // useless for exactly the programs somebody most wants to start.
          if (i + 1 >= args.Length)
            return options with { Error = "--run needs a program to start" };

          return options with { Mode = RunMode.Run, LaunchCommand = args[(i + 1)..] };

        case "--host":
          options = options with { Mode = RunMode.Host };
          explicitMode = true;
          break;

        case "--limits":
          if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var limited))
            return options with { Error = "--limits needs a pid" };

          options = options with { Mode = RunMode.Limits, TargetPid = limited };
          explicitMode = true;
          ++i;
          break;

        case "--help-fields":
          return options with { Mode = RunMode.HelpFields };

        case "--help" or "-h" or "-?":
          return options with { Mode = RunMode.Help };
        case "--version" or "-V":
          return options with { Mode = RunMode.Version };
        default:
          return options with { Error = $"unknown option '{argument}'" };
      }
    }

    // With no display there is nothing for the desktop front-end to open. Falling back to the
    // terminal is what makes `procman` over SSH do the useful thing instead of the failing one.
    if (!explicitMode && options.Mode == RunMode.Desktop && !HasDisplay())
      options = options with { Mode = RunMode.Terminal };

    return options;
  }

  private static bool HasDisplay()
    => OperatingSystem.IsWindows()
    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

  private static (string Name, string? Value) Split(string argument) {
    var equals = argument.IndexOf('=', StringComparison.Ordinal);
    return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
  }

  /// <summary>
  /// Reads a limit as <c>soft:hard</c>, or as one value meaning both.
  /// </summary>
  /// <remarks>
  /// One value setting both halves is <c>prlimit</c>'s own rule, and following it is what makes an
  /// answer checkable against the tool. Both halves are always sent, because the syscall sets both
  /// together — a switch that changed one and preserved the other would have to read the other back
  /// first and would race with anything the process did to itself in between.
  /// </remarks>
  private static bool TryLimitValues(string text, out ulong? soft, out ulong? hard) {
    soft = null;
    hard = null;
    var colon = text.IndexOf(':', StringComparison.Ordinal);
    if (colon < 0)
      return ResourceLimits.TryParseValue(text, out soft) && ResourceLimits.TryParseValue(text, out hard);

    return ResourceLimits.TryParseValue(text[..colon], out soft)
      && ResourceLimits.TryParseValue(text[(colon + 1)..], out hard);
  }

  private static bool TryValue(string[] args, ref int index, string? inlineValue, out string value) {
    if (inlineValue is not null) {
      value = inlineValue;
      return true;
    }

    if (index + 1 >= args.Length) {
      value = string.Empty;
      return false;
    }

    value = args[++index];
    return true;
  }

  /// <summary>Reads a <c>WIDTHxHEIGHT</c> argument, in either of the two letters people write it with.</summary>
  private static bool TryParseSize(string text, out int width, out int height) {
    width = height = 0;
    var parts = text.Split(['x', 'X', '*'], 2);
    return parts.Length == 2
      && int.TryParse(parts[0], out width) && width >= 40
      && int.TryParse(parts[1], out height) && height >= 10;
  }

  public const string HelpText = """
    procman — a process manager for Windows and Linux

    Usage:
      procman                        the desktop UI (falls back to the terminal with no display)
      procman --tui                  the terminal UI
      procman --list [--json]        one snapshot to stdout, then exit
      procman --find <pattern>       which processes match, by name, command line or open file
      procman --host                 what this machine is: processor, memory, cache, uptime
      procman --limits PID           every ceiling on a process: its own, its cgroup's, and the
                                     out-of-memory standing that decides who dies first
      procman --run PROGRAM [ARG...] start a program; everything after --run belongs to it
      procman --startup              what is configured to start when you log in
      procman --users                who is logged in, and what their processes cost
      procman --services             which services exist and which are running
      procman --service <cmd> <unit> start, stop, restart, reload, enable or disable a unit
      procman --connections[=what]   every socket and who owns it: inet (default), unix or all
      procman --help-fields          every field that can be sorted, filtered or shown
      procman --kill <pid> [--tree]  end a process, optionally with its descendants
      procman --end-task <pid>       ask a process to close: its window first, SIGTERM if it has none
      procman --restart <pid>        end it and start it again with the same arguments and directory
      procman --scheduling <pid> <c> move it into a scheduler class: other, batch, idle, rr, fifo
      procman --signal <pid> <sig>   send any signal, by name or number: TERM, HUP, USR1, 34
      procman --rlimit <pid> <l> <v> set one ceiling: --rlimit 412 nofile 1024, or 512:4096
      procman --oom <pid> <n>        how likely the out-of-memory killer is to pick it, -1000 to 1000
      procman --freeze <pid>         stop every process in its cgroup; --thaw starts them again

    Options:
      --sort <field>     any field key; see --help-fields for the list
      --filter <query>   show only matching processes: 'cpu:>50', 'user:alice AND memory:>1GiB'
      --format <fmt>     text (default), csv, tsv, json, jsonl, markdown
      --columns <a,b,c>  which fields to write; see --help-fields
      --columns @<name>  a saved or built-in column set: basic, expert, security, io, memory, cpu
      --settings <path>  read settings from here instead of the usual place
      --save-settings    write this run's options back to the settings file
      --settings-path    print which settings file is in use, and what put it there
      --export-settings <path>  copy the settings out to a file, then exit
      --import-settings <path>  replace the settings with that file's, then exit
      --reset-settings   remove the settings file, so the next start is a fresh one
      --tree             show the process tree (with --kill: the whole subtree)
      --flat             start with a flat list sorted by CPU rather than a tree
      --group <what>     group the rows: none, tree, user, session, service, executable,
                         container, cgroup, package
      --user             only this user's processes
      --interval <s>     seconds between samples (default 1)
      --json             the same as --format=json
      --probe-root <d>   read a recorded /proc tree instead of the live one
      --ascii            draw the terminal's history columns with ASCII rather than block characters
      --graph-style <s>  blocks (default), braille, ascii or numbers for the history columns
      --capture-size WxH the size of a captured terminal frame (default 120x40)
      --capture-keys <k> press these keys before capturing: \t tab, \n enter, \e escape, \s space
      --no-mouse         do not ask the terminal for mouse reports
      --resolve          with --connections: turn addresses into hostnames (asks a resolver)
      -n, --numeric      with --connections: leave ports as numbers rather than naming them
      --gpu              account for what each process is doing to the graphics adapters (costly)
      --no-helper        never start the privileged helper, even for an action that needs it
      --self-test        check the probe against the runtime's own view of this process
      --helper-check     talk to the privileged helper over its pipe, unelevated, and check it
      --help, --version

    Exit codes: 0 success · 1 error · 2 nothing matched
    """;

}
