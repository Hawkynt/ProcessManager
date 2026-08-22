using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Turning a login-time entry on and off, and removing one (PRD §42).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="XdgAutostartReader"/> and <see cref="SystemdUserStartupReader"/>, and
/// it writes in exactly the ways those two read. Both mechanisms are behind one switch on purpose: a
/// front-end has a row under the pointer and no business knowing which of the two kinds it is, and the
/// moment it has to know is the moment one of them quietly stops being switchable.
/// </para>
/// <para>
/// A user's own desktop file is edited where it is. A system-wide one is <em>not</em> edited — that
/// file belongs to a package and the next update would overwrite whatever we did to it, and on most
/// machines it cannot be written at all. It is turned off by writing a file of the same name into the
/// user's own directory, which is the specification's override and the same mechanism every desktop's
/// own switch uses.
/// </para>
/// <para>
/// Turning a system entry back on removes that override rather than writing <c>Hidden=false</c> into
/// it. Leaving a copy behind would freeze the entry as it was on the day it was switched off: the
/// package's own file could gain a new command, a new name or a new condition, and none of it would
/// ever be seen again.
/// </para>
/// <para>
/// A user unit is neither of those. Its enablement is a symlink in a <c>.wants</c> directory, and the
/// thing that writes one correctly is the user's own manager — so that half is handed to
/// <see cref="IServiceControl"/> rather than reimplemented here. Without a manager to ask, those rows
/// refuse and say so instead of editing a file the manager would ignore.
/// </para>
/// </remarks>
public sealed class LinuxStartupControl : IStartupControl {

  private readonly string? _userDirectory;
  private readonly IServiceControl? _units;

  /// <param name="userDirectory">
  /// Where the user's own entries live, usually <c>~/.config/autostart</c>. Null when there is no
  /// home directory to write to, which is a real state on a machine running this as a daemon.
  /// </param>
  /// <param name="units">
  /// What to hand a systemd user unit to, or null where there is no manager here to ask.
  /// </param>
  public LinuxStartupControl(string? userDirectory = null, IServiceControl? units = null) {
    this._userDirectory = userDirectory ?? DefaultUserDirectory();
    this._units = units;
  }

  /// <inheritdoc />
  /// <remarks>
  /// True when either mechanism can be written. A machine with no home directory but a running user
  /// manager can still enable and disable units, and hiding the whole menu because of the other half
  /// would be hiding a capability the machine has (PRD §7).
  /// </remarks>
  public bool IsAvailable => this._userDirectory is { Length: > 0 } || this._units is { IsAvailable: true };

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
    if (entry.Mechanism == StartupMechanism.SystemdUserUnit)
      return this.Command(in entry, enabled ? ServiceCommand.Enable : ServiceCommand.Disable);

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

  /// <inheritdoc />
  /// <remarks>
  /// Deleting is refused for everything except the user's own desktop file, and each refusal names the
  /// thing to do instead. That is not caution for its own sake: a package's file comes back at the
  /// next update, so deleting it looks like it worked and does not, and removing a unit file leaves
  /// the <c>.wants</c> symlink pointing at nothing — which the manager complains about at every login
  /// afterwards.
  /// </remarks>
  public ActionResult Delete(in StartupEntry entry) {
    if (entry.Mechanism == StartupMechanism.SystemdUserUnit)
      return ActionResult.Fail(
        ActionOutcome.Refused,
        $"'{entry.Name}' is a systemd user unit, and a unit file is not this program's to delete. "
        + "Switching it off removes the enablement and leaves the unit where it is."
      );

    if (entry.Scope != StartupScope.User)
      return ActionResult.Fail(
        ActionOutcome.Refused,
        $"'{entry.Name}' belongs to a package and is the whole machine's. Deleting it would be undone "
        + "by the next update of that package; switching it off writes an override in your own "
        + "directory, which lasts."
      );

    if (entry.Path is not { Length: > 0 } path)
      return ActionResult.Fail(ActionOutcome.Refused, "that entry has no file behind it");

    try {
      if (!File.Exists(path))
        return ActionResult.Fail(ActionOutcome.Refused, $"'{path}' is no longer there");

      File.Delete(path);
      return ActionResult.Ok;
    } catch (UnauthorizedAccessException problem) {
      return ActionResult.Fail(ActionOutcome.NotPermitted, problem.Message);
    } catch (IOException problem) {
      return ActionResult.Fail(ActionOutcome.Failed, problem.Message);
    }
  }

  /// <summary>Hands a unit to the user's own manager, or says there is none to hand it to.</summary>
  private ActionResult Command(in StartupEntry entry, ServiceCommand command) {
    if (this._units is not { IsAvailable: true } units)
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        $"'{entry.Name}' is a systemd user unit, and there is no user manager here to ask. Its "
        + "enablement is a symlink the manager owns; writing one behind its back would be ignored "
        + "until the next login and wrong afterwards."
      );

    return units.Apply(command, entry.Name, userScope: true);
  }

}
