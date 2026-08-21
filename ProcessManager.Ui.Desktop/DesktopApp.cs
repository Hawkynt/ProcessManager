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
      // Opened at the start of the hold and kept, for the same reason the performance page is: a
      // properties window opened at capture time holds exactly one sample, and every one of its six
      // graphs is an empty grid — a picture that proves the layout and nothing else.
      ProcessPropertiesWindow? properties = null;

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
          description += "page sweep:\n" + performance.DescribeEveryPageForCapture();
          var pagePng = Path.Combine(directory, "performance.png");
          var pageSize = GtkCapture.Window(pagePng, out var pageFailure, performance.Text);
          description += pageSize is { } page
            ? $"page capture: {page.Width}x{page.Height} -> {pagePng}\n"
            : $"page capture: none — {pageFailure}\n";

          // And the same page with its fourth level open, on the resource that has the most of it.
          // The collapsed block, the compact density and the per-core grid are three states that a
          // photograph of the default page cannot show, and a state nobody photographs is one whose
          // layout regressions ship (PRD §45.2, §45.7).
          performance.Show("Processor");
          // Compact opens the fourth level with it: somebody who asked for density asked to see
          // more at once, not the same thing in less space.
          performance.SetDensity(compact: true);
          description += performance.DescribeForCapture();
          var expandedPng = Path.Combine(directory, "performance-expanded.png");
          var expandedSize = GtkCapture.Window(expandedPng, out var expandedFailure, performance.Text);
          description += expandedSize is { } expanded
            ? $"page expanded: {expanded.Width}x{expanded.Height} -> {expandedPng}\n"
            : $"page expanded: none — {expandedFailure}\n";

          // The rail's views. Counted and not quoted: the number is the empty-view detector, and the
          // rows are this machine's services, logins and open sockets, which do not belong in a log
          // that goes into a public repository (PRD §9).
          description += "shell views:\n" + window.DescribeShellForCapture();

          if (Environment.GetEnvironmentVariable("PROCMAN_SHOOT_VIEW") is { Length: > 0 } wanted && window.ShowView(wanted)) {
            // A local check only, and never part of the committed set: these views list this
            // machine's services, its logins and its open sockets, and the capture script's private
            // pid namespace does not hide any of that. The counts above are the published evidence;
            // this is for somebody looking at the layout on their own machine.
            var viewPng = Path.Combine(directory, "view.png");
            var viewSize = GtkCapture.Window(viewPng, out var viewFailure, window.Text);
            description += viewSize is { } view
              ? $"view capture: {view.Width}x{view.Height} -> {viewPng}\n"
              : $"view capture: none — {viewFailure}\n";

            window.ShowView("Processes");
          }

          // And one process in a window of its own, on both of the pages §26 grew: the sheet of
          // facts and the six graphs. The graphs are tiled by arithmetic, which is exactly the kind
          // of layout that photographs as an empty rectangle while every test around it passes.
          if (properties is not null) {
            properties.ShowPage("General");
            properties.ApplyLayout();
            description += $"properties:   {properties.TabTitles.Count} tabs, "
              + $"{properties.GeneralText.Split('\n').Length} facts on General\n";

            var generalPng = Path.Combine(directory, "properties.png");
            var generalSize = GtkCapture.Window(generalPng, out var generalFailure, properties.Text);
            description += generalSize is { } general
              ? $"properties general: {general.Width}x{general.Height} -> {generalPng}\n"
              : $"properties general: none — {generalFailure}\n";

            properties.ShowPage("Performance");
            properties.ApplyLayout();
            description += properties.PerformanceText;
            var graphsPng = Path.Combine(directory, "properties-performance.png");
            var graphsSize = GtkCapture.Window(graphsPng, out var graphsFailure, properties.Text);
            description += graphsSize is { } graphs
              ? $"properties graphs: {graphs.Width}x{graphs.Height} -> {graphsPng}\n"
              : $"properties graphs: none — {graphsFailure}\n";
          } else
            description += "properties:   no row was selected to open one for\n";

          // And the file box of §25.3, which is laid out by arithmetic rather than by anchoring and
          // is therefore exactly the kind that renders as an empty rectangle while every test around
          // it passes. Shown rather than shown modally: a modal one would block this callback and
          // the loop would never be told to exit.
          if (window.OpenExecutableProperties() is { } file) {
            // How many facts it holds rather than which file it described. The count is the
            // empty-box detector — a layout that lost its label reports one line and a picture of a
            // grey rectangle — and the name of a program on the capturing machine belongs in neither
            // a log nor a repository.
            description += $"file box:     {file.Description.Split('\n').Length} lines\n";
            var filePng = Path.Combine(directory, "file.png");
            var fileSize = GtkCapture.Window(filePng, out var fileFailure, file.Text);
            description += fileSize is { } shown
              ? $"file capture: {shown.Width}x{shown.Height} -> {filePng}\n"
              : $"file capture: none — {fileFailure}\n";
          } else
            description += "file box:     the selected process has no readable executable\n";
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
        // A row has to be selected before there is a process to open a window for, and nobody is
        // here to click one.
        window.SelectFirstRow();
        properties = window.OpenProperties();
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
