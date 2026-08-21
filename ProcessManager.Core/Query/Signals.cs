using System.Globalization;
using System.Runtime.InteropServices;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Every signal that can be sent to a process, named once (PRD §25.1).
/// </summary>
/// <remarks>
/// <para>
/// One catalogue rather than a list in each front-end, for the reason
/// <see cref="SchedulingClasses"/> is one: the desktop's dialog and the command line read from here,
/// so a signal is spelled the same way and carries the same warning wherever it is offered.
/// </para>
/// <para>
/// <b>The numbers are not universal and are therefore not assumed.</b> Linux uses one layout on
/// x86, ARM, RISC-V and LoongArch and a different one on Alpha, SPARC, MIPS and PA-RISC, where
/// <c>SIGUSR1</c> is 30 rather than 10 and 10 is <c>SIGBUS</c>. An architecture whose layout is not
/// known here says so instead of sending the number anyway — the same rule the I/O priority
/// syscalls follow, for the same reason: a signal sent by the wrong number is not a failed action,
/// it is a successful one performed on the wrong thing (PRD §5.3).
/// </para>
/// </remarks>
public static class Signals {

  /// <summary>
  /// What the kernel does with a signal the program has <em>not</em> installed a handler for.
  /// </summary>
  /// <remarks>
  /// The only part of a signal a caller can be told in advance. What a program does with one it
  /// handles is the program's business and is unknowable from outside — which is why the dialogs
  /// say "if it does not handle it" rather than stating an outcome (PRD §72.3).
  /// </remarks>
  public enum Default : byte {

    /// <summary>The process ends.</summary>
    Terminates,

    /// <summary>The process ends and the kernel writes a core dump, where one is allowed.</summary>
    TerminatesWithCore,

    /// <summary>Nothing at all happens.</summary>
    Ignored,

    /// <summary>The process stops, and stays stopped until something continues it.</summary>
    Stops,

    /// <summary>A stopped process runs again.</summary>
    Continues,

  }

  /// <summary>
  /// One signal: what it is called, its number on this architecture, what it is for, and what
  /// happens to a program that does not handle it.
  /// </summary>
  /// <param name="Catchable">
  /// Whether the program is allowed to handle, block or ignore it at all. Only <c>SIGKILL</c> and
  /// <c>SIGSTOP</c> are not, and that is the whole reason they exist — it is also why they are the
  /// two that cannot be declined and must be confirmed accordingly (PRD §5.5).
  /// </param>
  public readonly record struct Signal(string Name, int Number, string Meaning, Default Default, bool Catchable = true) {

    /// <summary>Whether a program that ignores it is nonetheless ended by it.</summary>
    public bool IsFatalByDefault => this.Default is Default.Terminates or Default.TerminatesWithCore;

    public override string ToString() => $"{this.Name} ({this.Number.ToString(CultureInfo.InvariantCulture)})";

  }

