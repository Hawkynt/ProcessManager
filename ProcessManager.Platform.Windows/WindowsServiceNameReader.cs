using System.Runtime.Versioning;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// The machine's own port-name file (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// Windows ships the same file in the same format Berkeley wrote in 1983, at
/// <c>%SystemRoot%\System32\drivers\etc\services</c>, which is why the parsing is shared with Linux
/// and only the path is not (see <see cref="ServiceNames"/>).
/// </para>
/// <para>
/// Read once and kept. The file changes when the machine is rebuilt, and re-reading it per frame
/// would be a disk touch for a fact that cannot have moved.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsServiceNameReader {

  private static ServiceNames? _cached;

  /// <summary>
  /// Where Windows keeps it.
  /// </summary>
  /// <remarks>
  /// Derived from the system directory rather than spelled out from the drive letter up: Windows is
  /// not always on <c>C:</c>, and <c>System32</c> is not always called that on a machine running a
  /// 32-bit process under WOW64 — <see cref="Environment.SpecialFolder.System"/> is the one answer
  /// that is right in both cases.
  /// </remarks>
  public static string DefaultPath {
    get {
      var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
      return system.Length > 0
        ? Path.Combine(system, "drivers", "etc", "services")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "services");
    }
  }

  /// <summary>
  /// The table, or nothing at all where the file is missing or unreadable.
  /// </summary>
  /// <remarks>
  /// Nothing at all rather than a table compiled in here. A machine that has had the file deleted,
  /// or one locked down by policy, names no ports — and the numbers underneath were always the fact
  /// the name was standing in for.
  /// </remarks>
  public static ServiceNames Read(string? path = null) {
    var wanted = path ?? DefaultPath;
    if (path is null && _cached is { } already)
      return already;

    var names = ServiceNames.Empty;
    try {
      if (File.Exists(wanted))
        names = ServiceNames.Parse(File.ReadAllText(wanted));
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    }

    if (path is null)
      _cached = names;

    return names;
  }

}
