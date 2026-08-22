namespace Hawkynt.ProcessManager.Model;

/// <summary>Where a startup entry was found, which decides who can change it.</summary>
public enum StartupScope : byte {

  /// <summary>Applies to this user only, and is theirs to edit.</summary>
  User,

  /// <summary>Applies to everyone; changing it needs privilege.</summary>
  System,

}

/// <summary>
/// Which mechanism will start the entry, which is the thing that decides how to switch it off.
/// </summary>
/// <remarks>
/// Not cosmetic and not merged with the scope above. A desktop file and a user unit are both
/// "something that starts at login" and are turned off in completely different ways — one by a key
/// inside the file, the other by removing a symlink from a <c>.wants</c> directory — so a front-end
/// offering the wrong one would write a setting nothing reads (PRD §5.3).
/// </remarks>
public enum StartupMechanism : byte {

  /// <summary>A <c>.desktop</c> file in an autostart directory, per the XDG specification.</summary>
  XdgAutostart = 0,

  /// <summary>
  /// A systemd user unit that <c>default.target</c> wants, which is what starts it at login.
  /// </summary>
  SystemdUserUnit,

}

/// <summary>
/// Something configured to start when the user logs in (PRD §42).
/// </summary>
/// <param name="Name">The human-readable name, or the file's own if it has none.</param>
/// <param name="Command">What will be run.</param>
/// <param name="Path">The file that says so, which is what "reveal configuration" opens.</param>
/// <param name="Enabled">
/// Whether it will actually run. An entry can be present and disabled in several different ways, and
/// they all end up here.
/// </param>
/// <param name="DisabledReason">
/// Why it will not run, when it will not. Stated rather than left to be inferred from a false:
/// "hidden by a user override" and "not for this desktop" are different problems with different
/// fixes (PRD §7).
/// </param>
/// <param name="Scope">Whether it is this user's or the machine's.</param>
/// <param name="OnlyShowIn">
/// The desktop environments it is limited to, when it is limited. Empty means any.
/// </param>
/// <remarks>
/// The seven that identify an entry are constructor parameters; the rest are initialisers that start
/// at "not read", so a probe that knows less than the Linux one does not hand a front-end a blank to
/// render as an answer (PRD §72.3).
/// </remarks>
public readonly record struct StartupEntry(
  string Name,
  string Command,
  string Path,
  bool Enabled,
  string? DisabledReason,
  StartupScope Scope,
  string? OnlyShowIn
) {

  /// <summary>What will start it, and therefore what turning it off means.</summary>
  public StartupMechanism Mechanism { get; init; }

  /// <summary>
  /// The program <see cref="Command"/> runs, without its arguments.
  /// </summary>
  /// <remarks>
  /// Its own field rather than the first word of the command, because the first word of the command
  /// is not always the program: a quoted path may contain a space, and a <c>.desktop</c> file's
  /// <c>Exec</c> carries field codes like <c>%U</c> that are not arguments to anything. Split once,
  /// where the format is known, rather than by every reader of the row.
  /// </remarks>
  public string? Executable { get; init; }

  /// <summary>What is passed to <see cref="Executable"/>.</summary>
  public string? Arguments { get; init; }

  /// <summary>
  /// The entry's own account of what it is for, where its file carries one.
  /// </summary>
  /// <remarks>
  /// A <c>.desktop</c> file's <c>Comment</c> or a unit's <c>Description</c>. Not a publisher and not
  /// presented as one: it is whatever the author of the file wrote about it, which nobody has
  /// verified (PRD §70).
  /// </remarks>
  public string? Description { get; init; }

}
