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

  /// <summary>The one properties page the capture counts and does not photograph — see below.</summary>
  private const string _ServicesPage = "Services";

  /// <summary>
  /// Runs the window. Returns null on a clean exit, or a sentence explaining why it could not start —
  /// which the caller shows before falling back to the terminal.
  /// </summary>
  public static string? Run(
    Sampler sampler,
    ISystemProbe probe,
    IProcessActions? actions,
    IServiceControl? services = null,
    IStartupControl? startup = null,
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

      var window = new MainWindow(sampler, probe, actions, services, startup);

      // Before FlatMode, so an explicit --flat still wins over what the file remembered.
      window.SettingsFile = SettingsStore.Locate(settingsPath);
      window.ApplySettings(settings ?? new(), updated => SettingsStore.Save(updated, settingsPath));
      if (flat)
        window.FlatMode = true;

      window.Start();

      if (shootPath is not null)
        return Shoot(window, sampler, probe, shootPath, holdSeconds);

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
  private static string? Shoot(
    MainWindow window,
    Sampler sampler,
    ISystemProbe probe,
    string directory,
    double holdSeconds
  ) {
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

          // The same table with headings in it. Grouped by executable rather than by user, because
          // inside the capture's private pid namespace every process belongs to one account and a
          // picture with one heading in it proves nothing about how a list of them looks (PRD §83).
          var groupedPng = Path.Combine(directory, "desktop-grouped.png");
          // Put back at the end, because the window writes its settings once a second: a capture run
          // that left the list grouped would change the layout of whoever regenerated the pictures.
          var opened = window.Grouping;
          description += window.ShowGrouping(Query.ProcessGrouping.Executable);
          var groupedSize = GtkCapture.Window(groupedPng, out var groupedFailure, window.Text);
          description += groupedSize is { } grouped
            ? $"grouped capture: {grouped.Width}x{grouped.Height} -> {groupedPng}\n"
            : $"grouped capture: none — {groupedFailure}\n";

          window.ShowGrouping(opened);

          // The filter box, with something in it. Match highlighting is measured text drawn behind
          // other text, which is the shape of defect that passes every test and is one column out on
          // screen (PRD §11).
          var filteredPng = Path.Combine(directory, "desktop-filtered.png");
          description += window.ShowFilter("ba");
          var filteredSize = GtkCapture.Window(filteredPng, out var filteredFailure, window.Text);
          description += filteredSize is { } filtered
            ? $"filtered capture: {filtered.Width}x{filtered.Height} -> {filteredPng}\n"
            : $"filtered capture: none — {filteredFailure}\n";

          window.ShowFilter(string.Empty);

          // A table wider than the window, pinned and scrolled to its far end (PRD §11). The default
          // six columns fit, so nothing scrolls and nothing is held still — a pinned run that had
          // stopped working would photograph exactly like one that had not. Put back afterwards,
          // because the window writes its settings once a second.
          var openedColumns = window.ShownColumns;
          var pinnedPng = Path.Combine(directory, "desktop-pinned.png");
          description += window.ShowColumns(Settings.UserSettings.Presets["expert"], pinned: 2, scrollToTheEnd: true);
          var pinnedSize = GtkCapture.Window(pinnedPng, out var pinnedFailure, window.Text);
          description += pinnedSize is { } wide
            ? $"pinned capture: {wide.Width}x{wide.Height} -> {pinnedPng}\n"
            : $"pinned capture: none — {pinnedFailure}\n";

          window.ShowColumns(openedColumns.Fields, openedColumns.Pinned);
          description += window.ExerciseColumns();

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

          // One more page of the performance window, by name, for somebody checking a layout on
          // their own machine — the GPU page in particular, which stacks six graphs and which no
          // committed picture can show, because the machines that took them have no such card.
          // Local only and never part of the committed set, like the rail view below it.
          if (Environment.GetEnvironmentVariable("PROCMAN_SHOOT_RESOURCE") is { Length: > 0 } resource
              && performance.Show(resource)) {
            description += performance.DescribeForCapture();
            var resourcePng = Path.Combine(directory, "resource.png");
            var resourceSize = GtkCapture.Window(resourcePng, out var resourceFailure, performance.Text);
            description += resourceSize is { } shown
              ? $"resource capture: {shown.Width}x{shown.Height} -> {resourcePng}\n"
              : $"resource capture: none — {resourceFailure}\n";
          }

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

            // And the three pages §26 was missing. Every one of them is a list that can come up empty
            // for a reason rather than because the layout broke, so the log carries the count and the
            // sentence above it — which is what tells "the page says why" apart from "the page failed
            // to draw" without a person looking at the picture (PRD §9.6, §34, §36, §38).
            foreach (var (tab, image) in (ReadOnlySpan<(string Tab, string Image)>)[
              ("Memory map", "properties-memory-map.png"),
              ("Security", "properties-security.png"),
              ("cgroup", "properties-cgroup.png"),
              // The two lists §31 and §32 are about. Never photographed until now, which is how a
              // twenty-one-column module list and an eleven-column descriptor list stayed a layout
              // nobody had ever looked at: both scroll sideways, and a column whose width is wrong
              // takes the ones after it off the edge without failing a single test (PRD §9.6).
              ("Modules", "properties-modules.png"),
              ("Handles", "properties-handles.png"),
            ]) {
              properties.ShowPage(tab);
              properties.ApplyLayout();
              // What the page holds, before the picture of it. A list photographed as an empty
              // rectangle and a list of nought rows look identical in a PNG, and only one of them
              // is a layout fault (PRD §9.6).
              description += "  " + properties.Pane.DescribeForCapture();
              var pngPath = Path.Combine(directory, image);
              description += GtkCapture.Window(pngPath, out var pageFail, properties.Text) is { } shot
                ? $"properties {tab}: {shot.Width}x{shot.Height} -> {pngPath}\n"
                : $"properties {tab}: none — {pageFail}\n";
            }

            // After the loop, not before it: the map is filled when the page is asked for, so read
            // off beforehand this said "0 mappings" about a page that had never been opened.
            description += $"properties map: {properties.MemoryMapRows} mappings — {properties.MemoryMapHeading}\n";
            description += $"properties security: {properties.SecurityText.Split('\n').Length} rows\n";
            description += $"properties cgroup: {properties.CgroupText.Split('\n').Length} rows\n";
            // The Services page is counted and not photographed, and that is the same refusal the
            // rail's Services view gets. The capture's private pid namespace hides processes; it does
            // not hide unit files or the cgroup tree, so what lands on this page is a fact about
            // whoever took the picture rather than about the program. The count is the empty-page
            // detector, which is what the picture was for; the layout it would have proved is the
            // same two-column list the three pages above already prove (PRD §9, §97).
            properties.ShowPage(_ServicesPage);
            description += $"properties services: {properties.ServicesText.Split('\n').Length} rows\n";
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

          // And the legend of §23 — the one window here whose entire content is colour, and the one
          // that has to be looked at to be checked. It lays itself out by arithmetic against a height
          // it computes from its own rows, so a category added to it either lands under the buttons
          // or leaves a band of nothing across the middle, and no assertion can see either. Safe to
          // commit: swatches and fixed sentences, with nothing of this machine on it.
          var legend = new LegendWindow(Query.UsageThresholds.Default);
          legend.Show();
          legend.ApplyLayout();
          description += $"legend:       {LegendWindow.Categories.Count} colours, "
            + $"{LegendWindow.Note.Split('\n').Length} lines of note, {legend.Height} tall\n";

          var legendPng = Path.Combine(directory, "legend.png");
          description += GtkCapture.Window(legendPng, out var legendFailure, legend.Text) is { } legendShot
            ? $"legend capture: {legendShot.Width}x{legendShot.Height} -> {legendPng}\n"
            : $"legend capture: none — {legendFailure}\n";

          legend.Close();

          // And the settings box, which has the same problem the legend has and one more: it lays
          // itself out against a height counted off its own rows, and it is the one window here
          // whose entire job is to show every setting at once. A control that has fallen under the
          // buttons is invisible to every assertion and obvious in a photograph (PRD §67, §9.6).
          // Handed the defaults rather than this machine's settings: the box shows the path of the
          // file it is editing, and that path names whoever took the picture.
          var settingsBox = new SettingsDialog(new(), new(Path.Combine("~", ".config", "procman", SettingsStore.FileName), SettingsPlacement.Profile));
          settingsBox.Show();
          settingsBox.ApplyLayout();
          description += $"settings box: {settingsBox.RowCount} settings, {settingsBox.Height} tall\n";

          var settingsPng = Path.Combine(directory, "settings.png");
          description += GtkCapture.Window(settingsPng, out var settingsFailure, settingsBox.Text) is { } settingsShot
            ? $"settings capture: {settingsShot.Width}x{settingsShot.Height} -> {settingsPng}\n"
            : $"settings capture: none — {settingsFailure}\n";

          settingsBox.Close();

          // And the thread tab of §29 with the stack viewer of §30 open on one of its rows. Neither
          // has ever been photographed: the detail pane opens on the overview, so a table with
          // twenty columns in it was a layout no picture had ever shown — and a column drawn under
          // the scrollbar loses its units without any test noticing (PRD §9.6).
          description += Threads(sampler, probe, directory);
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

  /// <summary>
  /// Photographs the thread tab and the stack viewer (PRD §29, §30, §9.6).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The process with the most threads, in a window of its own, rather than the selected row's:
  /// a thread table photographed on a single-threaded process proves the layout and nothing else,
  /// and whichever row happens to sort first is not reliably interesting. §26 exists precisely so
  /// that several of these windows can be open at once, so opening one costs nothing.
  /// </para>
  /// <para>
  /// It matters more than usual here. Half of §29's columns are only filled for a process the reader
  /// could have attached a debugger to, and inside the capture's own PID namespace the program itself
  /// is both the busiest and the only such process — so this is the one picture where the instruction
  /// pointer and the stack usage are real readings rather than the refusals they are for everything
  /// else on a desktop.
  /// </para>
  /// </remarks>
  [System.Runtime.Versioning.SupportedOSPlatform("linux")]
  private static string Threads(Sampler sampler, ISystemProbe probe, string directory) {
    var busiest = Model.ProcessKey.None;
    var name = string.Empty;
    var most = 0;
    foreach (var process in sampler.Current.Processes)
      if (process.ThreadCount > most) {
        most = process.ThreadCount;
        busiest = process.Key;
        name = process.Name ?? string.Empty;
      }

    if (busiest.IsNone)
      return "thread tab:   nothing on this machine reported a thread count\n";

    var properties = new ProcessPropertiesWindow(probe, busiest, name);
    properties.Show();
    if (!properties.ShowPage("Threads"))
      return "thread tab:   the detail pane has no thread tab\n";

    // Filled twice, an interval apart, because the CPU and switch columns are differences between
    // two readings: photographed once they are a row of ellipses, which proves the layout and
    // nothing else — the same reason the performance page is opened at the start of the hold.
    System.Threading.Thread.Sleep(400);
    properties.Pane.Refresh();

    var description = properties.DescribeForCapture();
    var png = System.IO.Path.Combine(directory, "threads.png");
    description += GtkCapture.Window(png, out var failure, properties.Text) is { } size
      ? $"thread shot:  {size.Width}x{size.Height} -> {png}\n"
      : $"thread shot:  none — {failure}\n";

    var rows = properties.Pane.ThreadRows;
    if (rows.Count == 0)
      return description + "stack window: the process reported no threads to open one on\n";

    // The first thread, which is the one whose start address §29 can answer for.
    var stack = properties.Pane.OpenStack(rows[0].Tid, resolveSymbols: true);
    description += stack.DescribeForCapture();
    var stackPng = System.IO.Path.Combine(directory, "stack.png");
    description += GtkCapture.Window(stackPng, out var stackFailure, stack.Text) is { } stackSize
      ? $"stack shot:   {stackSize.Width}x{stackSize.Height} -> {stackPng}\n"
      : $"stack shot:   none — {stackFailure}\n";

    return description;
  }

}