  /// <summary>
  /// The standard signals, in the kernel's own numbering, on the architectures that share it.
  /// </summary>
  /// <remarks>
  /// The order is the number's, because that is the order <c>kill -l</c> prints and the order
  /// somebody checking an answer against it will read.
  /// </remarks>
  private static readonly Signal[] _Generic = [
    new("SIGHUP", 1, "the terminal or the session leader went away; many daemons re-read their configuration instead", Default.Terminates),
    new("SIGINT", 2, "what Ctrl-C sends", Default.Terminates),
    new("SIGQUIT", 3, "what Ctrl-\\ sends: quit, and leave a core behind", Default.TerminatesWithCore),
    new("SIGILL", 4, "an illegal instruction; normally the kernel's to send, not a person's", Default.TerminatesWithCore),
    new("SIGTRAP", 5, "a breakpoint; a debugger's signal", Default.TerminatesWithCore),
    new("SIGABRT", 6, "what abort() raises: give up now and leave a core behind", Default.TerminatesWithCore),
    new("SIGBUS", 7, "a bad memory access; normally the kernel's to send, not a person's", Default.TerminatesWithCore),
    new("SIGFPE", 8, "an arithmetic fault; normally the kernel's to send, not a person's", Default.TerminatesWithCore),
    new("SIGKILL", 9, "ends it immediately; it cannot be handled, blocked or refused", Default.Terminates, Catchable: false),
    new("SIGUSR1", 10, "whatever the program decided it means; it ends a program that decided nothing", Default.Terminates),
    new("SIGSEGV", 11, "an invalid memory reference; normally the kernel's to send, not a person's", Default.TerminatesWithCore),
    new("SIGUSR2", 12, "whatever the program decided it means; it ends a program that decided nothing", Default.Terminates),
    new("SIGPIPE", 13, "it wrote to a pipe with nobody reading", Default.Terminates),
    new("SIGALRM", 14, "an alarm() timer expired", Default.Terminates),
    new("SIGTERM", 15, "please stop; the polite request, and what a daemon's handler is written for", Default.Terminates),
    new("SIGSTKFLT", 16, "a coprocessor stack fault; unused by Linux itself", Default.Terminates),
    new("SIGCHLD", 17, "a child of it stopped or exited", Default.Ignored),
    new("SIGCONT", 18, "carry on, if it was stopped", Default.Continues),
    new("SIGSTOP", 19, "stop now; it cannot be handled, blocked or refused", Default.Stops, Catchable: false),
    new("SIGTSTP", 20, "what Ctrl-Z sends: stop, but the program may decline", Default.Stops),
    new("SIGTTIN", 21, "a background process tried to read from the terminal", Default.Stops),
    new("SIGTTOU", 22, "a background process tried to write to the terminal", Default.Stops),
    new("SIGURG", 23, "a socket has data out of band", Default.Ignored),
    new("SIGXCPU", 24, "it has used the CPU time RLIMIT_CPU allows it", Default.TerminatesWithCore),
    new("SIGXFSZ", 25, "it has written the file size RLIMIT_FSIZE allows it", Default.TerminatesWithCore),
    new("SIGVTALRM", 26, "a virtual timer expired", Default.Terminates),
    new("SIGPROF", 27, "a profiling timer expired", Default.Terminates),
    new("SIGWINCH", 28, "its terminal was resized", Default.Ignored),
    new("SIGIO", 29, "a descriptor is ready; also called SIGPOLL", Default.Terminates),
    new("SIGPWR", 30, "the machine is losing power", Default.Terminates),
    new("SIGSYS", 31, "a bad system call, or one a seccomp filter refused", Default.TerminatesWithCore),
  ];

  /// <summary>
  /// Whether this architecture's signal numbering is the one <see cref="All"/> lists.
  /// </summary>
  /// <remarks>
  /// A deliberate allowlist rather than an exclusion list. Alpha, SPARC, MIPS and PA-RISC each
  /// renumber part of the table, and a machine this program has never been run on should refuse to
  /// send a number rather than assume it means what it means here.
  /// </remarks>
  public static bool NumbersAreKnownHere => RuntimeInformation.ProcessArchitecture is
    Architecture.X86 or Architecture.X64
    or Architecture.Arm or Architecture.Armv6 or Architecture.Arm64
    or Architecture.RiscV64 or Architecture.LoongArch64;

  /// <summary>
  /// Every signal that can be offered by name, or nothing at all where the numbering is unknown.
  /// </summary>
  /// <remarks>
  /// Empty rather than a best guess. A front-end that offered <c>SIGUSR1</c> on a machine where the
  /// number means <c>SIGBUS</c> would be offering to crash a program under the label of poking it.
  /// </remarks>
  public static IReadOnlyList<Signal> All => NumbersAreKnownHere ? _Generic : [];

  /// <summary>Why there are no signals to offer, for a front-end that has to say something.</summary>
  public const string UnknownArchitecture
    = "the signal numbers for this architecture are not known, so none can be offered by name";

  /// <summary>
  /// The lowest and highest real-time signal numbers the kernel reserves.
  /// </summary>
  /// <remarks>
  /// The kernel's range, not the C library's. glibc keeps the first two or three for its own
  /// threading implementation and reports a <c>SIGRTMIN</c> of 34 rather than 32; musl keeps two and
  /// reports 35 for the same signal a glibc program calls <c>SIGRTMIN+1</c>. See
  /// <see cref="RealTimeAreNumberedOnly"/> for why that means they are not offered by name.
  /// </remarks>
  public static (int Min, int Max) RealTimeRange => (32, 64);

  /// <summary>
  /// Why the real-time signals are reachable by number and not by name (PRD §5.3).
  /// </summary>
  /// <remarks>
  /// There is no true number for <c>SIGRTMIN</c>. It is whatever the C library the <em>target</em>
  /// was linked against reserved for itself, and a sender cannot see that: computing "SIGRTMIN+3"
  /// from this program's own library and sending it to a process linked against another would
  /// deliver a different signal from the one its own header names. Offering the number, which is
  /// unambiguous, is the honest half of what can be offered.
  /// </remarks>
  public const string RealTimeAreNumberedOnly
    = "real-time signals are sent by number: SIGRTMIN is whatever C library the target was linked "
    + "against reserved for itself — 34 with glibc, 35 with musl — and a sender cannot see which.";

