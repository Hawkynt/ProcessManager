using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// The one place the CLI touches the desktop assembly.
/// </summary>
/// <remarks>
/// A single call site, so a build that never opens a window links nothing of the UI toolkit beyond
/// this method — which is what lets the terminal front-end stay small on a headless machine.
/// </remarks>
internal static class DesktopLauncher {

  /// <summary>Null on a clean exit; otherwise the reason, for the caller to show before falling back.</summary>
  public static string? TryRun(
    Sampler sampler,
    ISystemProbe probe,
    IProcessActions? actions,
    IServiceControl? services,
    IStartupControl? startup,
    ISessionControl? sessions,
    string? shootPath,
    double holdSeconds = 0,
    bool flat = false,
    UserSettings? settings = null,
    string? settingsPath = null
  ) => Ui.Desktop.DesktopApp.Run(sampler, probe, actions, services, startup, sessions, shootPath, holdSeconds, flat, settings, settingsPath);

}
