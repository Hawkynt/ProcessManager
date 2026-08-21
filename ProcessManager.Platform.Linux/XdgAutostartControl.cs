using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Turning an XDG autostart entry on and off (PRD §42).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="XdgAutostartReader"/>, and it writes in exactly the two ways that
/// reader reads. A user's own file is edited where it is. A system-wide entry is <em>not</em> edited
/// — that file belongs to a package and the next update would overwrite whatever we did to it, and
/// on most machines it cannot be written at all. It is turned off by writing a file of the same name
/// into the user's own directory, which is the specification's override and the same mechanism every
/// desktop's own switch uses.
/// </para>
/// <para>
/// Turning a system entry back on removes that override rather than writing <c>Hidden=false</c> into
/// it. Leaving a copy behind would freeze the entry as it was on the day it was switched off: the
/// package's own file could gain a new command, a new name or a new condition, and none of it would
/// ever be seen again.
/// </para>
/// </remarks>
public sealed class XdgAutostartControl : IStartupControl {

  private readonly string? _userDirectory;

  /// <param name="userDirectory">
  /// Where the user's own entries live, usually <c>~/.config/autostart</c>. Null when there is no
  /// home directory to write to, which is a real state on a machine running this as a daemon.
  /// </param>
  public XdgAutostartControl(string? userDirectory = null)
    => this._userDirectory = userDirectory ?? DefaultUserDirectory();

  /// <inheritdoc />
  public bool IsAvailable => this._userDirectory is { Length: > 0 };

  /// <summary>
  /// <c>$XDG_CONFIG_HOME/autostart</c>, or the default the specification gives when it is unset.
  /// </summary>
  private static string? DefaultUserDirectory() {
    var configuration = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    if (configuration is { Length: > 0 })
      return Path.Combine(configuration, "autostart");

    var home = Environment.GetEnvironmentVariable("HOME");
    return home is { Length: > 0 } ? Path.Combine(home, ".config", "autostart") : null;
  }

  /// <inheritdoc />
  public ActionResult SetEnabled(in StartupEntry entry, bool enabled) {
    if (this._userDirectory is not { Length: > 0 } directory)
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        "there is no home directory here to keep an autostart entry in"
      );

    if (entry.Path is not { Length: > 0 } path)
      return ActionResult.Fail(ActionOutcome.Refused, "that entry has no file behind it");

    var overridePath = Path.Combine(directory, Path.GetFileName(path));
    var isOwn = entry.Scope == StartupScope.User;

    try {
      // A system entry being switched back on: the override is what was turning it off, so removing
      // it is the whole of the change, and the package's own file speaks again.
      if (enabled && !isOwn) {
        if (!File.Exists(overridePath))
          return ActionResult.Fail(
            ActionOutcome.Refused,
            $"'{entry.Name}' is not switched off here — whatever is stopping it is in the entry itself"
          );

        File.Delete(overridePath);
        return ActionResult.Ok;
      }

      var source = path;
      if (!File.Exists(source))
        return ActionResult.Fail(ActionOutcome.Refused, $"'{source}' is no longer there");

      var written = DesktopEntryEdit.Apply(File.ReadAllText(source), enabled);
      var target = isOwn ? path : overridePath;

      Directory.CreateDirectory(directory);
      // Written whole and moved into place, so a reader that arrives mid-write sees the old file or
      // the new one and never half of either. An autostart file truncated by a crash is an entry
      // that silently stops running.
      var temporary = target + ".procman-new";
      File.WriteAllText(temporary, written);
      File.Move(temporary, target, overwrite: true);
      return ActionResult.Ok;
    } catch (UnauthorizedAccessException problem) {
      return ActionResult.Fail(ActionOutcome.NotPermitted, problem.Message);
    } catch (IOException problem) {
      return ActionResult.Fail(ActionOutcome.Failed, problem.Message);
    }
  }

}
