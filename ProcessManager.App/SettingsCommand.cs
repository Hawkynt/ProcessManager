using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// The four things somebody can ask of the settings file without opening a front-end (PRD §67).
/// </summary>
/// <remarks>
/// <para>
/// A settings file that is meant to be edited by hand still needs a way to be moved by hand, and the
/// three verbs here are what "moved" turns out to mean in practice: take a copy of this machine's
/// settings to another one, put a copy back, and start again. The fourth is the one that gets asked
/// first — <em>which</em> file is this program reading — because a settings change that did not take
/// is nearly always a settings change made to the wrong file.
/// </para>
/// <para>
/// Every one of them says what it did and where. This is the one part of the settings system that is
/// allowed to be loud: the auto-saver is silent because it runs once a second behind somebody's back,
/// and these run because somebody asked.
/// </para>
/// </remarks>
internal static class SettingsCommand {

  /// <summary>Carries out the action, printing what happened. Returns a process exit code.</summary>
  public static int Run(SettingsAction action, string? transferPath, string? settingsPath) {
    var location = SettingsStore.Locate(settingsPath);

    switch (action) {
      case SettingsAction.Show:
        Console.WriteLine(location.Explain());
        Console.WriteLine(File.Exists(location.Path)
          ? "the file exists and is being read"
          : "no file there yet — the built-in defaults are in use, and it is written on the first change");

        return 0;

      case SettingsAction.Export:
        return Export(location, transferPath);

      case SettingsAction.Import:
        return Import(location, transferPath);

      case SettingsAction.Reset:
        return Reset(location);

      default:
        Console.Error.WriteLine("procman: nothing was asked of the settings file");
        return 1;
    }
  }

  /// <summary>
  /// Writes the settings out to another file.
  /// </summary>
  /// <remarks>
  /// The settings as this build understands them plus every line it does not, because that is what is
  /// in the file: an export that quietly dropped a newer build's keys would hand somebody a backup
  /// that loses exactly the settings they could not afford to lose.
  /// </remarks>
  private static int Export(SettingsLocation location, string? destination) {
    if (string.IsNullOrWhiteSpace(destination)) {
      Console.Error.WriteLine("procman: --export-settings needs a path to write to");
      return 1;
    }

    var settings = SettingsStore.Load(location.Path);
    if (!SettingsStore.Save(settings, destination)) {
      Console.Error.WriteLine($"procman: the settings could not be written to {destination}");
      return 1;
    }

    Console.WriteLine($"wrote {destination} from {location.Explain()}");
    return 0;
  }

  /// <summary>
  /// Replaces the settings with another file's.
  /// </summary>
  /// <remarks>
  /// Read and re-rendered rather than copied byte for byte, and deliberately: a file that arrived by
  /// email with half a line missing must land as the settings it can be understood as, with the bad
  /// line refused, rather than as something that will be silently mangled on the next write. The
  /// unknown keys survive that round trip, so importing a newer machine's file onto an older build
  /// still keeps what the older build cannot read.
  /// </remarks>
  private static int Import(SettingsLocation location, string? source) {
    if (string.IsNullOrWhiteSpace(source)) {
      Console.Error.WriteLine("procman: --import-settings needs a path to read from");
      return 1;
    }

    if (!File.Exists(source)) {
      Console.Error.WriteLine($"procman: there is no file at {source}");
      return 1;
    }

    var imported = SettingsStore.Load(source);
    if (!SettingsStore.Save(imported, location.Path)) {
      Console.Error.WriteLine($"procman: the settings could not be written to {location.Path}");
      return 1;
    }

    Console.WriteLine($"read {source} into {location.Explain()}");
    return 0;
  }

  /// <summary>
  /// Removes the settings file.
  /// </summary>
  /// <remarks>
  /// Named as what it is on the way out. Everything else in this program carries an unknown key
  /// through untouched; this is the one place they are thrown away, because "start again" is what was
  /// asked for — and the line printed here is what tells somebody to have exported first.
  /// </remarks>
  private static int Reset(SettingsLocation location) {
    if (!File.Exists(location.Path)) {
      Console.WriteLine($"there was no settings file at {location.Path}; nothing to remove");
      return 0;
    }

    if (!SettingsStore.Delete(location.Path)) {
      Console.Error.WriteLine($"procman: {location.Path} could not be removed");
      return 1;
    }

    Console.WriteLine($"removed {location.Path}; the next start uses the built-in defaults");
    return 0;
  }

}
