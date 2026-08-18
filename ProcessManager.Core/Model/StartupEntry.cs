namespace Hawkynt.ProcessManager.Model;

/// <summary>Where a startup entry was found, which decides who can change it.</summary>
public enum StartupScope : byte {

  /// <summary>Applies to this user only, and is theirs to edit.</summary>
  User,

  /// <summary>Applies to everyone; changing it needs privilege.</summary>
  System,

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
public readonly record struct StartupEntry(
  string Name,
  string Command,
  string Path,
  bool Enabled,
  string? DisabledReason,
  StartupScope Scope,
  string? OnlyShowIn
);
