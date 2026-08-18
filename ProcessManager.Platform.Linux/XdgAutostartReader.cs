using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// What the XDG autostart specification says will start at login (PRD §42).
/// </summary>
/// <remarks>
/// <para>
/// Entries live in <c>~/.config/autostart</c> and in the system directories. The rule that makes
/// this more than a directory listing is the override: a user file with the same <em>file name</em>
/// as a system one replaces it entirely, which is how a desktop offers a switch that turns a
/// system-wide entry off for one person. Listing both would report an entry twice and, worse, report
/// a disabled one as enabled.
/// </para>
/// <para>
/// Managed file APIs, like the host reader and unlike the sampling path: this runs when somebody
/// opens the page, not every second.
/// </para>
/// </remarks>
internal static class XdgAutostartReader {

  /// <summary>
  /// Reads every entry, user files overriding system ones of the same name.
  /// </summary>
  /// <param name="userDirectory">Usually <c>~/.config/autostart</c>.</param>
  /// <param name="systemDirectories">Usually <c>/etc/xdg/autostart</c>.</param>
  /// <param name="currentDesktop">
  /// The value of <c>XDG_CURRENT_DESKTOP</c>, which decides whether an entry limited to one desktop
  /// will run here. Empty means we do not know, in which case no entry is excluded for it: guessing
  /// that a KDE entry will not run is worse than admitting we cannot tell.
  /// </param>
  public static IReadOnlyList<StartupEntry> Read(
    string? userDirectory,
    IReadOnlyList<string> systemDirectories,
    string? currentDesktop
  ) {
    var byFileName = new Dictionary<string, StartupEntry>(StringComparer.Ordinal);

    // System first, then the user's, so an override lands on top of what it overrides.
    foreach (var directory in systemDirectories)
      foreach (var (name, entry) in ReadDirectory(directory, StartupScope.System, currentDesktop))
        byFileName[name] = entry;

    if (userDirectory is not null)
      foreach (var (name, entry) in ReadDirectory(userDirectory, StartupScope.User, currentDesktop))
        byFileName[name] = entry;

    var result = new List<StartupEntry>(byFileName.Values);
    result.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
    return result;
  }

  private static List<(string FileName, StartupEntry Entry)> ReadDirectory(
    string directory,
    StartupScope scope,
    string? currentDesktop
  ) {
    var entries = new List<(string, StartupEntry)>();
    if (!Directory.Exists(directory))
      return entries;

    IEnumerable<string> files;
    try {
      files = Directory.EnumerateFiles(directory, "*.desktop");
    } catch (IOException) {
      return entries;
    } catch (UnauthorizedAccessException) {
      return entries;
    }

    foreach (var file in files)
      if (Parse(file, scope, currentDesktop) is { } entry)
        entries.Add((Path.GetFileName(file), entry));

    return entries;
  }

  private static StartupEntry? Parse(string file, StartupScope scope, string? currentDesktop) {
    string[] lines;
    try {
      lines = File.ReadAllLines(file);
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }

    string? name = null, command = null, onlyShowIn = null, notShowIn = null, tryExec = null;
    var hidden = false;
    var autostartEnabled = true;
    var inDesktopEntry = false;

    foreach (var raw in lines) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] == '#')
        continue;

      if (line[0] == '[') {
        // A .desktop file can carry action groups after the main one; their keys are not ours.
        inDesktopEntry = line.Equals("[Desktop Entry]", StringComparison.Ordinal);
        continue;
      }

      if (!inDesktopEntry)
        continue;

      var separator = line.IndexOf('=', StringComparison.Ordinal);
      if (separator <= 0)
        continue;

      var key = line[..separator].Trim();
      var value = line[(separator + 1)..].Trim();
      switch (key) {
        // The plain key only: "Name[de]" is a translation, and picking one at random would show a
        // German name to an English reader.
        case "Name": name ??= value; break;
        case "Exec": command ??= value; break;
        case "TryExec": tryExec ??= value; break;
        case "Hidden": hidden = IsTrue(value); break;
        case "OnlyShowIn": onlyShowIn = value; break;
        case "NotShowIn": notShowIn = value; break;
        // How GNOME and several others spell "the user turned this off".
        case "X-GNOME-Autostart-enabled": autostartEnabled = IsTrue(value); break;
        default: break;
      }
    }

    if (command is null)
      return null;

    var (enabled, reason) = Decide(hidden, autostartEnabled, tryExec, onlyShowIn, notShowIn, currentDesktop);
    return new(
      name ?? Path.GetFileNameWithoutExtension(file),
      command,
      file,
      enabled,
      reason,
      scope,
      onlyShowIn
    );
  }

  /// <summary>
  /// Whether the entry will actually run, and why not when it will not.
  /// </summary>
  /// <remarks>
  /// Four separate ways to be off, checked in the order that makes the most useful explanation:
  /// an explicit Hidden beats everything, and "not for this desktop" is only worth saying when the
  /// entry is otherwise fine.
  /// </remarks>
  private static (bool Enabled, string? Reason) Decide(
    bool hidden,
    bool autostartEnabled,
    string? tryExec,
    string? onlyShowIn,
    string? notShowIn,
    string? currentDesktop
  ) {
    if (hidden)
      return (false, "hidden");

    if (!autostartEnabled)
      return (false, "turned off");

    // TryExec names a program that must exist; the entry is skipped silently when it does not.
    if (tryExec is { Length: > 0 } && !Exists(tryExec))
      return (false, $"{tryExec} is not installed");

    if (string.IsNullOrEmpty(currentDesktop))
      return (true, null);

    if (onlyShowIn is { Length: > 0 } && !Mentions(onlyShowIn, currentDesktop))
      return (false, $"only for {onlyShowIn.TrimEnd(';').Replace(";", ", ", StringComparison.Ordinal)}");

    if (notShowIn is { Length: > 0 } && Mentions(notShowIn, currentDesktop))
      return (false, $"not for {currentDesktop}");

    return (true, null);
  }

  /// <summary>
  /// XDG_CURRENT_DESKTOP is itself a colon-separated list, so either side may name several.
  /// </summary>
  private static bool Mentions(string list, string currentDesktop) {
    foreach (var candidate in currentDesktop.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      foreach (var member in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (string.Equals(member, candidate, StringComparison.OrdinalIgnoreCase))
          return true;

    return false;
  }

  private static bool Exists(string program) {
    if (program.Contains('/', StringComparison.Ordinal))
      return File.Exists(program);

    var path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(path))
      return false;

    foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
      if (File.Exists(Path.Combine(directory, program)))
        return true;

    return false;
  }

  private static bool IsTrue(string value)
    => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";

}
