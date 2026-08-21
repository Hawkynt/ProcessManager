using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// What a process's cgroup allows it, from <c>/sys/fs/cgroup</c> (PRD §38).
/// </summary>
/// <remarks>
/// <para>
/// The answer to "why is this slow when the machine is idle". A container or a systemd unit can be
/// throttled to a fraction of a core or capped well below the machine's memory, and a process table
/// shows none of that — the process simply appears to be doing less than it should.
/// </para>
/// <para>
/// On demand, never from the sampling path: a dozen small files per process is affordable once for
/// the process being looked at and indefensible four hundred times a second (PRD §5.4).
/// </para>
/// </remarks>
internal static class CgroupReader {

  /// <summary>
  /// Reads one process's cgroup, or null where there is nothing to read.
  /// </summary>
  /// <param name="cgroupRoot">Where the unified hierarchy is mounted.</param>
  /// <param name="path">The cgroup path from <c>/proc/[pid]/cgroup</c>.</param>
  public static CgroupInfo? Read(string cgroupRoot, string? path) {
    if (path is not { Length: > 0 })
      return null;

    // The path always begins with a slash and is relative to the mount point; joining it directly
    // would make Path.Combine discard the root and read from the filesystem root instead.
    var directory = Path.Combine(cgroupRoot, path.TrimStart('/'));
    if (!Directory.Exists(directory))
      return null;

    return new(
      path,
      Words(ReadText(directory, "cgroup.controllers")),
      Bytes(ReadText(directory, "memory.current")),
      Bytes(ReadText(directory, "memory.max")),
      Bytes(ReadText(directory, "memory.high")),
      Number(ReadText(directory, "pids.current")),
      Number(ReadText(directory, "pids.max")),
      Cores(ReadText(directory, "cpu.max")),
      Throttled(ReadText(directory, "cpu.stat")),
      Pressure(directory, "cpu.pressure"),
      Pressure(directory, "memory.pressure"),
      Pressure(directory, "io.pressure"),
      Freezer(directory)
    );
  }

  /// <summary>
  /// Whether this cgroup is frozen, and whether it can be (PRD §38).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The state comes from <c>cgroup.events</c> rather than from <c>cgroup.freeze</c>: the first is
  /// what the cgroup <em>is</em> and the second is what it was <em>asked</em> to be. They differ
  /// while a freeze is still catching processes that were in a syscall when it began, and the
  /// question a caller is asking — "is it stopped" — is the first one.
  /// </para>
  /// <para>
  /// A cgroup with no <c>cgroup.freeze</c> file at all is on a kernel before 5.2, and reports that
  /// it cannot be frozen rather than that it is not frozen (PRD §5.3).
  /// </para>
  /// </remarks>
  private static CgroupFreezer Freezer(string directory) {
    var supported = File.Exists(Path.Combine(directory, "cgroup.freeze"));
    if (!supported)
      return new(false, false);

    foreach (var line in (ReadText(directory, "cgroup.events") ?? string.Empty).Split('\n'))
      if (line.StartsWith("frozen ", StringComparison.Ordinal))
        return new(true, line.AsSpan("frozen ".Length).Trim() is "1");

    // The file is there and says nothing about being frozen, which is what an older layout of
    // cgroup.events looks like. What it was asked to be is then the best answer available.
    return new(true, ReadText(directory, "cgroup.freeze") is "1");
  }

  /// <summary>
  /// <c>cpu.max</c> is "quota period" in microseconds, or "max period" for no limit.
  /// </summary>
  /// <remarks>
  /// Converted to a number of cores because that is the sentence somebody wants — "half a core" —
  /// and because the raw pair is meaningless without dividing it anyway. No limit returns null
  /// rather than a very large number: unlimited is not a quantity, and a caller that formatted one
  /// would print something absurd.
  /// </remarks>
  private static double? Cores(string? text) {
    if (text is not { Length: > 0 })
      return null;

    var space = text.IndexOf(' ');
    if (space <= 0)
      return null;

    var quota = text[..space];
    if (quota is "max")
      return null;

    return double.TryParse(quota, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
      && double.TryParse(text[(space + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var period)
      && period > 0
        ? microseconds / period
        : null;
  }

  /// <summary>
  /// How many times this cgroup has been stopped for exceeding its CPU quota.
  /// </summary>
  /// <remarks>
  /// The number that turns "it is slow" into "it is being throttled". Parsed in Core rather than
  /// here, because the column of §15 reads the same line for the same reason and the panel and the
  /// column must not be able to disagree about it (PRD §5.1).
  /// </remarks>
  private static Counter Throttled(string? text)
    => text is { Length: > 0 } ? CgroupCpuStatParser.Throttled(text) : Counter.NotSupported;

  /// <summary>
  /// A byte figure, where <c>max</c> means no limit.
  /// </summary>
  /// <remarks>
  /// "max" is reported as not-supported rather than as a very large number. A memory limit of
  /// 9223372036854771712 bytes is what the file literally says on some kernels and is not a limit;
  /// printing it would put an absurd figure in front of somebody looking for a real one.
  /// </remarks>
  private static Counter Bytes(string? text) {
    // No file at all: this cgroup has no such controller, so the question does not apply here and
    // whatever an ancestor sets is what governs.
    if (text is not { Length: > 0 })
      return Counter.NotSupported;

    // The file exists and says there is no ceiling. That is an answer, and a different one — telling
    // a reader the machine could not say would be wrong twice over.
    if (text is "max")
      return Counter.Unknown(UnknownReason.NoLimit);

    return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
      ? Counter.Of(value)
      : Counter.Unknown(UnknownReason.CounterInvalid);
  }

  /// <summary>
  /// A plain count, where <c>max</c> means no limit.
  /// </summary>
  /// <remarks>
  /// The same shape as a byte limit, and deliberately the same code: <c>pids.max</c> is written
  /// exactly like <c>memory.max</c>, including the literal "max", and a separate parser would only
  /// be a second place to forget that.
  /// </remarks>
  private static Counter Number(string? text) => Bytes(text);

  private static PressureReading Pressure(string directory, string file) {
    var text = ReadText(directory, file);
    return text is { Length: > 0 } ? PressureParser.Parse(text) : PressureReading.Unknown;
  }

  private static IReadOnlyList<string> Words(string? text)
    => text is { Length: > 0 } ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];

  private static string? ReadText(string directory, string file) {
    try {
      var path = Path.Combine(directory, file);
      return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

}
