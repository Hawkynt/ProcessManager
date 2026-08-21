using System.Globalization;
using System.Runtime.InteropServices;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The kernel's per-process ceilings, named once (PRD §25.2).
/// </summary>
/// <remarks>
/// <para>
/// One catalogue rather than a list in each front-end, the way <see cref="SchedulingClasses"/> and
/// <see cref="Signals"/> are: the dialog, the report and the command line read from here, so a limit
/// is spelled the same everywhere and a limit added to the kernel appears in all three or in none.
/// </para>
/// <para>
/// <b>The numbers are the ABI's and are not universal.</b> Alpha, MIPS and SPARC renumber the middle
/// of the table — <c>RLIMIT_NOFILE</c> is 7 here and 5 on MIPS, where 5 is <c>RLIMIT_NPROC</c> — so
/// an architecture whose layout is not known refuses rather than reading one ceiling and calling it
/// another (PRD §5.3). Same rule as the signal numbers, for the same reason.
/// </para>
/// </remarks>
public static class ResourceLimits {

  /// <summary>
  /// One limit: what the kernel calls it, its <c>RLIMIT_</c> number, what it measures, and what
  /// running into it actually does.
  /// </summary>
  /// <param name="Consequence">
  /// What happens when the process reaches it — which is never the same thing twice.
  /// <c>RLIMIT_CPU</c> sends a signal, <c>RLIMIT_NOFILE</c> fails an <c>open</c>, and
  /// <c>RLIMIT_AS</c> fails an allocation the program probably does not check. A sheet of numbers
  /// without these is a sheet nobody can act on.
  /// </param>
  public readonly record struct Definition(
    string Name,
    ResourceLimitKind Kind,
    int Number,
    ResourceLimitUnit Unit,
    string Consequence
  );

  /// <summary>
  /// The sixteen ceilings, in the kernel's own order — which is the order <c>/proc/[pid]/limits</c>
  /// prints them in, so the two can be read side by side.
  /// </summary>
  private static readonly Definition[] _Generic = [
    new("RLIMIT_CPU", ResourceLimitKind.CpuTime, 0, ResourceLimitUnit.Seconds,
      "at the soft limit the kernel sends SIGXCPU once a second; at the hard limit it sends SIGKILL"),
    new("RLIMIT_FSIZE", ResourceLimitKind.FileSize, 1, ResourceLimitUnit.Bytes,
      "a write past it fails with EFBIG and the process is sent SIGXFSZ"),
    new("RLIMIT_DATA", ResourceLimitKind.DataSize, 2, ResourceLimitUnit.Bytes,
      "brk and anonymous mmap fail with ENOMEM once the data segment reaches it"),
    new("RLIMIT_STACK", ResourceLimitKind.StackSize, 3, ResourceLimitUnit.Bytes,
      "the main thread's stack stops growing and the process is sent SIGSEGV"),
    new("RLIMIT_CORE", ResourceLimitKind.CoreFileSize, 4, ResourceLimitUnit.Bytes,
      "a core dump larger than this is not written; nought means none is written at all"),
    new("RLIMIT_RSS", ResourceLimitKind.ResidentSet, 5, ResourceLimitUnit.Bytes,
      "Linux has ignored this since 2.6; it is shown because the kernel still reports it, not because it does anything"),
    new("RLIMIT_NPROC", ResourceLimitKind.Processes, 6, ResourceLimitUnit.Count,
      "fork fails with EAGAIN once this user has that many processes — the limit is the user's, not this process's"),
    new("RLIMIT_NOFILE", ResourceLimitKind.OpenFiles, 7, ResourceLimitUnit.Count,
      "open, socket, pipe and accept fail with EMFILE past it; this is the ceiling a descriptor count is measured against"),
    new("RLIMIT_MEMLOCK", ResourceLimitKind.LockedMemory, 8, ResourceLimitUnit.Bytes,
      "mlock and unprivileged BPF fail with ENOMEM past it"),
    new("RLIMIT_AS", ResourceLimitKind.AddressSpace, 9, ResourceLimitUnit.Bytes,
      "every mapping counts, shared and file-backed alike, so this bites long before the process has used that much memory"),
    new("RLIMIT_LOCKS", ResourceLimitKind.FileLocks, 10, ResourceLimitUnit.Count,
      "unused since Linux 2.4.25; the kernel still reports it"),
    new("RLIMIT_SIGPENDING", ResourceLimitKind.PendingSignals, 11, ResourceLimitUnit.Count,
      "how many signals may be queued for this user at once; sigqueue fails with EAGAIN past it"),
    new("RLIMIT_MSGQUEUE", ResourceLimitKind.MessageQueueBytes, 12, ResourceLimitUnit.Bytes,
      "how many bytes this user may hold in POSIX message queues"),
    new("RLIMIT_NICE", ResourceLimitKind.NiceCeiling, 13, ResourceLimitUnit.Priority,
      "how far nice may be lowered without privilege, written as 20 minus the nice value — a limit of 0 permits nothing below nice 20"),
    new("RLIMIT_RTPRIO", ResourceLimitKind.RealTimePriority, 14, ResourceLimitUnit.Priority,
      "the highest real-time priority reachable without CAP_SYS_NICE; 0 means a real-time class cannot be entered at all"),
    new("RLIMIT_RTTIME", ResourceLimitKind.RealTimeTimeout, 15, ResourceLimitUnit.Microseconds,
      "how long a real-time task may hold a processor without blocking before it is sent SIGXCPU"),
  ];

