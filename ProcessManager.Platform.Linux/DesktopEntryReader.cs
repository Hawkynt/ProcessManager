using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// The machine's desktop entries, read once (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// Where they are is the base directory specification's answer and not a guess: <c>applications</c>
/// under <c>XDG_DATA_HOME</c> — <c>~/.local/share</c> when that is unset — and then under each of
/// <c>XDG_DATA_DIRS</c>, which defaults to <c>/usr/local/share:/usr/share</c>. Earlier directories
/// shadow later ones, which is how a user's own copy of an entry replaces the packaged one. On the
/// machine this was written on the list also carries the two Flatpak export directories, which is
/// how a Flatpak's name arrives here without this code knowing what a Flatpak is.
/// </para>
/// <para>
/// Once, and lazily. There are around three hundred small files, none of which changes while a
/// process is running, so the whole cost falls on the first sample that asked for the column and on
/// nothing else (PRD §5.4).
/// </para>
/// </remarks>
internal sealed class DesktopEntryReader {

  private const string _APPLICATIONS = "applications";
  private const string _SUFFIX = ".desktop";

  private readonly string[] _directories;
  private DesktopApplications? _applications;

  public DesktopEntryReader(string[] directories) => this._directories = directories;

  /// <summary>The catalogue, built on the first question asked of it.</summary>
  public DesktopApplications Applications => this._applications ??= this.Build();

  /// <summary>
  /// The <c>applications</c> directories this machine has, in the order the specification searches
  /// them.
  /// </summary>
  /// <remarks>
  /// The defaults are the specification's own, so a machine that sets neither variable is searched
  /// the same way every other program on it searches. An entry that is not a directory is dropped
  /// here rather than at every use.
  /// </remarks>
  public static string[] DefaultDirectories() {
    var directories = new List<string>();

    var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    if (home is not { Length: > 0 })
      home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    Add(home);

    var shared = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
    if (shared is not { Length: > 0 })
      shared = "/usr/local/share:/usr/share";

    foreach (var directory in shared.Split(':', StringSplitOptions.RemoveEmptyEntries))
      Add(directory);

    return [.. directories];

    void Add(string dataDirectory) {
      var applications = Path.Combine(dataDirectory, _APPLICATIONS);
      if (Directory.Exists(applications))
        directories.Add(applications);
    }
  }

  private DesktopApplications Build() {
    var applications = new DesktopApplications();
    foreach (var directory in this._directories)
      Read(applications, directory);

    return applications;
  }

  private static void Read(DesktopApplications applications, string directory) {
    string[] files;
    try {
      // Subdirectories included, because the specification allows them and KDE uses them. The id a
      // file gets is its path below this directory with the separators turned into dashes, which is
      // what makes kde/foo.desktop and foo.desktop two entries rather than one.
      files = Directory.GetFiles(directory, "*" + _SUFFIX, SearchOption.AllDirectories);
    } catch (IOException) {
      return;
    } catch (UnauthorizedAccessException) {
      return;
    }

    foreach (var file in files) {
      byte[] content;
      try {
        content = File.ReadAllBytes(file);
      } catch (IOException) {
        continue;
      } catch (UnauthorizedAccessException) {
        continue;
      }

      applications.Add(IdOf(directory, file), DesktopEntry.Read(content));
    }
  }

  private static string IdOf(string directory, string file) {
    var relative = Path.GetRelativePath(directory, file);
    return relative.Replace(Path.DirectorySeparatorChar, '-');
  }

}
