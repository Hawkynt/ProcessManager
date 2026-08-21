using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Reads one field out of a process: as text to display, as a number to compare, and as an ordering.
/// </summary>
/// <remarks>
/// The single place any field is turned into anything. Both front-ends render through
/// <see cref="Text"/>, the view sorts through <see cref="Compare"/>, and the filter compares through
/// <see cref="Number"/> — so a value reads the same in the window and in the terminal, and sorting by
/// a column can never disagree with what that column shows (PRD §5.1).
/// </remarks>
public static class FieldAccessor {

  /// <summary>
  /// What the field shows, including the reason when it shows no value (PRD §72.3).
  /// </summary>
  /// <param name="delta">
  /// May be <see langword="null"/> before a second sample exists, in which case every derived field
  /// reads as "not sampled yet" rather than as zero.
  /// </param>
  public static string Text(ProcessField field, in ProcessRecord process, SnapshotDelta? delta, int index) {
    switch (field) {
      case ProcessField.Name: return process.Name;
      case ProcessField.Pid: return process.Pid.ToString(CultureInfo.InvariantCulture);
      case ProcessField.PidHex: return "0x" + process.Pid.ToString("X", CultureInfo.InvariantCulture);
      case ProcessField.ParentPid:
        return process.ParentPid > 0 ? process.ParentPid.ToString(CultureInfo.InvariantCulture) : "—";
      // Not "unknown": a parent that is not in this sample has exited and the process has been
      // reparented, which is a fact about the tree rather than a gap in what could be read.
      case ProcessField.ParentName: return process.ParentName ?? "—";
      case ProcessField.UserName:
        return process.UserName ?? Humanize.Placeholder(UnknownReason.NotPermitted);
      case ProcessField.State: return Humanize.State(process.State);

      case ProcessField.CpuPercent: return Humanize.Percent(Rated(delta, index, field));
      case ProcessField.CpuPercentPerCore: return Humanize.Percent(Rated(delta, index, field));
      case ProcessField.CpuPercentDelta: return Humanize.SignedPercent(Rated(delta, index, field));
      case ProcessField.MemoryPercent: return Humanize.Percent(Rated(delta, index, field));
      case ProcessField.CpuTime: return Humanize.Duration(process.CpuTimeNs);
      case ProcessField.UserTime: return Humanize.Duration(process.UserTimeNs);
      case ProcessField.KernelTime: return Humanize.Duration(process.KernelTimeNs);
      case ProcessField.ProportionalSet: return Humanize.Bytes(process.ProportionalBytes);
      case ProcessField.ProportionalSwap: return Humanize.Bytes(process.ProportionalSwapBytes);
      case ProcessField.FileBackedSet: return Humanize.Bytes(process.FileBackedBytes);
      case ProcessField.SharedSet: return Humanize.Bytes(process.SharedResidentBytes);
      case ProcessField.LastCpu:
        // -1 is the platform declining to say, not processor number minus one.
        return process.LastCpu >= 0
          ? process.LastCpu.ToString(CultureInfo.InvariantCulture)
          : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

      case ProcessField.SchedulingClass:
        // Unknown is a stat line that stopped short or a platform that has no such notion — not the
        // ordinary class, which is what a nought here would claim (PRD §5.3).
        return process.SchedulingPolicy == SchedulingPolicy.Unknown
          ? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)
          : Humanize.SchedulingPolicy(process.SchedulingPolicy);
      case ProcessField.CpuAffinity:
        return process.CpuAffinity ?? Humanize.Placeholder(process.CpuAffinityReason);
      case ProcessField.CpuThrottled: return Humanize.Count(process.ThrottledPeriods);

      case ProcessField.CyclesDelta: return Humanize.Rate(Rated(delta, index, field));
      case ProcessField.ContextSwitchesDelta: return Humanize.Rate(Rated(delta, index, field));
      case ProcessField.PageFaultsDelta: return Humanize.Rate(Rated(delta, index, field));

      case ProcessField.PrivateBytes: return Humanize.Bytes(process.PrivateBytes);
      case ProcessField.PrivateBytesDelta: return Humanize.SignedBytesPerSecond(Rated(delta, index, field));
      case ProcessField.PrivateWorkingSet: return Humanize.Bytes(process.PrivateWorkingSetBytes);
      case ProcessField.WorkingSetBytes: return Humanize.Bytes(process.WorkingSetBytes);
      case ProcessField.PeakWorkingSet: return Humanize.Bytes(process.PeakWorkingSetBytes);
      case ProcessField.VirtualBytes: return Humanize.Bytes(process.VirtualBytes);
      case ProcessField.PeakVirtualBytes: return Humanize.Bytes(process.PeakVirtualBytes);
      case ProcessField.PagedPool: return Humanize.Bytes(process.PagedPoolBytes);
      case ProcessField.PeakPagedPool: return Humanize.Bytes(process.PeakPagedPoolBytes);
      case ProcessField.NonPagedPool: return Humanize.Bytes(process.NonPagedPoolBytes);
      case ProcessField.PeakNonPagedPool: return Humanize.Bytes(process.PeakNonPagedPoolBytes);
      case ProcessField.Swap: return Humanize.Bytes(process.SwapBytes);

      case ProcessField.IoTotalRate:
      case ProcessField.ReadBytesPerSecond:
      case ProcessField.WriteBytesPerSecond:
        return Humanize.BytesPerSecond(Rated(delta, index, field));

      case ProcessField.GpuPercent:
      case ProcessField.GpuEnginePercent:
      case ProcessField.GpuGraphicsPercent:
      case ProcessField.GpuComputePercent:
      case ProcessField.GpuCopyPercent:
      case ProcessField.GpuEncodePercent:
      case ProcessField.GpuDecodePercent:
        return Humanize.Percent(Rated(delta, index, field));

      case ProcessField.GpuDedicatedMemory: return Humanize.Bytes(process.GpuDedicatedBytes);
      case ProcessField.GpuSharedMemory: return Humanize.Bytes(process.GpuSharedBytes);
      case ProcessField.GpuTotalMemory: return Humanize.Bytes(GpuTotalMemory(in process));
      case ProcessField.GpuDedicatedMemoryDelta: return Humanize.SignedBytesPerSecond(Rated(delta, index, field));
      case ProcessField.GpuAdapter: return process.GpuAdapter ?? Humanize.Placeholder(process.GpuAdapterReason);
      case ProcessField.GpuEngineName: return GpuEngineName(delta, index);

      case ProcessField.Elevated: return YesNo(process.IsElevated);
      case ProcessField.Integrity:
        return process.IntegrityLevel.HasValue
          ? IntegrityName(process.IntegrityLevel.Value)
          : Humanize.Placeholder(process.IntegrityLevel.Reason);

      case ProcessField.NoNewPrivileges: return YesNo(process.NoNewPrivileges);
      case ProcessField.Seccomp:
        if (!process.SeccompMode.HasValue)
          return Humanize.Placeholder(process.SeccompMode.Reason);

        return process.SeccompMode.Value switch {
          0 => "off",
          1 => "strict",
          2 => "filter",
          _ => "?",
        };

      case ProcessField.SeccompFilters: return Humanize.Count(process.SeccompFilters);

      case ProcessField.Capabilities: return Names(process.EffectiveCapabilities);
      case ProcessField.PermittedCapabilities: return Names(process.PermittedCapabilities);
      case ProcessField.InheritableCapabilities: return Names(process.InheritableCapabilities);
      case ProcessField.BoundingCapabilities: return Names(process.BoundingCapabilities);
      case ProcessField.AmbientCapabilities: return Names(process.AmbientCapabilities);
      case ProcessField.CapabilitiesHex:
        return process.EffectiveCapabilities.HasValue
          ? LinuxCapabilities.Hex(process.EffectiveCapabilities.Value)
          : Humanize.Placeholder(process.EffectiveCapabilities.Reason);

      case ProcessField.SecurityContext:
        return process.SecurityContext ?? Humanize.Placeholder(process.SecurityContextReason);

      case ProcessField.PrivilegeChanged: return YesNo(PrivilegeChanged(in process));
      case ProcessField.EffectiveUserName:
        return process.EffectiveUserName ?? Humanize.Placeholder(UnknownReason.NotPermitted);
      case ProcessField.UserId: return Id(process.UserId);
      case ProcessField.EffectiveUserId: return Id(process.EffectiveUserId);
      case ProcessField.SavedUserId: return Id(process.SavedUserId);
      case ProcessField.FilesystemUserId: return Id(process.FilesystemUserId);
      case ProcessField.GroupId: return Id(process.GroupId);
      case ProcessField.EffectiveGroupId: return Id(process.EffectiveGroupId);
      case ProcessField.SavedGroupId: return Id(process.SavedGroupId);
      case ProcessField.FilesystemGroupId: return Id(process.FilesystemGroupId);
      case ProcessField.SupplementaryGroups:
        // The empty string is a real answer — a process in no supplementary group at all, which is
        // every kernel thread — so it says so rather than leaving a cell that reads like a hole.
        return process.SupplementaryGroups is { } groups
          ? (groups.Length > 0 ? groups : "none")
          : Humanize.Placeholder(process.SupplementaryGroupsReason);

      case ProcessField.ThreadCount: return process.ThreadCount.ToString(CultureInfo.InvariantCulture);
      case ProcessField.HandleCount: return Humanize.Count(process.HandleCount);
      case ProcessField.SocketCount: return Humanize.Count(process.SocketCount);
      case ProcessField.FileCount: return Humanize.Count(process.FileCount);
      case ProcessField.PipeCount: return Humanize.Count(process.PipeCount);
      case ProcessField.Priority: return process.Priority.ToString(CultureInfo.InvariantCulture);
      case ProcessField.Nice: return process.Nice.ToString(CultureInfo.InvariantCulture);
      case ProcessField.Terminal: return Humanize.Terminal(process.TerminalDevice);
      case ProcessField.ExecutableName: return ExecutableName(in process);
      case ProcessField.ContainerId: return Humanize.ContainerId(process.ContainerPath) ?? "—";
      case ProcessField.UniqueSet: return Humanize.Bytes(process.UniqueBytes);
      case ProcessField.SessionId:
        return process.SessionId >= 0 ? process.SessionId.ToString(CultureInfo.InvariantCulture) : "—";
      case ProcessField.StartTime:
        return process.StartTimeUtcTicks > 0
          ? new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
          : "—";
      case ProcessField.Container: return process.ContainerPath ?? "—";
      case ProcessField.ImagePath: return process.ImagePath ?? "—";
      case ProcessField.CommandLine: return process.CommandLine ?? string.Empty;

      // The graphs are drawn, not written. Asking for their text is a caller bug, but returning
      // empty is friendlier than throwing in a render loop.
      case ProcessField.CpuHistory:
      case ProcessField.MemoryHistory:
      case ProcessField.IoHistory:
      default:
        return string.Empty;
    }
  }

  /// <summary>
  /// The field as a plain number, for filtering and for sorting.
  /// </summary>
  /// <returns>
  /// <see langword="null"/> when the field has no number at all — either because it is text, or
  /// because this platform does not report it. A filter must treat those two the same way: a process
  /// whose value is unknown does not match <c>&gt; 0</c>, and it does not match <c>== 0</c> either.
  /// </returns>
  public static double? Number(ProcessField field, in ProcessRecord process, SnapshotDelta? delta, int index) {
    switch (field) {
      case ProcessField.Pid:
      case ProcessField.PidHex: return process.Pid;
      case ProcessField.ParentPid: return process.ParentPid;
      case ProcessField.State: return (byte)process.State;
      case ProcessField.LastCpu: return process.LastCpu >= 0 ? process.LastCpu : null;
      case ProcessField.ThreadCount: return process.ThreadCount;
      // The class as its own kernel number, so sorting groups the real-time tasks together. Unknown
      // has no number at all, which keeps it out of both "== other" and "!= other".
      case ProcessField.SchedulingClass:
        return process.SchedulingPolicy == SchedulingPolicy.Unknown ? null : (byte)process.SchedulingPolicy;
      case ProcessField.CpuThrottled: return Number(process.ThrottledPeriods);
      case ProcessField.Priority: return process.Priority;
      case ProcessField.Nice: return process.Nice;
      case ProcessField.UniqueSet: return Number(process.UniqueBytes);
      case ProcessField.SessionId: return process.SessionId;
      case ProcessField.StartTime: return process.StartTimeUtcTicks;

      case ProcessField.Elevated: return Number(process.IsElevated);
      case ProcessField.Integrity: return Number(process.IntegrityLevel);
      case ProcessField.Seccomp: return Number(process.SeccompMode);
      case ProcessField.SeccompFilters: return Number(process.SeccompFilters);
      case ProcessField.NoNewPrivileges: return Number(process.NoNewPrivileges);
      // The mask as a magnitude. Not a quantity anybody adds up, but ordering by it groups the
      // privileged rows together, which is what sorting a security column is for.
      case ProcessField.Capabilities:
      case ProcessField.CapabilitiesHex: return Number(process.EffectiveCapabilities);
      case ProcessField.PermittedCapabilities: return Number(process.PermittedCapabilities);
      case ProcessField.InheritableCapabilities: return Number(process.InheritableCapabilities);
      case ProcessField.BoundingCapabilities: return Number(process.BoundingCapabilities);
      case ProcessField.AmbientCapabilities: return Number(process.AmbientCapabilities);

      case ProcessField.PrivilegeChanged: return Number(PrivilegeChanged(in process));
      // -1 is "nobody told us", not user minus one, so it has no number at all and a filter cannot
      // match it either way (PRD §3.4).
      case ProcessField.UserId: return process.UserId >= 0 ? process.UserId : null;
      case ProcessField.EffectiveUserId: return process.EffectiveUserId >= 0 ? process.EffectiveUserId : null;
      case ProcessField.SavedUserId: return process.SavedUserId >= 0 ? process.SavedUserId : null;
      case ProcessField.FilesystemUserId: return process.FilesystemUserId >= 0 ? process.FilesystemUserId : null;
      case ProcessField.GroupId: return process.GroupId >= 0 ? process.GroupId : null;
      case ProcessField.EffectiveGroupId: return process.EffectiveGroupId >= 0 ? process.EffectiveGroupId : null;
      case ProcessField.SavedGroupId: return process.SavedGroupId >= 0 ? process.SavedGroupId : null;
      case ProcessField.FilesystemGroupId: return process.FilesystemGroupId >= 0 ? process.FilesystemGroupId : null;

      case ProcessField.CpuTime: return Number(process.CpuTimeNs);
      case ProcessField.UserTime: return Number(process.UserTimeNs);
      case ProcessField.KernelTime: return Number(process.KernelTimeNs);
      case ProcessField.ProportionalSet: return Number(process.ProportionalBytes);
      case ProcessField.ProportionalSwap: return Number(process.ProportionalSwapBytes);
      case ProcessField.FileBackedSet: return Number(process.FileBackedBytes);
      case ProcessField.SharedSet: return Number(process.SharedResidentBytes);
      case ProcessField.PrivateBytes: return Number(process.PrivateBytes);
      case ProcessField.PrivateWorkingSet: return Number(process.PrivateWorkingSetBytes);
      case ProcessField.WorkingSetBytes: return Number(process.WorkingSetBytes);
      case ProcessField.PeakWorkingSet: return Number(process.PeakWorkingSetBytes);
      case ProcessField.VirtualBytes: return Number(process.VirtualBytes);
      case ProcessField.PeakVirtualBytes: return Number(process.PeakVirtualBytes);
      case ProcessField.PagedPool: return Number(process.PagedPoolBytes);
      case ProcessField.PeakPagedPool: return Number(process.PeakPagedPoolBytes);
      case ProcessField.NonPagedPool: return Number(process.NonPagedPoolBytes);
      case ProcessField.PeakNonPagedPool: return Number(process.PeakNonPagedPoolBytes);
      case ProcessField.Swap: return Number(process.SwapBytes);
      case ProcessField.HandleCount: return Number(process.HandleCount);
      case ProcessField.SocketCount: return Number(process.SocketCount);
      case ProcessField.FileCount: return Number(process.FileCount);
      case ProcessField.PipeCount: return Number(process.PipeCount);

      case ProcessField.GpuDedicatedMemory: return Number(process.GpuDedicatedBytes);
      case ProcessField.GpuSharedMemory: return Number(process.GpuSharedBytes);
      case ProcessField.GpuTotalMemory: return Number(GpuTotalMemory(in process));
      // The engine sorts by its own identity, so grouping a table by it is one click. Unknown has no
      // number at all, which keeps it out of both "== compute" and "!= compute".
      case ProcessField.GpuEngineName: {
        var engine = delta?.BusiestGpuEngine(index) ?? GpuEngine.Unknown;
        return engine == GpuEngine.Unknown ? null : (byte)engine;
      }

      case ProcessField.GpuPercent:
      case ProcessField.GpuEnginePercent:
      case ProcessField.GpuGraphicsPercent:
      case ProcessField.GpuComputePercent:
      case ProcessField.GpuCopyPercent:
      case ProcessField.GpuEncodePercent:
      case ProcessField.GpuDecodePercent:
      case ProcessField.GpuDedicatedMemoryDelta:
      case ProcessField.CpuPercent:
      case ProcessField.CpuPercentPerCore:
      case ProcessField.CpuPercentDelta:
      case ProcessField.MemoryPercent:
      case ProcessField.CyclesDelta:
      case ProcessField.ContextSwitchesDelta:
      case ProcessField.PageFaultsDelta:
      case ProcessField.PrivateBytesDelta:
      case ProcessField.IoTotalRate:
      case ProcessField.ReadBytesPerSecond:
      case ProcessField.WriteBytesPerSecond: {
        var rate = Rated(delta, index, field);
        return rate.HasValue ? rate.Value : null;
      }

      default: return null;
    }
  }

  /// <summary>The field as raw text, for substring and regular-expression filtering.</summary>
  /// <remarks>
  /// Deliberately not <see cref="Text"/>: a filter must match what the value <em>is</em>, not how it
  /// was abbreviated for a column. Searching for a path should not fail because the column showed
  /// an em dash, and searching "1024" should not match a cell that reads "1.0K".
  /// </remarks>
  /// <param name="delta">
  /// Needed by the few fields whose text is derived rather than stored — the busiest GPU engine is
  /// named by comparing rates, and there is nowhere in a single sample for that name to live. Left
  /// out by a caller that has no delta, which then reads those fields as having no text, the same
  /// answer they give before a second sample exists.
  /// </param>
  public static string? RawText(
    ProcessField field,
    in ProcessRecord process,
    SnapshotDelta? delta = null,
    int index = 0
  ) => field switch {
    ProcessField.Name => process.Name,
    ProcessField.ParentName => process.ParentName,
    ProcessField.UserName => process.UserName,
    ProcessField.ImagePath => process.ImagePath,
    ProcessField.CommandLine => process.CommandLine,
    ProcessField.Container => process.ContainerPath,
    ProcessField.ContainerId => Humanize.ContainerId(process.ContainerPath),
    ProcessField.ExecutableName => process.ImagePath is { Length: > 0 } ? ExecutableName(in process) : null,
    ProcessField.Terminal => process.TerminalDevice == 0 ? null : Humanize.Terminal(process.TerminalDevice),
    ProcessField.State => Humanize.State(process.State),
    // The security states are matched by the word they show, so "elevated:yes" reads the way it
    // would be said aloud. The numeric form still works, because Number covers them too.
    ProcessField.Elevated => Word(process.IsElevated),
    ProcessField.Integrity => process.IntegrityLevel.HasValue ? IntegrityName(process.IntegrityLevel.Value) : null,
    ProcessField.NoNewPrivileges => Word(process.NoNewPrivileges),
    ProcessField.Seccomp => process.SeccompMode.HasValue
      ? process.SeccompMode.Value switch { 0 => "off", 1 => "strict", 2 => "filter", _ => null }
      : null,
    ProcessField.SecurityContext => process.SecurityContext,
    // The kernel's own spelling, which is what "sched.class:SCHED_FIFO" is written as and what chrt
    // prints. Unknown has no text, so it matches neither that nor its negation.
    ProcessField.SchedulingClass => process.SchedulingPolicy == SchedulingPolicy.Unknown
      ? null
      : Humanize.SchedulingPolicy(process.SchedulingPolicy),
    ProcessField.CpuAffinity => process.CpuAffinity,
    ProcessField.GpuAdapter => process.GpuAdapter,
    ProcessField.GpuEngineName => delta?.BusiestGpuEngine(index) is { } engine and not GpuEngine.Unknown
      ? EngineName(engine)
      : null,
    ProcessField.PrivilegeChanged => Word(PrivilegeChanged(in process)),
    ProcessField.EffectiveUserName => process.EffectiveUserName,
    // Empty is a real answer and null is not one, so the two must not collapse: a filter for a group
    // must miss a process that is in none, rather than miss one nobody could read.
    ProcessField.SupplementaryGroups => process.SupplementaryGroups,
    // The names, not the mask: "caps:cap_net_admin" is the question somebody actually has, and a
    // substring search over sixteen hex digits answers nothing. The raw form is its own field.
    // Textual at all because without this the column rendered a value on screen and exported an
    // empty cell — the exporter asks only RawText for a field of textual kind.
    ProcessField.Capabilities => Words(process.EffectiveCapabilities),
    ProcessField.PermittedCapabilities => Words(process.PermittedCapabilities),
    ProcessField.InheritableCapabilities => Words(process.InheritableCapabilities),
    ProcessField.BoundingCapabilities => Words(process.BoundingCapabilities),
    ProcessField.AmbientCapabilities => Words(process.AmbientCapabilities),
    ProcessField.CapabilitiesHex => process.EffectiveCapabilities.HasValue
      ? LinuxCapabilities.Hex(process.EffectiveCapabilities.Value)
      : null,
    ProcessField.PidHex => "0x" + process.Pid.ToString("X", CultureInfo.InvariantCulture),
    ProcessField.Pid => process.Pid.ToString(CultureInfo.InvariantCulture),
    ProcessField.ParentPid => process.ParentPid.ToString(CultureInfo.InvariantCulture),
    _ => null,
  };

  /// <summary>
  /// Orders two rows by one field. Text compares case-insensitively; numbers compare numerically;
  /// a value that is unknown sorts below every known one, whichever direction is chosen.
  /// </summary>
  public static int Compare(
    ProcessField field,
    in ProcessRecord a,
    int indexA,
    in ProcessRecord b,
    int indexB,
    SnapshotDelta? delta
  ) {
    switch (field) {
      case ProcessField.Name:
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ParentName:
        return string.Compare(a.ParentName, b.ParentName, StringComparison.OrdinalIgnoreCase);
      case ProcessField.UserName:
        return string.Compare(a.UserName, b.UserName, StringComparison.OrdinalIgnoreCase);
      case ProcessField.EffectiveUserName:
        return string.Compare(a.EffectiveUserName, b.EffectiveUserName, StringComparison.OrdinalIgnoreCase);
      case ProcessField.SupplementaryGroups:
        return string.Compare(a.SupplementaryGroups, b.SupplementaryGroups, StringComparison.Ordinal);
      // Ordinal, so "0-15" and "15" sort apart rather than both compared as the numbers they are
      // not: an affinity list is a set, and the only order it has is its spelling.
      case ProcessField.CpuAffinity:
        return string.Compare(a.CpuAffinity, b.CpuAffinity, StringComparison.Ordinal);
      case ProcessField.CommandLine:
        return string.Compare(a.CommandLine, b.CommandLine, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ImagePath:
        return string.Compare(a.ImagePath, b.ImagePath, StringComparison.OrdinalIgnoreCase);
      case ProcessField.Container:
        return string.Compare(a.ContainerPath, b.ContainerPath, StringComparison.OrdinalIgnoreCase);
    }

    var left = Number(field, in a, delta, indexA);
    var right = Number(field, in b, delta, indexB);
    if (left is null)
      return right is null ? 0 : -1;
    if (right is null)
      return 1;

    return left.Value.CompareTo(right.Value);
  }

  private static double? Number(Counter counter) => counter.HasValue ? counter.Value : null;

  /// <summary>
  /// The well-known mandatory integrity levels, by name.
  /// </summary>
  /// <remarks>
  /// A level Microsoft adds later shows as its number rather than being flattened into the nearest
  /// name we happen to know — "0x2800" is a true statement and "medium" would not be.
  /// </remarks>
  private static string IntegrityName(ulong level) => level switch {
    0x0000 => "untrusted",
    0x1000 => "low",
    0x2000 => "medium",
    0x2100 => "medium+",
    0x3000 => "high",
    0x4000 => "system",
    0x5000 => "protected",
    _ => "0x" + level.ToString("x", CultureInfo.InvariantCulture),
  };

  /// <summary>A capability mask by name, or the reason there is none.</summary>
  private static string Names(Counter mask)
    => mask.HasValue ? LinuxCapabilities.Describe(mask.Value) : Humanize.Placeholder(mask.Reason);

  /// <summary>
  /// The same for filtering: never a placeholder, and never abbreviated.
  /// </summary>
  /// <remarks>
  /// A filter must match what the value is rather than how the column shortened it, so a search for
  /// <c>cap_sys_module</c> cannot miss the root processes that hold it precisely because they hold
  /// every capability there is and the column says "all".
  /// </remarks>
  private static string? Words(Counter mask)
    => mask.HasValue ? LinuxCapabilities.List(mask.Value) : null;

  /// <summary>A numeric identity, or the mark for the platform declining to say who it is.</summary>
  /// <remarks>
  /// -1 is the whole point: zero is root, so rendering an unfilled id as its number would name the
  /// superuser for every process on a platform that does not report the quartet at all.
  /// <para>
  /// An <see langword="int"/> cannot carry <em>why</em> the way a <see cref="Counter"/> does, so the
  /// one reason it can give is the one that is nearly always true — the quartet is a Unix idea and
  /// the platforms that have no answer are the ones that have no such thing. A
  /// <c>hidepid</c> mount, where the file exists and this user may not read it, would be better
  /// served by the other mark; that is the cost of holding these as plain numbers, and the
  /// alternative is eight more counters in a struct that is refreshed a thousand times a second.
  /// </para>
  /// </remarks>
  private static string Id(int id) => id >= 0
    ? id.ToString(CultureInfo.InvariantCulture)
    : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

  /// <summary>
  /// Whether the process is running as somebody other than whoever started it.
  /// </summary>
  /// <remarks>
  /// Real against effective, for the user and for the group: a set-group-ID binary is the same kind
  /// of thing as a set-user-ID one and hiding it under a field named for the more famous half would
  /// be the false equivalence §5.3 forbids. Unknown when either half is, because "they are the same"
  /// is a claim and an absence is not evidence for it.
  /// </remarks>
  private static Counter PrivilegeChanged(in ProcessRecord process) {
    if (process.UserId < 0 || process.EffectiveUserId < 0)
      return Counter.NotSupported;

    if (process.UserId != process.EffectiveUserId)
      return Counter.Of(1ul);

    if (process.GroupId < 0 || process.EffectiveGroupId < 0)
      return Counter.NotSupported;

    return Counter.Of(process.GroupId != process.EffectiveGroupId ? 1ul : 0ul);
  }

  /// <summary>A yes/no counter as the word, or the reason there is no answer.</summary>
  private static string YesNo(Counter counter)
    => counter.HasValue ? (counter.Value != 0 ? "yes" : "no") : Humanize.Placeholder(counter.Reason);

  /// <summary>The same, but <see langword="null"/> rather than a placeholder, for filtering.</summary>
  private static string? Word(Counter counter)
    => counter.HasValue ? (counter.Value != 0 ? "yes" : "no") : null;

  /// <summary>
  /// The file that is running, which is not always what the process calls itself.
  /// </summary>
  /// <remarks>
  /// A process can rename itself and many do — a browser's helpers, anything using an interpreter,
  /// anything that rewrote its own argv. The name is what it claims; this is what it is.
  /// </remarks>
  private static string ExecutableName(in ProcessRecord process) {
    if (process.ImagePath is not { Length: > 0 } path)
      return "—";

    var slash = path.LastIndexOf('/');
    if (slash < 0)
      slash = path.LastIndexOf('\\');

    return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
  }

  #region graphics (PRD §19)

  /// <summary>
  /// Dedicated and shared adapter memory together, or the reason there is no total.
  /// </summary>
  /// <remarks>
  /// A card that reports only one of the two still has a total, and it is the half that is known:
  /// an integrated part has no dedicated memory to report and a discrete one under NVML publishes no
  /// system-memory figure, so insisting on both would leave the column empty on every machine there
  /// is. Only when neither is known is there nothing to add, and the reason travels from whichever
  /// half was asked first.
  /// </remarks>
  private static Counter GpuTotalMemory(in ProcessRecord process) {
    var dedicated = process.GpuDedicatedBytes;
    var shared = process.GpuSharedBytes;
    if (!dedicated.HasValue && !shared.HasValue)
      return dedicated;

    return Counter.Of(dedicated.GetValueOrDefault() + shared.GetValueOrDefault());
  }

  /// <summary>
  /// The busiest engine's name, or why there is none.
  /// </summary>
  /// <remarks>
  /// An engine of <see cref="GpuEngine.Unknown"/> beside a real percentage means the process is
  /// using none of the adapter, and an empty cell says that better than a word would. Beside a
  /// percentage that is itself unknown it means nobody could tell, and the cell carries that reason
  /// rather than pretending the process is idle (PRD §72.3).
  /// </remarks>
  private static string GpuEngineName(SnapshotDelta? delta, int index) {
    if (delta is null)
      return Humanize.Placeholder(UnknownReason.NotSampledYet);

    var busiest = delta.GpuEnginePercent(index);
    if (!busiest.HasValue)
      return Humanize.Placeholder(busiest.Reason);

    var engine = delta.BusiestGpuEngine(index);
    return engine == GpuEngine.Unknown ? string.Empty : EngineName(engine);
  }

  private static string EngineName(GpuEngine engine) => engine switch {
    GpuEngine.Graphics => "3D",
    GpuEngine.Compute => "compute",
    GpuEngine.Copy => "copy",
    GpuEngine.Encode => "encode",
    GpuEngine.Decode => "decode",
    _ => string.Empty,
  };

  #endregion

  private static Rate Rated(SnapshotDelta? delta, int index, ProcessField field) {
    if (delta is null)
      return Rate.NotSampledYet;

    return field switch {
      ProcessField.CpuPercent => delta.CpuPercent(index),
      ProcessField.MemoryPercent => delta.MemoryPercent(index),
      ProcessField.CpuPercentPerCore => delta.CpuPercentPerCore(index),
      ProcessField.CpuPercentDelta => delta.CpuPercentDelta(index),
      ProcessField.CyclesDelta => delta.CyclesPerSecond(index),
      ProcessField.ContextSwitchesDelta => delta.ContextSwitchesPerSecond(index),
      ProcessField.PageFaultsDelta => delta.PageFaultsPerSecond(index),
      ProcessField.PrivateBytesDelta => delta.PrivateBytesDelta(index),
      ProcessField.IoTotalRate => delta.IoTotalBytesPerSecond(index),
      ProcessField.ReadBytesPerSecond => delta.ReadBytesPerSecond(index),
      ProcessField.WriteBytesPerSecond => delta.WriteBytesPerSecond(index),
      ProcessField.GpuPercent => delta.GpuPercent(index),
      ProcessField.GpuEnginePercent => delta.GpuEnginePercent(index),
      ProcessField.GpuGraphicsPercent => delta.GpuGraphicsPercent(index),
      ProcessField.GpuComputePercent => delta.GpuComputePercent(index),
      ProcessField.GpuCopyPercent => delta.GpuCopyPercent(index),
      ProcessField.GpuEncodePercent => delta.GpuEncodePercent(index),
      ProcessField.GpuDecodePercent => delta.GpuDecodePercent(index),
      ProcessField.GpuDedicatedMemoryDelta => delta.GpuDedicatedBytesDelta(index),
      _ => Rate.NotSampledYet,
    };
  }

}
