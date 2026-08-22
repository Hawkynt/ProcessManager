namespace Hawkynt.ProcessManager.Model;

/// <summary>What kind of entry a session record is.</summary>
public enum SessionKind : byte {

  Unknown = 0,

  /// <summary>Somebody is logged in on this line.</summary>
  User,

  /// <summary>When the machine last booted.</summary>
  Boot,

  /// <summary>A login prompt with nobody at it yet.</summary>
  LoginProcess,

  /// <summary>A session that has ended. Kept in the file until its slot is reused.</summary>
  Dead,

}

/// <summary>
/// How somebody is at the machine (PRD §43).
/// </summary>
/// <remarks>
/// <para>
/// Named for the evidence rather than for what logind calls the same thing. logind's own types —
/// <c>tty</c>, <c>x11</c>, <c>wayland</c>, <c>web</c> — come from whatever asked it to open the
/// session, and are in a file whose first line says it is private data and not to be parsed. These
/// come from the login record's own two fields, which is a smaller claim honestly made: a login on
/// <c>tty1</c> is at the machine, a pseudo-terminal from <c>10.0.0.5</c> came over the network, and
/// a pseudo-terminal from <c>:1</c> is a terminal window on this machine's own display.
/// </para>
/// <para>
/// <see cref="Unknown"/> is nought so that a record nobody classified does not read as a console
/// login, which is the one that carries the strongest implication — that somebody is physically
/// there (PRD §72.3).
/// </para>
/// </remarks>
public enum SessionType : byte {

  Unknown = 0,

  /// <summary>A real terminal on the machine itself: <c>tty1</c>, a serial line.</summary>
  Console,

  /// <summary>A display rather than a terminal — the login record's line is <c>:0</c> or the like.</summary>
  Graphical,

  /// <summary>A pseudo-terminal opened on this machine: a terminal window, <c>screen</c>, <c>tmux</c>.</summary>
  Terminal,

  /// <summary>A pseudo-terminal whose other end is another machine.</summary>
  Remote,

}

/// <summary>
/// Whether a login record still describes something that is there (PRD §43).
/// </summary>
/// <remarks>
/// Not logind's <c>active</c>/<c>online</c>/<c>closing</c>, which are about which session owns the
/// seat. This is the cruder and more useful question for a table of logins: is the process that
/// opened it still running. A record whose leader has gone is a login that was never written out —
/// which is what the page means when it says a user with a session and no processes is a stale one.
/// </remarks>
public enum SessionState : byte {

  Unknown = 0,

  /// <summary>The process that opened the session is still there.</summary>
  Alive,

  /// <summary>It is not. The record outlived it.</summary>
  Stale,

}

/// <summary>
/// One login session (PRD §43).
/// </summary>
/// <param name="Terminal">The tty or pseudo-terminal, e.g. <c>pts/0</c> or <c>tty1</c>.</param>
/// <param name="RemoteHost">
/// Where the login came from — a hostname, an address, or an X display like <c>:1</c>. Null for a
/// local console login, which is a different thing from an empty one.
/// </param>
/// <param name="Pid">The process that owns the session, usually a shell or a login process.</param>
/// <param name="SessionId">
/// What <c>loginctl</c> calls this session, where it can be worked out. The login record carries no
/// such thing, so it comes from the leader's cgroup — a session's leader lives in
/// <c>session-N.scope</c>, and N is the id. Null where the leader is in no such scope, which is a
/// login systemd did not open.
/// </param>
/// <param name="FullName">
/// The account's own description, from the fifth field of the password file. Null where the file has
/// none or the account is not in it — a machine whose users come from a directory service.
/// </param>
/// <param name="LastInputUtcTicks">
/// When the session's terminal was last written to, which is what <c>who -u</c> and <c>w</c> measure
/// idleness by. Nought where there is no terminal to ask or its timestamp could not be read, and a
/// caller must not read that as "just now" (PRD §72.3).
/// </param>
public readonly record struct SessionRecord(
  string UserName,
  string Terminal,
  string? RemoteHost,
  int Pid,
  long LoginTimeUtcTicks,
  SessionKind Kind,
  string? SessionId = null,
  string? FullName = null,
  SessionType Type = SessionType.Unknown,
  SessionState State = SessionState.Unknown,
  long LastInputUtcTicks = 0
);
