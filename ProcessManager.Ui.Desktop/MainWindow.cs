using System.Drawing;
using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// The Process-Explorer-shaped window: system plots on top, the process tree below, a detail pane
/// under it, and a status bar that admits what the sample cost (PRD §7.1).
/// </summary>
public sealed class MainWindow : Form {

  private readonly Sampler _sampler;
  private readonly ISystemProbe _probe;
  private readonly IProcessActions? _actions;
  private readonly ProcessView _view = new() { TreeMode = true, SortColumn = ProcessField.CpuPercent, SortDescending = true };
  private readonly ProcessTreeBinder _binder;
  private readonly TreeListView _tree = new();
  private readonly HistoryPlot _cpuPlot = new();
  private readonly HistoryPlot _memoryPlot = new();
  private readonly CoreHeatmap _cores = new();
  private readonly DetailPane _details;
  private readonly SplitContainer _split = new();
  private readonly Panel _plots = new();
  private readonly Label _status = new();
  private readonly NativeForms.Timer _timer = new();
  private readonly HistoryRing<Rate> _cpuHistory = new(600);
  private readonly HistoryRing<Rate> _memoryHistory = new(600);
  private readonly ProcessHistory _rowHistory = new();
  private readonly List<ProcessField> _columns = [.. ColumnSet.Default];
  private bool _splitPlaced;
  private ITheme _theme = DefaultTheme.Instance;
  private int _laidOutWidth = -1;
  private UserSettings _settings = new();
  private SettingsAutoSaver? _autoSaver;

  public MainWindow(Sampler sampler, ISystemProbe probe, IProcessActions? actions) {
    ArgumentNullException.ThrowIfNull(sampler);
    ArgumentNullException.ThrowIfNull(probe);

    this._sampler = sampler;
    this._probe = probe;
    this._actions = actions;
    this._binder = new(this._tree);
    this._details = new(probe);

    this.Text = "Process Manager";
    this.Bounds = new(0, 0, 1240, 820);

    // Docked, in the order the layout is stacked: the menu on top, the plots under it, the status
    // line at the bottom, and the splitter taking everything that is left. Fixed bounds were the
    // first version and did not survive a resize.
    // Added outermost-first: the docking walk gives each control the edge of what is left, so the
    // menu has to come before the plot strip or the strip takes the top of the window.
    this.BuildStatus();
    this.BuildMenu();
    this.BuildPlots();
    this.BuildSplit();

    this._timer.Interval = 1000;
    this._timer.Tick += (_, _) => this.Refresh();
  }

  #region what survives a restart (PRD §11)

  /// <summary>
  /// Opens the window the way it was left, and keeps it that way.
  /// </summary>
  /// <remarks>
  /// Called before <see cref="Start"/>. Everything the file did not say is left at the default, so a
  /// fresh install and a settings file with one line in it both work.
  /// <para>
  /// The saver is handed a description of the window rather than a copy of it, so what gets written
  /// is what is on screen at the moment of the write — not what was on screen when something last
  /// changed.
  /// </para>
  /// </remarks>
  public void ApplySettings(UserSettings settings, Func<UserSettings, bool>? save = null) {
    ArgumentNullException.ThrowIfNull(settings);

    this._settings = settings;
    RowPalette.Apply(settings.Colours);

    this._view.SortColumn = settings.SortField;
    this._view.SortDescending = settings.SortDescending;
    this._view.TreeMode = settings.TreeMode;
    this._sampler.CpuPercentMode = settings.CpuMode;
    this.Interval = (int)Math.Round(settings.IntervalSeconds * 1000);

    if (settings.DesktopColumns.Length > 0) {
      this._columns.Clear();
      this._columns.AddRange(settings.DesktopColumns);
    }

    if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
      this.Bounds = new(this.Bounds.X, this.Bounds.Y, settings.WindowWidth, settings.WindowHeight);

    this.RebuildColumns();

    this._autoSaver = new(this.DescribeSettings, save);
    this._autoSaver.Prime(settings);
  }

