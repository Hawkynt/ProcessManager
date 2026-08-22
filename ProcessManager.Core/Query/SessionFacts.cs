using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a login record says beyond the fields it literally holds (PRD §43).
/// </summary>
/// <remarks>
/// <para>
/// utmp carries a user, a line, a host, a pid and a time, and that is all it has ever carried. Every
/// other column a table of logins wants — which session this is, whether anybody is at it, how long
/// they have been away — has to be worked out from those five and from the machine around them.
/// </para>
/// <para>
/// The working out is here rather than in the probe so that it is one rule the window, the terminal
/// and the command line all read, and so that it is tested on every CI leg: none of it touches a
/// file (PRD §5.1, §9.2).
/// </para>
/// </remarks>
public static class SessionFacts {

  /// <summary>
  /// How somebody is at the machine, from the login record's line and host.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The order matters and each arm has one piece of evidence behind it. A line that is a display
  /// rather than a terminal — <c>:0</c> — is a graphical login and the display manager wrote it. A
  /// host that is a display is a terminal window on this machine, which is what <c>who</c> shows in
  /// brackets. A host that is anything else is another machine. A line beginning <c>tty</c> with no
  /// host at all is somebody at the keyboard.
  /// </para>
  /// <para>
  /// Anything else is <see cref="SessionType.Unknown"/> rather than assigned to the nearest arm.
  /// Console is the strongest claim on the list — it says a person is physically present — and it is
  /// the last one that should be reached by falling through (PRD §5.3).
  /// </para>
  /// </remarks>
  public static SessionType Type(string? terminal, string? remoteHost) {
    if (terminal is { Length: > 0 } line && line[0] == ':')
      return SessionType.Graphical;

    if (remoteHost is { Length: > 0 } host)
      return host[0] == ':' ? SessionType.Terminal : SessionType.Remote;

    if (terminal is not { Length: > 0 } name)
      return SessionType.Unknown;

    if (name.StartsWith("pts/", StringComparison.Ordinal))
      return SessionType.Terminal;

    // "tty" covers the serial lines too: ttyS0 and ttyUSB0 begin with it, and somebody on a serial
    // console is at the machine in every sense this column means.
    return name.StartsWith("tty", StringComparison.Ordinal) ? SessionType.Console : SessionType.Unknown;
  }

  /// <summary>What a type is called, in a column.</summary>
  public static string Describe(SessionType type) => type switch {
    SessionType.Console => "console",
    SessionType.Graphical => "graphical",
    SessionType.Terminal => "terminal",
    SessionType.Remote => "remote",
    _ => "—",
  };

  /// <summary>What a state is called, in a column.</summary>
  public static string Describe(SessionState state) => state switch {
    SessionState.Alive => "alive",
    SessionState.Stale => "stale",
    _ => "—",
  };

  /// <summary>
  /// The session id buried in a cgroup path, or null where there is none.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A login record has no session id: <c>loginctl</c> identifies a session by one and utmp predates
  /// it by decades. What systemd does have is a cgroup per session, named <c>session-N.scope</c>,
  /// and the leader of the session lives in it — so the id is readable off the leader without asking
  /// logind anything and without parsing a file that says not to.
  /// </para>
  /// <para>
  /// The whole path is searched rather than only its innermost unit, because a leader that has since
  /// moved — into a service of its own, into a nested scope — is still inside the session's scope
  /// somewhere above it. Null where no segment is one, which is a login systemd did not open: a
  /// container's, an <c>agetty</c> that nobody has answered, a record left by something older.
  /// </para>
  /// </remarks>
  public static string? IdFromCgroup(string? cgroupPath) {
    if (cgroupPath is not { Length: > 0 })
      return null;

    foreach (var range in cgroupPath.AsSpan().Split('/')) {
      var segment = cgroupPath.AsSpan()[range];
      if (!segment.StartsWith("session-", StringComparison.Ordinal)
          || !segment.EndsWith(".scope", StringComparison.Ordinal))
        continue;

      var id = segment["session-".Length..^".scope".Length];

      // Digits only. systemd names a session by a number, and a scope somebody wrote by hand called
      // "session-backup.scope" is a unit rather than a login.
      if (id.IsEmpty)
        continue;

      var digits = true;
      foreach (var character in id)
        if (!char.IsAsciiDigit(character)) {
          digits = false;
          break;
        }

      if (digits)
        return new(id);
    }

    return null;
  }

  /// <summary>
  /// The account's own description, from the fifth field of a password file line.
  /// </summary>
  /// <remarks>
  /// The field is comma-separated and only the first part is a name — the rest is an office, a
  /// telephone number and whatever else <c>chfn</c> was given, none of which belongs in a column
  /// headed with somebody's name. An empty field is null rather than an empty string: most accounts
  /// on most machines have none, and a blank cell is the honest rendering of that.
  /// </remarks>
  public static string? FullNameFromGecos(string? gecos) {
    if (gecos is not { Length: > 0 })
      return null;

    var comma = gecos.IndexOf(',');
    var name = (comma < 0 ? gecos : gecos[..comma]).Trim();
    return name.Length == 0 ? null : name;
  }

  /// <summary>
  /// How long a session has been idle, from when its terminal was last written to.
  /// </summary>
  /// <remarks>
  /// The same measurement <c>who -u</c> and <c>w</c> make, and it has the same limits: it is the
  /// terminal's modification time, so a session doing something that produces no terminal output
  /// looks idle, and a session with no terminal at all — a graphical login — has nothing to measure.
  /// Null in that case, rather than nought, which would read as "active this second" about the one
  /// kind of session this cannot see (PRD §72.3).
  /// </remarks>
  public static TimeSpan? IdleFor(long lastInputUtcTicks, DateTime nowUtc) {
    if (lastInputUtcTicks <= 0)
      return null;

    var idle = nowUtc - new DateTime(lastInputUtcTicks, DateTimeKind.Utc);

    // A terminal written to a moment in the future is a clock that has been put back, not a session
    // that has been idle for a negative time.
    return idle < TimeSpan.Zero ? TimeSpan.Zero : idle;
  }

  /// <summary>
  /// An idle time in the shortest form that is still exact enough to act on.
  /// </summary>
  /// <remarks>
  /// Under a minute is "now" rather than a number of seconds, because the reading is only as fresh
  /// as the last thing the terminal printed and a figure in seconds claims a precision it has not
  /// got.
  /// </remarks>
  public static string DescribeIdle(TimeSpan? idle) {
    if (idle is not { } span)
      return "—";

    if (span.TotalMinutes < 1)
      return "now";

    if (span.TotalHours < 1)
      return $"{((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture)}m";

    return span.TotalDays < 1
      ? $"{((int)span.TotalHours).ToString(CultureInfo.InvariantCulture)}h{span.Minutes.ToString("00", CultureInfo.InvariantCulture)}"
      : $"{((int)span.TotalDays).ToString(CultureInfo.InvariantCulture)}d";
  }

}
