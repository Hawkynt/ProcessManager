using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

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
  public static string? Run(
    Sampler sampler,
    ISystemProbe probe,
    IProcessActions? actions,
    string? shootPath = null,
    double holdSeconds = 0,
    bool flat = false,
    UserSettings? settings = null,
    string? settingsPath = null
  ) {
    try {
      if (OperatingSystem.IsWindows())
        BackendRegistry.Register(new NativeForms.Backends.Windows.Win32Backend());
      else if (OperatingSystem.IsLinux())
        BackendRegistry.Register(new NativeForms.Backends.Gtk.GtkBackend());
      else
        return $"there is no UI backend for {Environment.OSVersion.Platform}";

      var window = new MainWindow(sampler, probe, actions);

      // Before FlatMode, so an explicit --flat still wins over what the file remembered.
      window.ApplySettings(settings ?? new(), updated => SettingsStore.Save(updated, settingsPath));
      if (flat)
        window.FlatMode = true;

      window.Start();

      if (shootPath is not null)
        return Shoot(window, shootPath, holdSeconds);

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
  private static string? Shoot(MainWindow window, string directory, double holdSeconds) {
    Directory.CreateDirectory(directory);
    var log = Path.Combine(directory, "shoot.log");
    try {
      // NativeForms has no DoEvents, and a smoke run must not enter a loop nobody is there to end.
      // A timer scheduled before Run therefore closes the window from inside the loop, two sample
      // intervals in — long enough that what is photographed has rates in it rather than a table of
      // ellipses.
      // Held open when asked, so something outside can photograph the window. The smoke run passes
      // zero and keeps its old behaviour: up, described, gone.
      var closer = new NativeForms.Timer { Interval = Math.Max(2500, (int)(holdSeconds * 1000)) };
      closer.Tick += (_, _) => {
        closer.Stop();

        // The picture, taken on the UI thread because that is the only thread allowed to ask a
        // widget to draw itself.
        window.SelectFirstRow();

        // What the content says its smallest size is, which a window manager turns into the floor
        // somebody dragging an edge cannot cross. It must not track the window's own size: when it
        // did, the window could be grown and never shrunk, and every growth ratcheted the floor up
        // again. Nothing else in the run would notice that coming back (PRD §45.1).
        var floor = OperatingSystem.IsLinux() ? GtkCapture.MinimumOf(window.Text) : null;
        var description = $"content floor:{(floor is { } f ? $" {f.Width}x{f.Height}" : " unknown")}\n"
          + window.DescribeForCapture();
        if (OperatingSystem.IsLinux()) {
          var png = Path.Combine(directory, "desktop.png");
          var size = GtkCapture.Window(png, out var failure, window.Text);
          description += size is { } taken
            ? $"capture:      {taken.Width}x{taken.Height} -> {png}\n"
            : $"capture:      none — {failure}\n";

          // And the performance page, which is where most of §45 lives and none of it was ever
          // photographed. It was opened at the start of the hold rather than here, so its graphs
          // have a history to draw: opened at capture time it has exactly one sample, and every
          // plot on it is an empty grid — a picture that proves the layout and nothing else.
          var performance = window.OpenPerformance();
          // Memory rather than whatever is busiest: it is the page with the composition bar on it,
          // and a capture that photographs a different page every run is not a regression detector.
          performance.Show("Memory");
          description += performance.DescribeForCapture();
          var pagePng = Path.Combine(directory, "performance.png");
          var pageSize = GtkCapture.Window(pagePng, out var pageFailure, performance.Text);
          description += pageSize is { } page
            ? $"page capture: {page.Width}x{page.Height} -> {pagePng}\n"
            : $"page capture: none — {pageFailure}\n";
        } else
          description += "capture:      not implemented on this platform\n";

        // Everything a reader needs to tell "the window came up empty" from "the window came up":
        // a windowed process on a runner has no console, so this file is the only witness
        // (PRD §9.6).
        File.WriteAllText(log, description);
        Application.Exit();
      };

      // The performance page is opened as soon as the loop is running rather than at capture time,
      // so its graphs have a history to draw — opened at capture time it has exactly one sample and
      // every plot on it is an empty grid, a picture that proves the layout and nothing else. It
      // cannot be opened before Application.Run: Form.Show needs a loop to show into.
      var opener = new NativeForms.Timer { Interval = 400 };
      opener.Tick += (_, _) => {
        opener.Stop();
        window.OpenPerformance();
      };

      opener.Start();
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
