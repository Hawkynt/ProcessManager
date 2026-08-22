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

  /// <summary>The name the file goes by wherever it is kept.</summary>
  public const string FileName = "settings.conf";

  /// <summary>
  /// The environment variable that names the file outright, for a run that must not touch the
  /// profile at all.
  /// </summary>
  /// <remarks>
  /// A variable and not only a flag, because the people who need this are running the program from a
  /// script or a service unit where adding an argument means editing somebody else's command line.
  /// </remarks>
  public const string PathVariable = "PROCMAN_SETTINGS";

  /// <summary>
  /// The marker beside the executable that keeps the settings beside the executable.
  /// </summary>
  /// <remarks>
  /// The convention every portable build on Windows already uses: an empty file next to the program
  /// says "keep my state here". It is a separate file from the settings themselves so that portable
  /// mode can be switched on before there are any settings to write.
  /// </remarks>
  public const string PortableMarker = "procman.portable";

  /// <summary>
  /// The file, following each platform's own convention rather than inventing one.
  /// </summary>
  /// <remarks>
  /// <c>XDG_CONFIG_HOME</c> or <c>~/.config</c> on Unix; <c>%APPDATA%</c> on Windows.
  /// </remarks>
  public static string Path => Locate().Path;

  /// <summary>
  /// Where the settings are kept, and which of the three rules put them there (PRD §67).
  /// </summary>
  /// <remarks>
  /// <para>
  /// In order: the variable, then a portable install, then the profile. The order is the order of how
  /// deliberate each one is — naming a file outright beats dropping a marker beside the binary, which
  /// beats the default — so a more specific answer is never overruled by a vaguer one.
  /// </para>
  /// <para>
  /// A portable install is one that carries its own state: either the marker is beside the executable
  /// or a settings file already is. The second half matters for the case nobody plans for — a folder
  /// copied onto a stick, marker and all, and then the marker deleted; the settings that are visibly
  /// sitting there must not stop being read.
  /// </para>
  /// </remarks>
  public static SettingsLocation Locate(string? explicitPath = null) {
    if (!string.IsNullOrWhiteSpace(explicitPath))
      return new(explicitPath, SettingsPlacement.Chosen);

    if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } named)
      return new(named, SettingsPlacement.Environment);

    if (BesideTheProgram() is { } portable)
      return new(portable, SettingsPlacement.Portable);

    var directory = Environment.GetFolderPath(
      Environment.SpecialFolder.ApplicationData,
      Environment.SpecialFolderOption.DoNotVerify
    );

    if (string.IsNullOrEmpty(directory))
      directory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        ".config"
      );

    return new(System.IO.Path.Combine(directory, "procman", FileName), SettingsPlacement.Profile);
  }

  /// <summary>
  /// The settings file beside the executable, when this is a portable install, and null otherwise.
  /// </summary>
  /// <remarks>
  /// Best-effort like everything else here: a program run from a directory it may not read is a
  /// program that falls back to the profile, not one that fails to start (PRD §81).
  /// </remarks>
  private static string? BesideTheProgram() {
    try {
      var program = Environment.ProcessPath;
      var directory = string.IsNullOrEmpty(program)
        ? AppContext.BaseDirectory
        : System.IO.Path.GetDirectoryName(program);

      if (string.IsNullOrEmpty(directory))
        return null;

      var settings = System.IO.Path.Combine(directory, FileName);
      return File.Exists(System.IO.Path.Combine(directory, PortableMarker)) || File.Exists(settings)
        ? settings
        : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
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

  /// <summary>The name the usage record goes by, beside the settings (PRD §44).</summary>
  /// <remarks>
  /// A separate file rather than a section of the settings, for two reasons that point the same way.
  /// It is data rather than preference and it grows, so a settings file somebody edits by hand would
  /// fill with rows they never wrote; and turning the feature off should be able to delete the record
  /// without touching anything else they have set.
  /// </remarks>
  public const string UsageFileName = "usage.tsv";

  /// <summary>
  /// Where the usage record lives: beside the settings, wherever those turned out to be.
  /// </summary>
  /// <param name="settingsPath">
  /// The settings file actually in use, which is not always the default one — `--settings`,
  /// `PROCMAN_SETTINGS` and a portable marker each move it. The record has to follow it: a portable
  /// install on a stick that wrote its record into the profile directory would leave behind exactly
  /// the file it exists to keep off the machine.
  /// </param>
  public static string UsagePathFor(string? settingsPath = null) {
    var directory = System.IO.Path.GetDirectoryName(settingsPath ?? Path);
    return string.IsNullOrEmpty(directory) ? UsageFileName : System.IO.Path.Combine(directory, UsageFileName);
  }

  /// <summary>Beside the settings file this run resolved to.</summary>
  public static string UsagePath => UsagePathFor();

  /// <summary>
  /// Reads the usage record, or an empty one where there is none to read.
  /// </summary>
  /// <remarks>
  /// A missing file is the ordinary state — the feature is off by default — and is not a failure.
  /// </remarks>
  public static Sampling.UsageHistory LoadUsage(string? path = null) {
    path ??= UsagePath;
    var history = new Sampling.UsageHistory();
    try {
      if (File.Exists(path))
        history.Restore(Sampling.UsageHistory.Parse(File.ReadAllText(path)));
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    }

    return history;
  }

  /// <summary>
  /// Writes it, the same way the settings are written: whole, then moved into place.
  /// </summary>
  public static bool SaveUsage(Sampling.UsageHistory history, string? path = null) {
    ArgumentNullException.ThrowIfNull(history);
    path ??= UsagePath;

    try {
      var directory = System.IO.Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

      var temporary = path + ".new";
      File.WriteAllText(temporary, history.Write());
      File.Move(temporary, path, overwrite: true);
      return true;
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }
  }

  /// <summary>
  /// Removes the usage record, which is what turning the feature off has to be able to do (PRD §44).
  /// </summary>
  /// <remarks>
  /// Deleting rather than emptying. Somebody switching this off is asking for the record to stop
  /// existing, and a file left behind holding a header and no rows still says the feature was on.
  /// </remarks>
  public static bool ForgetUsage(string? path = null) {
    path ??= UsagePath;
    try {
      if (File.Exists(path))
        File.Delete(path);

      return true;
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }
  }

  /// <summary>
  /// Removes the file, so the next start is a fresh install (PRD §67).
  /// </summary>
  /// <remarks>
  /// Deleting rather than writing the defaults out, and the difference is the unknown keys. Every
  /// other path through this class carries a key it does not understand through untouched, because an
  /// older build must not eat a newer one's settings; this is the one place somebody has actually
  /// asked for the file to stop existing, and a "restore defaults" that quietly kept lines they can
  /// no longer see would be a worse answer than the honest one. The dialog says so, and offers an
  /// export first.
  /// </remarks>
  /// <returns>Whether there is no longer a settings file — including when there never was one.</returns>
  public static bool Delete(string? path = null) {
    path ??= Path;
    try {
      File.Delete(path);
      return true;
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }
  }

}

/// <summary>Which rule decided where the settings file is (PRD §67).</summary>
public enum SettingsPlacement {

  /// <summary>The platform's own config location — where it goes when nobody said otherwise.</summary>
  Profile,

  /// <summary>Beside the executable, because this install carries its own state.</summary>
  Portable,

  /// <summary>Named by <see cref="SettingsStore.PathVariable"/>.</summary>
  Environment,

  /// <summary>Named on the command line by <c>--settings</c>.</summary>
  Chosen,

}

/// <summary>
/// A settings file and the reason it is where it is.
/// </summary>
/// <remarks>
/// The reason travels with the path because the question people actually ask is not "where is it"
/// but "why is it not reading mine" — and a program that shows only the path leaves them to work out
/// for themselves that a variable in their shell profile is the answer.
/// </remarks>
public readonly record struct SettingsLocation(string Path, SettingsPlacement Placement) {

  /// <summary>One line naming the file and what put it there.</summary>
  public string Explain() => this.Placement switch {
    SettingsPlacement.Chosen => $"{this.Path} (given with --settings)",
    SettingsPlacement.Environment => $"{this.Path} (named by {SettingsStore.PathVariable})",
    SettingsPlacement.Portable => $"{this.Path} (portable: kept beside the program)",
    _ => this.Path,
  };

}
