using Hawkynt.ProcessManager.Abstractions;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// Picks the probe for the running platform.
/// </summary>
/// <remarks>
/// A plain <c>if</c> on <see cref="OperatingSystem"/>, not a registry or a scan. The trimmer can see
/// that a Linux build never reaches the Windows branch and drops that assembly entirely, which is
/// what keeps a single-platform publish small (PRD §2).
/// </remarks>
internal static class ProbeFactory {

  /// <summary>
  /// The channel to the privileged helper, shared by the probe and the actions.
  /// </summary>
  /// <remarks>
  /// Constructed eagerly and <em>started</em> lazily: making the object costs nothing and prompts
  /// nobody, and the first request that needs root is what raises the polkit dialog. A program that
  /// asked for a password on start-up would be a program people stop opening (PRD §8.1).
  /// </remarks>
  public static ElevatedChannel? Elevated { get; private set; }

  public static ISystemProbe? Create(string? probeRoot, bool useHelper = true) {
    if (OperatingSystem.IsLinux()) {
      // A recorded tree is somebody else's machine; asking a helper about pids in it would be asking
      // about whatever happens to hold those pids here.
      if (useHelper && probeRoot is null)
        Elevated = new(FindHelper());

      return new Platform.Linux.LinuxProbe(
        probeRoot is null
          ? new() { Elevated = Elevated }
          // A recorded tree was captured by somebody else, so the live user's id would refuse every
          // file in it. Root reads everything, which is what a replay wants (PRD §9.1).
          : new() { ProcRoot = probeRoot, PasswdPath = Path.Combine(probeRoot, "passwd"), EffectiveUserId = 0 }
      );
    }

    if (OperatingSystem.IsWindows())
      return new Platform.Windows.WindowsProbe();

    if (OperatingSystem.IsMacOS())
      return new Platform.MacOS.MacOsProbe();

    return null;
  }

  public static IProcessActions? CreateActions(string? probeRoot) {
    if (OperatingSystem.IsLinux())
      return new Platform.Linux.LinuxProcessActions(
        probeRoot is null ? new() { Elevated = Elevated } : new() { ProcRoot = probeRoot }
      );

    if (OperatingSystem.IsWindows())
      return new Platform.Windows.WindowsProcessActions();

    return null;
  }

  /// <summary>
  /// Where the helper is. The installed path first, because that is the one the polkit policy names
  /// and therefore the only one that can actually be elevated; the build layout second, so that
  /// `--helper-check` works from a source tree.
  /// </summary>
  private static string FindHelper() {
    foreach (var candidate in (ReadOnlySpan<string>)[
      "/usr/lib/procman/procman-helper",
      "/usr/local/lib/procman/procman-helper",
      Path.Combine(AppContext.BaseDirectory, "procman-helper"),
    ])
      if (File.Exists(candidate))
        return candidate;

    return "/usr/lib/procman/procman-helper";
  }

}