  /// <summary>The signal of that name, or null when there is none.</summary>
  public static Signal? ByName(string? name) {
    if (string.IsNullOrWhiteSpace(name))
      return null;

    var wanted = name.Trim();
    // Both spellings, because both are what people type and what every tool accepts: kill -TERM and
    // kill -SIGTERM name the same signal.
    if (!wanted.StartsWith("SIG", StringComparison.OrdinalIgnoreCase))
      wanted = "SIG" + wanted;

    foreach (var signal in All)
      if (string.Equals(signal.Name, wanted, StringComparison.OrdinalIgnoreCase))
        return signal;

    // The four historical spellings every kill(1) still accepts. They are the same numbers, not
    // separate signals, so they resolve rather than being listed twice.
    var alias = wanted.ToUpperInvariant() switch {
      "SIGIOT" => 6,
      "SIGCLD" => 17,
      "SIGPOLL" => 29,
      "SIGUNUSED" => 31,
      _ => 0,
    };

    return alias == 0 ? null : ByNumber(alias);
  }

  /// <summary>The signal of that number, or null when it is not one of the named ones.</summary>
  /// <remarks>
  /// A real-time number is a valid signal and is not in this catalogue, so a caller wanting to know
  /// whether a number is <em>sendable</em> asks <see cref="IsSendable"/> instead.
  /// </remarks>
  public static Signal? ByNumber(int number) {
    foreach (var signal in All)
      if (signal.Number == number)
        return signal;

    return null;
  }

  /// <summary>Whether the number names a signal at all, named or real-time.</summary>
  public static bool IsSendable(int number)
    => (number >= 1 && number <= 31) || (number >= RealTimeRange.Min && number <= RealTimeRange.Max);

  /// <summary>
  /// Reads <c>TERM</c>, <c>SIGTERM</c>, <c>sigterm</c> or <c>15</c>.
  /// </summary>
  /// <remarks>
  /// The number is accepted as well as the name because the real-time signals have no name to
  /// accept, and refusing a number would leave a third of the table unreachable from a command line.
  /// </remarks>
  public static bool TryParse(string? text, out int number) {
    number = 0;
    if (string.IsNullOrWhiteSpace(text))
      return false;

    var trimmed = text.Trim();
    if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) {
      number = parsed;
      return IsSendable(parsed);
    }

    if (ByName(trimmed) is not { } signal)
      return false;

    number = signal.Number;
    return true;
  }

  /// <summary>
  /// How a signal reads where there is only room for a word or two — including the numbers that have
  /// no name here.
  /// </summary>
  public static string Describe(int number) {
    if (ByNumber(number) is { } signal)
      return signal.ToString();

    var (min, max) = RealTimeRange;
    return number >= min && number <= max
      ? $"real-time signal {number.ToString(CultureInfo.InvariantCulture)}"
      : $"signal {number.ToString(CultureInfo.InvariantCulture)}";
  }

  /// <summary>
  /// What sending it will do to a program that does not handle it, in a sentence a confirmation can
  /// use (PRD §5.5, §90).
  /// </summary>
  /// <remarks>
  /// The sentence somebody actually needs, and the one that is least obvious: the default action of
  /// most signals is to end the process, so <c>SIGUSR1</c> sent to a program that never installed a
  /// handler for it kills the program. A dialog that said only "send SIGUSR1?" would be hiding that.
  /// </remarks>
  public static string Consequence(int number) {
    if (ByNumber(number) is not { } signal)
      return "A program that has installed no handler for a real-time signal is ended by it.";

    if (!signal.Catchable)
      return signal.Default == Default.Stops
        ? "It stops immediately and stays stopped until something continues it. It cannot decline, and a process stopped while holding a lock keeps holding it."
        : "It ends immediately without being asked to save anything, and unsaved work in it will be lost. It cannot decline.";

    return signal.Default switch {
      Default.Terminates => "If it has installed no handler for this signal, the default action ends it and unsaved work in it will be lost.",
      Default.TerminatesWithCore => "If it has installed no handler for this signal, the default action ends it and writes a core dump; unsaved work in it will be lost.",
      Default.Stops => "The default action stops it until something continues it, and a program that handles this signal may decline.",
      Default.Continues => "It runs again if it was stopped, and nothing happens if it was not.",
      _ => "The default action for this signal is to ignore it, so nothing happens unless the program handles it.",
    };
  }

}
