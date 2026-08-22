using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// The other half of what starts at login: the user manager's units (PRD §42).
/// </summary>
/// <remarks>
/// <para>
/// A user unit that <c>default.target</c> wants is a login-time entry by any reasonable reading — it
/// is started when the session starts, by the manager the session starts with — and for as long as
/// this program looked only in the autostart directories it reported a machine as having nothing at
/// login while a dozen units came up with it. On a desktop that has moved its session to systemd,
/// which several now have, the autostart directories are close to empty and this is the whole answer.
/// </para>
/// <para>
/// Read from the same files as everything else here: the <c>default.target.wants</c> directories say
/// what is wanted, and the unit each symlink points at says what will run. Enablement is the symlink's
/// own existence, which is exactly what <c>systemctl --user enable</c> writes and the reason a
/// <c>Hidden=</c> key would mean nothing here.
/// </para>
/// </remarks>
internal static class SystemdUserStartupReader {

  private const string _WantsSuffix = ".wants";

  /// <summary>
  /// Reads every user unit that <c>default.target</c> pulls in at login.
  /// </summary>
  /// <param name="unitDirectories">
  /// Least specific first: the vendor's <c>/usr/lib/systemd/user</c>, the administrator's
  /// <c>/etc/systemd/user</c>, then the user's own <c>~/.config/systemd/user</c>. A unit file in a
  /// later directory replaces an earlier one of the same name entirely, and so does the user's, which
  /// is how a person overrides a packaged unit and how they mask one.
  /// </param>
  public static List<StartupEntry> Read(IReadOnlyList<string> unitDirectories) {
    ArgumentNullException.ThrowIfNull(unitDirectories);

    var files = new Dictionary<string, (string Path, StartupScope Scope)>(StringComparer.Ordinal);
    var wanted = new Dictionary<string, StartupScope>(StringComparer.Ordinal);

    for (var i = 0; i < unitDirectories.Count; ++i) {
      var directory = unitDirectories[i];
      // The last directory is the user's own. Which one that is comes from the caller rather than
      // from matching a path against a home directory, because a test's directories are neither.
      var scope = i == unitDirectories.Count - 1 ? StartupScope.User : StartupScope.System;

      foreach (var file in Enumerate(directory, "*"))
        files[Path.GetFileName(file)] = (file, scope);

      foreach (var link in Enumerate(Path.Combine(directory, "default.target" + _WantsSuffix), "*"))
        wanted[Path.GetFileName(link)] = scope;
    }

    var entries = new List<StartupEntry>(wanted.Count);
    foreach (var (name, scope) in wanted) {
      // Only the units that actually run something. default.target.wants holds paths and timers as
      // well, and a timer is a schedule rather than a thing that starts at login (PRD §5.3).
      if (!name.EndsWith(".service", StringComparison.Ordinal))
        continue;

      files.TryGetValue(name, out var found);
      entries.Add(Describe(name, found.Path, scope, unitDirectories));
    }

    entries.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
    return entries;
  }

  private static StartupEntry Describe(
    string name,
    string? path,
    StartupScope scope,
    IReadOnlyList<string> unitDirectories
  ) {
    if (path is not { Length: > 0 })
      // Wanted by the target and with no file anywhere: an enablement left behind by a package that
      // has since been removed. It will never run, and saying so is more use than leaving it out —
      // the symlink is still there and still somebody's to delete.
      return new(name, string.Empty, string.Empty, false, "no unit file of that name is installed", scope, null) {
        Mechanism = StartupMechanism.SystemdUserUnit,
      };

    if (IsMasked(path))
      return new(name, string.Empty, path, false, "masked — it can never be started while this stands", scope, null) {
        Mechanism = StartupMechanism.SystemdUserUnit,
      };

    var unit = ReadUnit(name, path, unitDirectories);
    var command = unit.First("Service", "ExecStart") ?? string.Empty;
    // The prefix characters are dropped rather than kept: they matter to a service row, where a
    // leading "-" is the difference between a unit that reports failures and one that does not, and
    // they say nothing about whether the thing starts at login (PRD §41, §42).
    var (_, executable, arguments) = command.Length > 0
      ? UnitFile.SplitCommand(command)
      : (string.Empty, string.Empty, string.Empty);

    return new(
      name,
      command,
      path,
      // The symlink is the enablement. There is no third state here the way there is for a system
      // service: a user unit that default.target does not want is simply not in this list at all.
      true,
      null,
      scope,
      null
    ) {
      Mechanism = StartupMechanism.SystemdUserUnit,
      Executable = executable.Length > 0 ? executable : null,
      Arguments = arguments.Length > 0 ? arguments : null,
      Description = unit.Last("Unit", "Description"),
    };
  }

  private static UnitFile ReadUnit(string name, string path, IReadOnlyList<string> unitDirectories) {
    var unit = UnitFile.Parse(ReadLines(path));
    foreach (var directory in unitDirectories) {
      var files = new List<string>(Enumerate(Path.Combine(directory, name + ".d"), "*.conf"));
      files.Sort(StringComparer.Ordinal);
      foreach (var file in files)
        unit.Merge(ReadLines(file));
    }

    return unit;
  }

  private static bool IsMasked(string path) {
    try {
      return new FileInfo(path).LinkTarget is { } target && target.EndsWith("/dev/null", StringComparison.Ordinal);
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }
  }

  private static IEnumerable<string> Enumerate(string directory, string pattern) {
    if (!Directory.Exists(directory))
      return [];

    try {
      return Directory.EnumerateFiles(directory, pattern);
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    }
  }

  private static string[] ReadLines(string path) {
    try {
      return File.Exists(path) ? File.ReadAllLines(path) : [];
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    }
  }

}
