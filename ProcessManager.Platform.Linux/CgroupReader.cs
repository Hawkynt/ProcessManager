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
  /// <param name="blockDeviceRoot">
  /// Where a major and minor number can be turned into a device name — <c>/sys/dev/block</c> on a
  /// real machine. A root that is not there leaves the numbers unnamed rather than failing: a
  /// device whose name could not be looked up is still a device with a limit on it.
  /// </param>
  public static CgroupInfo? Read(string cgroupRoot, string? path, string? blockDeviceRoot) {
    if (path is not { Length: > 0 })
      return null;

    // The path always begins with a slash and is relative to the mount point; joining it directly
    // would make Path.Combine discard the root and read from the filesystem root instead.
    var directory = Path.Combine(cgroupRoot, path.TrimStart('/'));
    if (!Directory.Exists(directory))
      return null;

    var (limits, reason) = IoLimits(directory, blockDeviceRoot);
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
      Freezer(directory),
      limits,
      reason,
      Hierarchy(cgroupRoot, path, blockDeviceRoot)
    );
  }

  /// <summary>
  /// Every cgroup from the root down to this one, outermost first (PRD §38).
  /// </summary>
  /// <remarks>
  /// <para>
  /// A limit on an ancestor governs everything below it, and the group a process is actually in
  /// very often sets nothing at all — a desktop application's own scope carries no <c>cpu.max</c>
  /// and is nonetheless inside whatever <c>user.slice</c> was given. Reading one directory answers
  /// "no limit" about a process that is being held to a fraction of a core two levels up.
  /// </para>
  /// <para>
  /// Four small files a level and rarely more than five levels, on demand only: the same budget the
  /// rest of this reader spends, and for the question the page exists to answer (PRD §5.4). A level
  /// whose directory has gone between one read and the next is skipped rather than failing the lot —
  /// cgroups are removed the moment their last process leaves.
  /// </para>
  /// </remarks>
  private static IReadOnlyList<CgroupLevel> Hierarchy(string cgroupRoot, string path, string? blockDeviceRoot) {
    var levels = new List<CgroupLevel>();
    var trimmed = path.Trim('/');

    // The root itself first. It is a real level with real files, and it is the one place a machine's
    // own global settings would show up.
    Add(levels, cgroupRoot, "/", blockDeviceRoot);

    var walked = string.Empty;
    foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      walked = walked + "/" + segment;
      Add(levels, Path.Combine(cgroupRoot, walked.TrimStart('/')), walked, blockDeviceRoot);
    }

    return levels;
  }

  private static void Add(List<CgroupLevel> levels, string directory, string path, string? blockDeviceRoot) {
    if (!Directory.Exists(directory))
      return;

    var (limits, reason) = IoLimits(directory, blockDeviceRoot);
    var quota = ReadText(directory, "cpu.max");
    levels.Add(new(
      path,
      CgroupUnit.Of(path),
      Words(ReadText(directory, "cgroup.controllers")),
      Cores(quota),
      Bytes(ReadText(directory, "memory.max")),
      Bytes(ReadText(directory, "memory.high")),
      Number(ReadText(directory, "pids.max")),
      limits,
      reason,
      QuotaReason(quota)
    ));
  }

  /// <summary>
  /// Why a level has no processor quota, when it has none (PRD §38).
  /// </summary>
  /// <remarks>
  /// <see cref="Cores"/> answers null four different ways and only two of them are answers. No file
  /// means the controller is not enabled here; the literal word <c>max</c> means somebody left it
  /// unbounded; a line that will not parse and a period of nought are holes. A chain that reported
  /// all four as "no quota anywhere" would tell a reader nothing is holding their process back on
  /// the strength of a file nobody could read.
  /// </remarks>
  private static UnknownReason QuotaReason(string? text) {
    if (text is not { Length: > 0 })
      return UnknownReason.NotSupportedOnPlatform;

    var space = text.IndexOf(' ');
    if (space <= 0)
      return UnknownReason.CounterInvalid;

    if (text[..space] is "max")
      return UnknownReason.NoLimit;

    return Cores(text) is null ? UnknownReason.CounterInvalid : UnknownReason.None;
  }

  /// <summary>
  /// What each device is allowed here, from <c>io.max</c>.
  /// </summary>
  /// <remarks>
  /// The distinction the pair carries is the one this whole page is careful about: no file at all
  /// means the I/O controller is not enabled here and an ancestor's throttling is what governs; a
  /// file with nothing in it means the controller is on and nothing has been capped. Both are an
  /// empty list and they are not the same statement (PRD §5.3).
  /// </remarks>
  private static (IReadOnlyList<CgroupIoLimit> Limits, UnknownReason Reason) IoLimits(
    string directory,
    string? blockDeviceRoot
  ) {
    if (ReadText(directory, "io.max") is not { } text)
      return ([], UnknownReason.NotSupportedOnPlatform);

    var parsed = CgroupIoMaxParser.Parse(text);
    if (parsed.Count == 0)
      return ([], UnknownReason.NoLimit);

    var named = new List<CgroupIoLimit>(parsed.Count);
    foreach (var limit in parsed)
      named.Add(limit with { Device = DeviceName(blockDeviceRoot, limit.Major, limit.Minor) });

    return (named, UnknownReason.None);
  }

  /// <summary>
  /// The name behind a major and minor number, from <c>/sys/dev/block</c>.
  /// </summary>
  /// <remarks>
  /// A symlink whose target's last component is the name: <c>259:0</c> points at
  /// <c>../../devices/.../nvme0n1</c>. Resolved rather than read, because the link is the whole of
  /// the answer. Null where the machine has no such directory or the device is not in it, which is
  /// a name that could not be looked up rather than a device that has none — the numbers are shown
  /// instead, and they are what the kernel itself calls it.
  /// </remarks>
  private static string? DeviceName(string? blockDeviceRoot, int major, int minor) {
    if (blockDeviceRoot is not { Length: > 0 } root || !Directory.Exists(root))
      return null;

    try {
      var entry = Path.Combine(root, $"{major.ToString(CultureInfo.InvariantCulture)}:{minor.ToString(CultureInfo.InvariantCulture)}");
      var target = Directory.ResolveLinkTarget(entry, returnFinalTarget: true);
      return target is null ? null : Path.GetFileName(target.FullName) is { Length: > 0 } name ? name : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
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
