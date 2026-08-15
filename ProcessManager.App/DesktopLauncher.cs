using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Sampling;

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
  public static string? TryRun(Sampler sampler, ISystemProbe probe, IProcessActions? actions, string? shootPath)
    => Ui.Desktop.DesktopApp.Run(sampler, probe, actions, shootPath);

}