  /// <summary>
  /// Whether this architecture numbers the limits the way <see cref="All"/> lists them.
  /// </summary>
  /// <remarks>
  /// An allowlist, like the signal numbers': a machine this has never run on should refuse rather
  /// than read one ceiling under another one's name.
  /// </remarks>
  public static bool NumbersAreKnownHere => RuntimeInformation.ProcessArchitecture is
    Architecture.X86 or Architecture.X64
    or Architecture.Arm or Architecture.Armv6 or Architecture.Arm64
    or Architecture.RiscV64 or Architecture.LoongArch64;

  /// <summary>Every limit that can be read or set here, or nothing where the numbering is unknown.</summary>
  public static IReadOnlyList<Definition> All => NumbersAreKnownHere ? _Generic : [];

  /// <summary>Why there are none to offer, for a front-end that has to say something.</summary>
  public const string UnknownArchitecture
    = "the RLIMIT_ numbers for this architecture are not known, so no limit can be read or set safely";

  public static Definition? Of(ResourceLimitKind kind) {
    foreach (var definition in All)
      if (definition.Kind == kind)
        return definition;

    return null;
  }

  /// <summary>What to call one where there is no catalogue entry to hand.</summary>
  public static string Name(ResourceLimitKind kind) => Of(kind)?.Name ?? kind.ToString();

  /// <summary>The names a command line may use, for a help text that cannot drift from the parser.</summary>
  public const string Vocabulary
    = "cpu, fsize, data, stack, core, rss, nproc, nofile, memlock, as, locks, sigpending, msgqueue, nice, rtprio, rttime";

  /// <summary>
  /// Reads <c>nofile</c>, <c>NOFILE</c> or <c>RLIMIT_NOFILE</c>.
  /// </summary>
  /// <remarks>
  /// The short spelling is what <c>prlimit</c> takes and what somebody who has just read one will
  /// type; the long one is what the manual page calls it. Both, rather than a choice between them.
  /// </remarks>
  public static bool TryParse(string? text, out ResourceLimitKind kind) {
    kind = default;
    if (string.IsNullOrWhiteSpace(text))
      return false;

    var wanted = text.Trim();
    if (wanted.StartsWith("RLIMIT_", StringComparison.OrdinalIgnoreCase))
      wanted = wanted["RLIMIT_".Length..];

    foreach (var definition in All)
      if (string.Equals(definition.Name["RLIMIT_".Length..], wanted, StringComparison.OrdinalIgnoreCase)) {
        kind = definition.Kind;
        return true;
      }

    return false;
  }

  /// <summary>
  /// The byte suffixes a value may carry, longest first so that <c>KiB</c> is matched before
  /// <c>K</c> would swallow its first letter.
  /// </summary>
  private static readonly (string Suffix, ulong Scale)[] _Suffixes = [
    ("KiB", 1ul << 10), ("MiB", 1ul << 20), ("GiB", 1ul << 30), ("TiB", 1ul << 40),
    ("K", 1ul << 10), ("M", 1ul << 20), ("G", 1ul << 30), ("T", 1ul << 40),
  ];

  /// <summary>
  /// Reads a limit value: a number, a number with a byte suffix, or the word <c>unlimited</c>.
  /// </summary>
  /// <param name="value">
  /// Null for unlimited, which is what the kernel spells <c>RLIM_INFINITY</c>. Unlimited is not a
  /// quantity and is not stored as one (PRD §38).
  /// </param>
  /// <remarks>
  /// The suffixes are the binary ones, because every limit that takes bytes is a page or a mapping
  /// count underneath and nobody sets a stack to a round million.
  /// </remarks>
  public static bool TryParseValue(string? text, out ulong? value) {
    value = null;
    if (string.IsNullOrWhiteSpace(text))
      return false;

    var trimmed = text.Trim();
    if (string.Equals(trimmed, "unlimited", StringComparison.OrdinalIgnoreCase)
      || string.Equals(trimmed, "infinity", StringComparison.OrdinalIgnoreCase)
      || string.Equals(trimmed, "max", StringComparison.OrdinalIgnoreCase))
      return true;

    var multiplier = 1ul;
    var digits = trimmed;
    foreach (var (suffix, scale) in _Suffixes) {
      if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        continue;

      multiplier = scale;
      digits = trimmed[..^suffix.Length].Trim();
      break;
    }

    if (!ulong.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
      return false;

    // A limit that overflowed the multiplication would be a very small one rather than a very large
    // one, which is the wrong way round for anything anybody meant.
    if (multiplier > 1 && number > ulong.MaxValue / multiplier)
      return false;

    value = number * multiplier;
    return true;
  }

  /// <summary>
  /// How a value reads: in its own unit, and unlimited as a word rather than as a number.
  /// </summary>
  public static string Format(ResourceLimitUnit unit, ulong? value) {
    if (value is not { } amount)
      return "unlimited";

    return unit switch {
      ResourceLimitUnit.Bytes => Humanize.Bytes(Counter.Of(amount)),
      ResourceLimitUnit.Seconds => $"{amount.ToString(CultureInfo.InvariantCulture)} s",
      ResourceLimitUnit.Microseconds => $"{amount.ToString(CultureInfo.InvariantCulture)} µs",
      _ => amount.ToString(CultureInfo.InvariantCulture),
    };
  }

  /// <summary>How one whole limit reads on a line: its soft value, then the ceiling on it.</summary>
  public static string Format(in ResourceLimit limit) {
    var unit = Of(limit.Kind)?.Unit ?? ResourceLimitUnit.Count;
    return $"{Format(unit, limit.Soft)} of {Format(unit, limit.Hard)}";
  }

}