  /// <summary>
  /// The settings as the window currently stands, over whatever the file already held.
  /// </summary>
  /// <remarks>
  /// Everything unrecognised is carried through untouched — including column sets somebody wrote by
  /// hand and keys a newer build wrote. A program that rewrites a settings file every second is the
  /// worst possible one to be careless about what it drops.
  /// </remarks>
  public UserSettings DescribeSettings() {
    var updated = this._settings with {
      IntervalSeconds = this.Interval / 1000d,
      SortField = this._view.SortColumn,
      SortDescending = this._view.SortDescending,
      TreeMode = this._view.TreeMode,
      CpuMode = this._sampler.CpuPercentMode,
      DesktopColumns = [.. this._columns],
      WindowWidth = this.Width,
      WindowHeight = this.Height,
    };

    return this._split.Height > 0
      ? updated with { SplitPercent = Math.Clamp(this._split.SplitterDistance * 100 / this._split.Height, 10, 90) }
      : updated;
  }

  #endregion


  /// <summary>
  /// What the window looks like right now, in text — the CI smoke leg's only evidence (PRD §9.6).
  /// </summary>
  public string DescribeForCapture() {
    var builder = new System.Text.StringBuilder();
    builder.AppendLine($"title:        {this.Text}");
    builder.AppendLine($"bounds:       {this.Bounds}");
    builder.AppendLine($"client size:  {this.ClientSize}");
    builder.AppendLine($"controls:     {this.Controls.Count}");
    builder.AppendLine($"process rows: {this._tree.Nodes.Count} roots, {this._tree.VisibleNodeCount} visible");
    builder.AppendLine($"columns:      {this._tree.Columns.Count}");
    builder.AppendLine($"split at:     {this._split.SplitterDistance}");
    builder.AppendLine($"plots:        cpu {this._cpuPlot.Bounds}, memory {this._memoryPlot.Bounds}, cores {this._cores.Bounds}");
    builder.AppendLine($"topology:     {this._cores.Topology.Cores.Count} logical, {this._cores.Topology.Packages.Count} socket(s), hybrid {this._cores.Topology.IsHybrid}");
    builder.AppendLine($"status:       {this._status.Text}");
    return builder.ToString();
  }

  /// <summary>Start with a flat sorted list rather than the tree. Set before <see cref="Start"/>.</summary>
  public bool FlatMode {
    get => !this._view.TreeMode;
    set => this._view.TreeMode = !value;
  }

  /// <summary>The refresh interval in milliseconds.</summary>
  public int Interval {
    get => this._timer.Interval;
    set => this._timer.Interval = Math.Clamp(value, 250, 60_000);
  }

  public void Start() {
    this._binder.CurrentUserId = CurrentUserId();
    // Read once, before the first paint: the machine will not rearrange its cores while we watch.
    this._cores.Topology = this._probe.DescribeTopology();
    this.Refresh();
    this._timer.Start();
  }

  /// <summary>
  /// Selects the first row, so a screenshot shows the detail pane doing its job rather than an empty
  /// box. A person opening the window picks a row within a second; a capture has nobody to do it.
  /// </summary>
  public void SelectFirstRow() {
    if (this._tree.Nodes.Count == 0)
      return;

    this._tree.SelectedNode = this._tree.Nodes[0];
    this.UpdateDetails();
  }

  /// <summary>
  /// The uid of whoever is running this, so "my processes" can be a colour. -1 on a platform where it
  /// cannot be read, which simply means no row is coloured as one's own.
  /// </summary>
  private static int CurrentUserId() {
    if (!OperatingSystem.IsLinux())
      return -1;

    try {
      foreach (var line in File.ReadLines("/proc/self/status")) {
        if (!line.StartsWith("Uid:", StringComparison.Ordinal))
          continue;

        var fields = line[4..].Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 0 && int.TryParse(fields[0].Trim(), out var uid))
          return uid;
      }
    } catch (IOException) {
      // Falls through to the sentinel, which colours nothing as one's own.
    }

