using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>
/// Turning a login-time entry on and off (PRD §42).
/// </summary>
/// <remarks>
/// Separate from the reading half for the same reason <see cref="IServiceControl"/> is: reading what
/// starts at login needs some files, and changing it writes to somebody's home directory. It is an
/// interface so that neither front-end has to name a platform to offer the switch — the entry was
/// listed in a view from the first version and could not be turned off from anywhere at all, which
/// §3.1 and §91 both count against this program.
/// </remarks>
public interface IStartupControl {

  /// <summary>Whether anything here can change a startup entry.</summary>
  bool IsAvailable { get; }

  /// <summary>
  /// Turns an entry on or off.
  /// </summary>
  /// <remarks>
  /// The whole entry is passed rather than a path, because what has to be written depends on whose
  /// entry it is: a user's own file is edited in place, and a system-wide one is turned off by
  /// writing a user file that replaces it. A caller holding only a path could not tell those apart.
  /// </remarks>
  ActionResult SetEnabled(in StartupEntry entry, bool enabled);

}
