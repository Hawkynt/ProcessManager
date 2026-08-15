using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Brings the window up on whichever backend this platform has.
/// </summary>
/// <remarks>
/// Backends are registered by an explicit <c>if</c> on the running OS rather than by a scan, so the
/// trimmer can drop the one this build will never reach — and a headless machine never loads GTK,
/// because a backend that is not registered is never asked for its native library (PRD §2).
/// </remarks>
public static class DesktopApp {

  /// <summary>
  /// Runs the window. Returns null on a clean exit, or a sentence explaining why it could not start —
  /// which the caller shows before falling back to the terminal.
  /// </summary>
  public static string? Run(Sampler sampler, ISystemProbe probe, IProcessActions? actions, string? shootPath = null) {
    try {
      if (OperatingSystem.IsWindows())
        BackendRegistry.Register(new NativeForms.Backends.Windows.Win32Backend());
      else if (OperatingSystem.IsLinux())
        BackendRegistry.Register(new NativeForms.Backends.Gtk.GtkBackend());
      else
        return $"there is no UI backend for {Environment.OSVersion.Platform}";

      var window = new MainWindow(sampler, probe, actions);
      window.Start();

      if (shootPath is not null)
        return Shoot(window, shootPath);

      Application.Run(window);
      return null;
    } catch (PlatformNotSupportedException e) {
      return e.Message;
    } catch (DllNotFoundException e) {
      // The usual one on a server: GTK is not installed. Naming the library is what turns this from
      // "it did not start" into something the reader can fix.
      return $"the UI toolkit's native library is missing ({e.Message})";
    }
  }

  /// <summary>
  /// The CI smoke path (PRD §9.6): bring the window up, prove it painted, write a log, and exit
  /// without an event loop nobody is there to end.
  /// </summary>
  private static string? Shoot(MainWindow window, string directory) {
    Directory.CreateDirectory(directory);
    var log = Path.Combine(directory, "shoot.log");
    try {
      // NativeForms has no DoEvents, and a smoke run must not enter a loop nobody is there to end.
      // A timer scheduled before Run therefore closes the window from inside the loop, two sample
      // intervals in — long enough that what is photographed has rates in it rather than a table of
      // ellipses.
      var closer = new NativeForms.Timer { Interval = 2500 };
      closer.Tick += (_, _) => {
        closer.Stop();
        File.WriteAllText(log, $"window up: {window.Text}, {window.Width}x{window.Height}\n");
        Application.Exit();
      };

      closer.Start();
      window.Start();
      Application.Run(window);
      return null;
    } catch (Exception e) {
      // A windowed process on a runner has no console attached, so the artifact has to be able to
      // explain an empty shot on its own.
      File.WriteAllText(log, e.ToString());
      return $"the window could not be brought up: {e.Message}";
    }
  }

}
