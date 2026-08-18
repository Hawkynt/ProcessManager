namespace Hawkynt.ProcessManager.Settings;

/// <summary>
/// Where the settings file lives and how it is read and written (PRD §67).
/// </summary>
/// <remarks>
/// Every operation here is best-effort. A settings file that cannot be read, cannot be written or
/// does not exist must not stop the program starting: somebody diagnosing a machine that is already
/// unhealthy is exactly the person who cannot afford a task manager that refuses to open because a
/// config file is on a full disk (PRD §81).
/// </remarks>
public static class SettingsStore {

  /// <summary>
  /// The file, following each platform's own convention rather than inventing one.
  /// </summary>
  /// <remarks>
  /// <c>XDG_CONFIG_HOME</c> or <c>~/.config</c> on Unix; <c>%APPDATA%</c> on Windows.
  /// </remarks>
  public static string Path {
    get {
      var directory = Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolderOption.DoNotVerify
      );

      if (string.IsNullOrEmpty(directory))
        directory = System.IO.Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
          ".config"
        );

      return System.IO.Path.Combine(directory, "procman", "settings.conf");
    }
  }

  /// <summary>Reads the settings, or the defaults when there is no file or it cannot be read.</summary>
  public static UserSettings Load(string? path = null) {
    path ??= Path;
    try {
      return File.Exists(path) ? UserSettings.Parse(File.ReadAllText(path)) : new();
    } catch (IOException) {
      return new();
    } catch (UnauthorizedAccessException) {
      return new();
    }
  }

  /// <summary>
  /// Writes the settings, returning whether it worked.
  /// </summary>
  /// <remarks>
  /// Written to a neighbouring temporary file and moved into place, so an interrupted write leaves
  /// the previous settings rather than a truncated file. The program has usually been asked to exit
  /// by the time this runs, which is the worst moment to be half-way through a file.
  /// </remarks>
  public static bool Save(UserSettings settings, string? path = null) {
    ArgumentNullException.ThrowIfNull(settings);
    path ??= Path;

    try {
      var directory = System.IO.Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

      var temporary = path + ".new";
      File.WriteAllText(temporary, settings.Write());
      File.Move(temporary, path, overwrite: true);
      return true;
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }
  }

}