    return -1;
  }

  #region layout

  private void BuildPlots() {
    this._plots.Dock = DockStyle.Top;
    this._plots.Height = 104;

    this._cpuPlot.Caption = "CPU";
    this._cpuPlot.AddSeries(this._cpuHistory, Color.FromArgb(0x2E, 0x8B, 0x57), "CPU");

    this._memoryPlot.Caption = "Memory";
    this._memoryPlot.AddSeries(this._memoryHistory, Color.FromArgb(0x46, 0x82, 0xB4), "Memory");

    // Clicking a total is how somebody asks for the detail behind it, so all three plots open the
    // performance view. The menu item stays, for people who would rather not find that by accident.
    //
    // MouseUp rather than Click: the toolkit raises Click only from PerformClick, so nothing a mouse
    // does ever reaches it. Wiring Click here compiled, read correctly, and did nothing at all.
    this._cpuPlot.MouseUp += (_, _) => this.ShowPerformance();
    this._memoryPlot.MouseUp += (_, _) => this.ShowPerformance();
    this._cores.MouseUp += (_, _) => this.ShowPerformance();

    this._plots.Controls.Add(this._cpuPlot);
    this._plots.Controls.Add(this._memoryPlot);
    this._plots.Controls.Add(this._cores);

    // Laid out by hand rather than with Anchor. Anchoring a child inside a docked container makes
    // the toolkit's layout feed the resolved width back into the parent, and the form then grows
    // without bound — measured at eleven million pixels wide after two seconds, with the anchored
    // control's width tracking it exactly.
    //
    // There is no resize event to hook either: Control.OnBoundsChanged is private protected, so a
    // control outside the toolkit's own assembly cannot observe its own resize. The layout therefore
    // runs on the sample tick, which means a resize is followed a fraction of a second later by the
    // strip catching up. Visible, and the alternative was a window that grows until it wraps.
    this.Controls.Add(this._plots);
    this.LayOutPlots();
  }

  private void LayOutPlots() {
    const int Gap = 6;
    var width = Math.Max(360, this._plots.Width);
    // Two fixed-width plots and a meter strip that takes what is left, with a floor so that a narrow
    // window clips the strip rather than inverting its width.
    var plotWidth = Math.Min(392, (width - Gap * 4) / 3);
    var height = this._plots.Height - Gap * 2;

    this._cpuPlot.Bounds = new(Gap, Gap, plotWidth, height);
    this._memoryPlot.Bounds = new(Gap * 2 + plotWidth, Gap, plotWidth, height);

    var coresLeft = Gap * 3 + plotWidth * 2;
    this._cores.Bounds = new(coresLeft, Gap, Math.Max(120, width - coresLeft - Gap), height);
  }

  private void BuildSplit() {
    this._split.Dock = DockStyle.Fill;
    this._split.Orientation = Orientation.Horizontal;
    this._split.Panel1MinSize = 120;
    this._split.Panel2MinSize = 120;

    // The distance is clamped against the container's current size, and a control that has not been
    // laid out yet has no size — so setting it here would silently collapse to Panel1MinSize, which
    // is exactly what it did. It is applied from the sample tick instead, once the splitter has a
    // height to divide.

    this.BuildTree();
    this._split.Panel1.Controls.Add(this._tree);
    this._split.Panel2.Controls.Add(this._details.Control);
    this.Controls.Add(this._split);
  }

  private void BuildTree() {
    this._tree.Dock = DockStyle.Fill;
    this._tree.ShowColumnHeaders = true;
    // Dense, the way the tools this imitates are: seventeen pixels fits about forty processes on a
    // laptop screen where the toolkit's default fits twenty-five, and forty is the difference
    // between scrolling to find a process and seeing it (PRD §93).
    this._tree.ItemHeight = 17;

    this.RebuildColumns();

    // What makes the list readable before a word of it is read: the row's colour says what kind of
    // process it is, and the legend window says what the colours mean (PRD §7.1).
    //
    // The theme comes from the cell-paint args rather than from the control: OwnerDrawnControl.Theme
    // is protected, so a caller cannot read the theme of a control it does not own. The row selector
    // runs just before the cells of the same row, so it is one frame behind on start-up — and the
    // first frame is drawn in the toolkit's default palette, which nobody sees at a 1 Hz refresh.
    this._tree.RowBackColorSelector = node =>
      node.Tag is ProcessRow row ? RowPalette.BackColorOf(row.Category, this._theme) : null;

    // The three history columns are drawn, not written.
    this._tree.CellPaint += this.OnCellPaint;
    this._tree.ColumnClick += this.OnColumnClick;
    this._tree.AfterSelect += (_, _) => this.UpdateDetails();
    // Double-click is how every tool of this kind opens a process, and the gesture people try first.
    this._tree.MouseDoubleClick += (_, _) => this.ShowProperties();
    this._tree.ContextMenuStrip = this.BuildContextMenu();
  }

  /// <summary>Recreates the header from the chosen column set.</summary>
  private void RebuildColumns() {
    this._tree.Columns.Clear();
    foreach (var column in this._columns) {
      var info = ColumnSet.Info(column);
      var header = info.Header;
      if (info.IsSortable && column == this._view.SortColumn)
        header = this._view.SortDescending ? header + " ▾" : header + " ▴";

      var which = column;
      this._tree.Columns.Add(new(header, info.DesktopWidth, node => ((ProcessRow)node.Tag!).TextOf(which)) {
        TextAlign = info.RightAligned ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
      });
    }

    this.StretchLastColumn();
  }

  private int _stretchedTo;

  /// <summary>
  /// Widens the last column so the columns fill the list.
  /// </summary>
  /// <remarks>
  /// Without it the area past the final column still takes each row's colour but has no cell in it,
  /// so a wide window ends in a band of stripes belonging to nothing — the one part of the list that
  /// looked unfinished beside the tools it copies (PRD §93).
  /// <para>
  /// Only the last column moves, and only when the width it should have changes: resizing a window
  /// must not silently rewrite the widths somebody chose for the columns before it.
  /// </para>
  /// </remarks>
  private void StretchLastColumn() {
    var count = this._tree.Columns.Count;
    if (count == 0)
      return;

    var available = this._tree.Width;
    if (available <= 0)
      return;

    var used = 0;
    for (var i = 0; i < count - 1; ++i)
      used += this._tree.Columns[i].Width;

    var last = this._columns.Count == count ? ColumnSet.Info(this._columns[count - 1]).DesktopWidth : 120;
    var wanted = Math.Max(last, available - used - 2);
    if (wanted == this._stretchedTo)
      return;

    this._stretchedTo = wanted;
    this._tree.Columns[count - 1].Width = wanted;
  }

  private void OnCellPaint(object? sender, TreeListViewCellPaintEventArgs e) {
    this._theme = e.Theme;
    if ((uint)e.ColumnIndex >= (uint)this._columns.Count)
      return;

    // Under everything else, and for every cell: this runs before the control draws its text, so a
    // rule laid down here is a rule the text sits on rather than one drawn over it.
    DrawGridLines(e);

    var info = ColumnSet.Info(this._columns[e.ColumnIndex]);
    if (info.Series is not { } series || e.Node.Tag is not ProcessRow row)
      return;

    var colour = series switch {
      HistorySeries.Cpu => RowPalette.Cpu,
      HistorySeries.Memory => RowPalette.Memory,
      _ => RowPalette.Io,
    };

    Sparkline.Draw(e.Graphics, e.Bounds, this._rowHistory.Get(row.Key, series), this._rowHistory.ScaleOf(series), colour);
    // Handled: the cell has no text, and letting the control draw an empty string over the plot
    // would only cost a measure.
    e.Handled = true;
  }

  /// <summary>
  /// The faint rules that make a dense table readable across a row (PRD §93).
  /// </summary>
  /// <remarks>
  /// Process Explorer and Process Hacker both draw them, and at seventeen pixels a row they are what
  /// stops the eye sliding onto the line below while it crosses fifteen columns. Derived from the
  /// row's own colour rather than fixed, so a rule over a green "new process" row is a darker green
  /// and not a grey stripe — the category colour has to survive the grid.
  /// </remarks>
  private static void DrawGridLines(TreeListViewCellPaintEventArgs e) {
    var ground = e.Selected
      ? e.Theme.SelectionBackground
      : e.Node.Tag is ProcessRow row
        ? RowPalette.BackColorOf(row.Category, e.Theme) ?? e.Theme.FieldBackground
        : e.Theme.FieldBackground;

    var line = Shade(ground);
    var bounds = e.Bounds;

    // Bottom of the cell, and the right edge: horizontal rules separate rows, and the vertical ones
    // keep a wide numeric column from reading as part of its neighbour.
    e.Graphics.FillRectangle(line, new(bounds.X, bounds.Bottom - 1, bounds.Width, 1));
    e.Graphics.FillRectangle(line, new(bounds.Right - 1, bounds.Y, 1, bounds.Height - 1));
  }

  /// <summary>
  /// A colour a little away from its ground, in whichever direction there is room for.
  /// </summary>
  /// <remarks>
  /// Darkening a light row and lightening a dark one keeps the rule visible in both themes without
  /// a second palette, and keeps it subordinate to the text in either.
  /// </remarks>
  private static Color Shade(Color ground) {
    var luminance = (0.299 * ground.R) + (0.587 * ground.G) + (0.114 * ground.B);
    var shift = luminance > 128 ? -34 : 34;
    return Color.FromArgb(
      ground.A,
      Math.Clamp(ground.R + shift, 0, 255),
      Math.Clamp(ground.G + shift, 0, 255),
      Math.Clamp(ground.B + shift, 0, 255)
    );
  }

  private void OnColumnClick(object? sender, ColumnClickEventArgs e) {
    if ((uint)e.Column >= (uint)this._columns.Count)
      return;

    var sortBy = this._columns[e.Column];
    if (!ColumnSet.Info(sortBy).IsSortable)
      // A history column has no text to sort by, and sorting by "the shape of a graph" is not a
      // thing. Clicking one does nothing rather than doing something arbitrary.
      return;

    // Clicking the current column reverses it, the way every list does.
    if (this._view.SortColumn == sortBy)
      this._view.SortDescending = !this._view.SortDescending;
    else {
      this._view.SortColumn = sortBy;
      this._view.SortDescending = sortBy.PrefersDescending();
    }

    this.RebuildColumns();
    this.Refresh();
  }

  private ContextMenuStrip BuildContextMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(Item("Properties", () => this.UpdateDetails()));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("End process", () => this.Act("end", key => this._actions!.Terminate(key))));
    menu.Items.Add(Item("Suspend", () => this.Act("suspend", key => this._actions!.Suspend(key))));
    menu.Items.Add(Item("Resume", () => this.Act("resume", key => this._actions!.Resume(key))));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("Read handle count", this.FillHandleCounts));
    menu.Items.Add(Item("Properties…", this.ShowProperties));
    menu.Items.Add(Item("Refresh details", () => this._details.Invalidate()));
    return menu;
  }

  private static ToolStripMenuItem Item(string text, Action action) {
    var item = new ToolStripMenuItem(text);
    item.Click += (_, _) => action();
    return item;
  }

  private void BuildStatus() {
    this._status.Dock = DockStyle.Bottom;
    this._status.Height = 22;
    this.Controls.Add(this._status);
  }

  private void BuildMenu() {
    // Docked *and* given a height. A MenuStrip has no intrinsic one — the toolkit's own demo assigns
    // its bounds by hand — so docking it Top without a height produces a menu that is present,
    // mapped and nought pixels tall, which photographs exactly like a menu that was never added.
    var menu = new MenuStrip { Dock = DockStyle.Top, Height = 26 };

    var view = new ToolStripMenuItem("View");
    view.DropDownItems.Add(Item("Tree", () => {
      this._view.TreeMode = !this._view.TreeMode;
      this.Refresh();
    }));

    view.DropDownItems.Add(Item("Show all users", () => {
      this._view.UserIdFilter = null;
      this.Refresh();
    }));

    view.DropDownItems.Add(Item("CPU % convention", () => {
      this._sampler.CpuPercentMode = this._sampler.CpuPercentMode == CpuPercentMode.Normalized
        ? CpuPercentMode.PerCore
        : CpuPercentMode.Normalized;

      this.Refresh();
    }));

    menu.Items.Add(view);

    // Sorting lives in a menu because the header cannot carry it: NativeForms' TreeListView has no
    // ColumnClick (its ListView does, and is flat). Click-to-sort is the gesture people expect, so
    // this is a stand-in for a hook that has to come from the toolkit, not a preference.
    var sort = new ToolStripMenuItem("Sort by");
    foreach (var column in (ReadOnlySpan<ProcessField>)[
      ProcessField.CpuPercent,
      ProcessField.PrivateBytes,
      ProcessField.WorkingSetBytes,
      ProcessField.ReadBytesPerSecond,
      ProcessField.WriteBytesPerSecond,
      ProcessField.ThreadCount,
      ProcessField.Name,
      ProcessField.Pid,
      ProcessField.UserName,
      ProcessField.StartTime,
    ]) {
      var chosen = column;
      sort.DropDownItems.Add(Item(column.Header(), () => {
        this._view.SortColumn = chosen;
        this._view.SortDescending = chosen.PrefersDescending();
        this.Refresh();
      }));
    }

    sort.DropDownItems.Add(new ToolStripSeparator());
    sort.DropDownItems.Add(Item("Reverse", () => {
      this._view.SortDescending = !this._view.SortDescending;
      this.RebuildColumns();
      this.Refresh();
    }));

    menu.Items.Add(sort);

    view.DropDownItems.Add(new ToolStripSeparator());
    view.DropDownItems.Add(Item("Select columns…", this.ChooseColumns));
    view.DropDownItems.Add(Item("Performance…", this.ShowPerformance));
    view.DropDownItems.Add(Item("Colour legend…", () => new LegendWindow().ShowDialog()));

    var process = new ToolStripMenuItem("Process");
    process.DropDownItems.Add(Item("End process", () => this.Act("end", key => this._actions!.Terminate(key))));
    process.DropDownItems.Add(Item("Suspend", () => this.Act("suspend", key => this._actions!.Suspend(key))));
    process.DropDownItems.Add(Item("Resume", () => this.Act("resume", key => this._actions!.Resume(key))));
    process.DropDownItems.Add(new ToolStripSeparator());
    process.DropDownItems.Add(Item("Read handle count", this.FillHandleCounts));
    process.DropDownItems.Add(Item("Properties…", this.ShowProperties));
    process.DropDownItems.Add(Item("Refresh details", () => this._details.Invalidate()));
    menu.Items.Add(process);

    this.Controls.Add(menu);
  }

  #endregion

  #region refresh

  private new void Refresh() {
    this.ApplyLayout();
    // Once a tick rather than once a change: a window being dragged would otherwise be a write per
    // frame, and the saver writes nothing when nothing actually differs.
    this._autoSaver?.Flush();
    this._sampler.Sample();
    var snapshot = this._sampler.Current;
    var delta = this._sampler.Delta;

    this._cpuHistory.Add(delta.SystemCpuPercent);
    this._memoryHistory.Add(MemoryPercent(snapshot.System));

    this._view.Rebuild(snapshot, delta);

    // History only for the rows on screen (PRD §3.3). TopIndex and the visible count come from the
    // control, so scrolling changes which processes are tracked rather than tracking all of them.
    this._rowHistory.Update(snapshot, delta, this._view, this._tree.TopIndex, this._tree.VisibleNodeCount + 8);
    this._binder.Sync(snapshot, delta, this._view);
    this._cores.Bind(delta);
    this.StretchLastColumn();
    this._performance?.UpdateFromSample();
    foreach (var window in this._properties)
      window.UpdateFromSample(snapshot, this._binder.RowFor(window.Key));

    this._cpuPlot.Invalidate();
    this._memoryPlot.Invalidate();

    this.UpdateStatus(snapshot, delta);
    this.UpdateDetails();
  }

  /// <summary>
  /// The layout the toolkit cannot do for us: the plot strip's widths, and the splitter's first
  /// placement. Both are no-ops once they have settled, so running this every tick costs nothing.
  /// </summary>
  private void ApplyLayout() {
    if (this._plots.Width != this._laidOutWidth) {
      this._laidOutWidth = this._plots.Width;
      this.LayOutPlots();
    }

    if (this._splitPlaced || this._split.Height <= 240)
      return;

    this._splitPlaced = true;
    var percent = this._settings.SplitPercent > 0 ? this._settings.SplitPercent : 55;
    this._split.SplitterDistance = this._split.Height * percent / 100;
  }

  private static Rate MemoryPercent(in SystemCounters system) {
    if (!system.TotalMemoryBytes.HasValue || system.TotalMemoryBytes.Value == 0 || !system.AvailableMemoryBytes.HasValue)
      return Rate.Gap;

    var total = system.TotalMemoryBytes.Value;
    var used = total - Math.Min(total, system.AvailableMemoryBytes.Value);
    return Rate.Of(used * 100d / total);
  }

  private void UpdateStatus(SystemSnapshot snapshot, SnapshotDelta delta) {
    var mode = this._sampler.CpuPercentMode == CpuPercentMode.PerCore ? "per core" : "normalized";
    this._status.Text =
      $"{this._view.RowCount} of {snapshot.ProcessCount} processes  ·  "
      + $"CPU {Humanize.Percent(delta.SystemCpuPercent)}% ({mode})  ·  "
      + $"memory {Humanize.Bytes(snapshot.System.TotalMemoryBytes)} total, {Humanize.Bytes(snapshot.System.AvailableMemoryBytes)} free  ·  "
      + $"sample {this._sampler.LastSampleDuration.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms";
  }

  private void UpdateDetails() {
    var row = this._binder.SelectedRow;
    if (row is null)
      return;

    this._details.Select(row.Key);
    if (this._sampler.Current.TryGetProcess(row.Key, out var process))
      this._details.UpdateOverview(in process, row);

    this._details.Refresh();
  }

  private void ChooseColumns() {
    var chooser = new ColumnChooser(this._columns);
    chooser.ShowDialog();
    if (!chooser.Accepted)
      return;

    this._columns.Clear();
    this._columns.AddRange(chooser.Selection);
    this.RebuildColumns();
    this.Refresh();
  }

  private void FillHandleCounts() {
    // Only the selected row: on Linux this makes the kernel walk the process's whole descriptor
    // table, which is why it is not in the sample (PRD §3.5).
    if (this._binder.SelectedRow is not { } row)
      return;

    this._binder.HandleCounts[row.Key] = this._probe.GetHandleCount(row.Key);
    this.Refresh();
  }

  #endregion

  private void Act(string what, Func<ProcessKey, ActionResult> action) {
    if (this._binder.SelectedRow is not { } row)
      return;

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    // Confirmed before it happens, and the target named unambiguously — a pid on its own is not a
    // name, and the row under the pointer may have moved (PRD §6.4).
    var answer = MessageBox.Show(
      $"{char.ToUpper(what[0], CultureInfo.CurrentCulture)}{what[1..]} {row.Name} ({row.Pid})?",
      "Process Manager",
      MessageBoxButtons.YesNo
    );

    if (answer != DialogResult.Yes)
      return;

    var result = action(row.Key);
    if (!result.Succeeded)
      MessageBox.Show(result.Detail ?? result.Outcome.ToString(), "Process Manager");

    this.Refresh();
  }


  /// <summary>
  /// Opens the performance view (PRD §45).
  /// </summary>
  /// <remarks>
  /// Modal, and deliberately: it is a page somebody opens to read, not a palette to keep beside the
  /// list, and a modeless one would need its own timer and its own lifetime. The plots share the
  /// main window's rings, so it opens showing the last sixty seconds rather than starting blank.
  /// </remarks>
  private readonly List<ProcessPropertiesWindow> _properties = [];

  /// <summary>
  /// Opens the selected process in a window of its own (PRD §26).
  /// </summary>
  /// <remarks>
  /// One per process, and a second request for the same one brings the existing window forward
  /// rather than opening a duplicate — several windows are the point, several of the *same* process
  /// are not.
  /// </remarks>
  private void ShowProperties() {
    if (this._binder.SelectedRow is not { } row)
      return;

    foreach (var open in this._properties)
      if (open.Key == row.Key) {
        // The toolkit has no Activate; focusing it is the nearest thing and is enough to say
        // "this one is already open" rather than opening a second.
        open.Focus();
        return;
      }

    var window = new ProcessPropertiesWindow(this._probe, row.Key, row.Name);
    window.FormClosed += (_, _) => this._properties.Remove(window);
    this._properties.Add(window);
    window.Show();
  }

  private PerformanceWindow? _performance;

  /// <summary>
  /// Opens the system information window (PRD §45), or brings the open one forward.
  /// </summary>
  /// <remarks>
  /// Modeless and refreshed from the sample tick below, so its numbers move. It was modal and drawn
  /// once — a performance page that never updated, which is worse than not having one.
  /// </remarks>
  /// <summary>
  /// Opens the performance page and returns it, so a capture run can photograph it too.
  /// </summary>
  /// <remarks>
  /// §45 is almost entirely about what the page looks like, and the only evidence of that in CI is
  /// a picture of it. A page nobody photographs is one whose layout regressions ship.
  /// </remarks>
  public PerformanceWindow OpenPerformance() {
    this.ShowPerformance();
    return this._performance!;
  }

  private void ShowPerformance() {
    if (this._performance is not null) {
      this._performance.UpdateFromSample();
      return;
    }

    var window = new PerformanceWindow(this._probe, this._sampler);
    // Forgetting it on close is what keeps the tick from refreshing a window that is gone.
    window.FormClosed += (_, _) => this._performance = null;
    this._performance = window;
    window.Show();
  }

}
