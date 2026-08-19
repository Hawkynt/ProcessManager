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
/// One login session (PRD §43).
/// </summary>
/// <param name="Terminal">The tty or pseudo-terminal, e.g. <c>pts/0</c> or <c>tty1</c>.</param>
/// <param name="RemoteHost">
/// Where the login came from — a hostname, an address, or an X display like <c>:1</c>. Null for a
/// local console login, which is a different thing from an empty one.
/// </param>
/// <param name="Pid">The process that owns the session, usually a shell or a login process.</param>
public readonly record struct SessionRecord(
  string UserName,
  string Terminal,
  string? RemoteHost,
  int Pid,
  long LoginTimeUtcTicks,
  SessionKind Kind
);
