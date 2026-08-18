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
      case ProcessField.UserName:
        return process.UserName ?? Humanize.Placeholder(UnknownReason.NotPermitted);
      case ProcessField.State: return Humanize.State(process.State);

      case ProcessField.CpuPercent: return Humanize.Percent(Rated(delta, index, field));
      case ProcessField.CpuPercentPerCore: return Humanize.Percent(Rated(delta, index, field));
      case ProcessField.CpuTime: return Humanize.Duration(process.CpuTimeNs);
      case ProcessField.LastCpu:
        // -1 is the platform declining to say, not processor number minus one.
        return process.LastCpu >= 0
          ? process.LastCpu.ToString(CultureInfo.InvariantCulture)
          : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

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

      case ProcessField.Capabilities:
        return process.EffectiveCapabilities.HasValue
          ? "0x" + process.EffectiveCapabilities.Value.ToString("x", CultureInfo.InvariantCulture)
          : Humanize.Placeholder(process.EffectiveCapabilities.Reason);

      case ProcessField.SecurityContext:
        return process.SecurityContext ?? Humanize.Placeholder(process.SecurityContextReason);

      case ProcessField.ThreadCount: return process.ThreadCount.ToString(CultureInfo.InvariantCulture);
      case ProcessField.HandleCount: return Humanize.Count(process.HandleCount);
      case ProcessField.Priority: return process.Priority.ToString(CultureInfo.InvariantCulture);
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
      case ProcessField.Priority: return process.Priority;
      case ProcessField.SessionId: return process.SessionId;
      case ProcessField.StartTime: return process.StartTimeUtcTicks;

      case ProcessField.Elevated: return Number(process.IsElevated);
      case ProcessField.Integrity: return Number(process.IntegrityLevel);
      case ProcessField.Seccomp: return Number(process.SeccompMode);
      case ProcessField.NoNewPrivileges: return Number(process.NoNewPrivileges);
      case ProcessField.Capabilities: return Number(process.EffectiveCapabilities);

      case ProcessField.CpuTime: return Number(process.CpuTimeNs);
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

      case ProcessField.CpuPercent:
      case ProcessField.CpuPercentPerCore:
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
  public static string? RawText(ProcessField field, in ProcessRecord process) => field switch {
    ProcessField.Name => process.Name,
    ProcessField.UserName => process.UserName,
    ProcessField.ImagePath => process.ImagePath,
    ProcessField.CommandLine => process.CommandLine,
    ProcessField.Container => process.ContainerPath,
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
      case ProcessField.UserName:
        return string.Compare(a.UserName, b.UserName, StringComparison.OrdinalIgnoreCase);
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

  /// <summary>A yes/no counter as the word, or the reason there is no answer.</summary>
  private static string YesNo(Counter counter)
    => counter.HasValue ? (counter.Value != 0 ? "yes" : "no") : Humanize.Placeholder(counter.Reason);

  /// <summary>The same, but <see langword="null"/> rather than a placeholder, for filtering.</summary>
  private static string? Word(Counter counter)
    => counter.HasValue ? (counter.Value != 0 ? "yes" : "no") : null;

  private static Rate Rated(SnapshotDelta? delta, int index, ProcessField field) {
    if (delta is null)
      return Rate.NotSampledYet;

    return field switch {
      ProcessField.CpuPercent => delta.CpuPercent(index),
      ProcessField.CpuPercentPerCore => delta.CpuPercentPerCore(index),
      ProcessField.CyclesDelta => delta.CyclesPerSecond(index),
      ProcessField.ContextSwitchesDelta => delta.ContextSwitchesPerSecond(index),
      ProcessField.PageFaultsDelta => delta.PageFaultsPerSecond(index),
      ProcessField.PrivateBytesDelta => delta.PrivateBytesDelta(index),
      ProcessField.IoTotalRate => delta.IoTotalBytesPerSecond(index),
      ProcessField.ReadBytesPerSecond => delta.ReadBytesPerSecond(index),
      ProcessField.WriteBytesPerSecond => delta.WriteBytesPerSecond(index),
      _ => Rate.NotSampledYet,
    };
  }

}
