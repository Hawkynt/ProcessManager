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

  public static ISystemProbe? Create(string? probeRoot) {
    if (OperatingSystem.IsLinux())
      return new Platform.Linux.LinuxProbe(
        probeRoot is null
          ? new()
          // A recorded tree was captured by somebody else, so the live user's id would refuse every
          // file in it. Root reads everything, which is what a replay wants (PRD §9.1).
          : new() { ProcRoot = probeRoot, PasswdPath = Path.Combine(probeRoot, "passwd"), EffectiveUserId = 0 }
      );

    if (OperatingSystem.IsWindows())
      return new Platform.Windows.WindowsProbe();

    if (OperatingSystem.IsMacOS())
      return new Platform.MacOS.MacOsProbe();

    return null;
  }

  public static IProcessActions? CreateActions(string? probeRoot) {
    if (OperatingSystem.IsLinux())
      return new Platform.Linux.LinuxProcessActions(
        probeRoot is null ? new() : new() { ProcRoot = probeRoot }
      );

    if (OperatingSystem.IsWindows())
      return new Platform.Windows.WindowsProcessActions();

    return null;
  }

}
