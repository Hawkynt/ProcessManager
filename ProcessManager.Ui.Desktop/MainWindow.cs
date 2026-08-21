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
  private readonly NavigationView _rail = new();
  private readonly ToolStrip _commands = new();
  private readonly Panel _content = new();
  private readonly List<ShellView> _views = [];
  private readonly ShellViews _shell;
  private ShellView? _shown;
  private ToolStripButton? _lowerPaneButton;
  private ToolStripMenuItem? _lowerPaneItem;
  private readonly NativeForms.Timer _timer = new();
  private readonly HistoryRing<Rate> _cpuHistory = new(600);
  private readonly HistoryRing<Rate> _memoryHistory = new(600);
  private readonly ProcessHistory _rowHistory = new();
  private readonly DesktopColumns _columns = new(ColumnSet.Default);
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
    this._details = new(probe) { Actions = actions };
    this._shell = new(probe);

    this.Text = "Process Manager";
    this.Bounds = new(0, 0, 1240, 820);

    // Without this the window can be grown and never shrunk, which reads as one that does not
    // resize at all. GTK advertises a minimum to the window manager, and with no explicit limit it
    // computes one from the content: every docked child asks for the width it currently has, so the
    // floor is exactly the size the window opened at. Naming a real minimum replaces that computed
    // one (PRD §45.1).
    this.MinimumSize = new(900, 600);

    // Added in reverse of the order they stack, because that is how the toolkit docks: its layout
    // pass walks the children backwards, so the child added *last* claims its edge first and ends up
    // outermost. The window reads menu, command bar, plots, then the rail beside the content, with
    // the status line along the foot — so the adds run the other way round.
    //
    // This is not a style note. The strip used to be added after the menu on the assumption that
    // earlier meant outer, and the window shipped with its plots above its menu bar for exactly as
    // long as nobody looked at a picture of it.
    this.BuildSplit();
    this.BuildRail();
    this.BuildStatus();
    this.BuildFilterBar();
    this.BuildPlots();
    this.BuildCommandBar();
    this.BuildMenu();
    this.BuildViews();

    this._timer.Interval = 1000;
    this._timer.Tick += (_, _) => this.Refresh();

    // The layout runs on the sample tick as well, but a window being dragged has to follow the
    // pointer rather than the second hand — a whole second of the plots being the old width while
    // the frame is the new one is exactly what "it does not resize" looks like.
    this.Resize += (_, _) => this.ApplyLayout();
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
    ProcessRow.Thresholds = settings.Thresholds;

    this._view.SortColumn = settings.SortField;
    this._view.SortDescending = settings.SortDescending;
    this._view.TreeMode = settings.TreeMode;
    this._sampler.CpuPercentMode = settings.CpuMode;
    this.Interval = (int)Math.Round(settings.IntervalSeconds * 1000);

    if (settings.DesktopColumns.Length > 0)
      this._columns.Apply(settings.DesktopColumns);

    // After the set, because a width belongs to a column that is showing: applying them the other
    // way round would drop every width the moment the column list was replaced.
    foreach (var (field, width) in settings.DesktopColumnWidths)
      this._columns.Restore(field, width);

    this._view.Grouping = settings.Grouping;

    if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
      this.Bounds = new(this.Bounds.X, this.Bounds.Y, settings.WindowWidth, settings.WindowHeight);

    this.LowerPaneVisible = settings.LowerPaneVisible;
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
      DesktopColumns = this._columns.Fields,
      DesktopColumnWidths = this._columns.ChosenWidths,
      Grouping = this._view.Grouping,
      Thresholds = ProcessRow.Thresholds,
      WindowWidth = this.Width,
      WindowHeight = this.Height,
      LowerPaneVisible = this.LowerPaneVisible,
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
    builder.AppendLine($"columns:      {this._tree.Columns.Count} — {this.DescribeColumns()}");
    builder.AppendLine($"sorted by:    {this.DescribeSort()}");
    builder.AppendLine(
      $"grouped by:   {Settings.UserSettings.NameOfGrouping(this._view.Grouping)}, "
      + $"{this._view.Groups.Count} heading(s), {this._view.MatchCount} process row(s)"
    );

    builder.AppendLine(
      $"filter:       {(this._filterBox.Text.Length == 0 ? "(none)" : this._filterBox.Text)}"
      + $", case {(this._view.CaseSensitive ? "matters" : "ignored")}"
      + $"{(this._filterNote.Text.Length > 0 ? " — " + this._filterNote.Text : string.Empty)}"
    );

    builder.AppendLine($"ticked rows:  {this.TickedKeys().Count}");
    builder.AppendLine($"split at:     {this._split.SplitterDistance}, lower pane {(this.LowerPaneVisible ? "shown" : "hidden")}");
    builder.AppendLine($"rail:         {this._rail.Bounds}, {this._rail.Items.Count} entries — {string.Join(", ", this._rail.Items)}");
    builder.AppendLine($"command bar:  {this._commands.Bounds}, {this._commands.Items.Count} items");
    builder.AppendLine($"content:      {this._content.Bounds} showing {this._shown?.Title ?? "nothing"}");
    builder.AppendLine($"plots:        cpu {this._cpuPlot.Bounds}, memory {this._memoryPlot.Bounds}, cores {this._cores.Bounds}");
    builder.AppendLine($"topology:     {this._cores.Topology.Cores.Count} logical, {this._cores.Topology.Packages.Count} socket(s), hybrid {this._cores.Topology.IsHybrid}");
    builder.AppendLine($"status:       {this._status.Text}");
    return builder.ToString();
  }

  /// <summary>The columns as they stand — their order and the width each ended up with.</summary>
  private string DescribeColumns() {
    var parts = new List<string>(this._columns.Count);
    for (var i = 0; i < this._columns.Count; ++i)
      parts.Add(
        $"{ColumnSet.Info(this._columns.FieldAt(i)).Key}:{this._columns.WidthAt(i).ToString(CultureInfo.InvariantCulture)}"
        + (i == this._columns.Current ? "*" : string.Empty)
      );

    return string.Join(" ", parts);
  }

  /// <summary>The sort, tie-breakers included — the half of §11 a picture of the header cannot show.</summary>
  private string DescribeSort() {
    var parts = new List<string> {
      ColumnSet.Info(this._view.SortColumn).Key + (this._view.SortDescending ? " desc" : " asc"),
    };

    foreach (var key in this._view.SecondarySort)
      parts.Add("then " + ColumnSet.Info(key.Field).Key + (key.Descending ? " desc" : " asc"));

    return string.Join(", ", parts);
  }

  /// <summary>What the rows are grouped by (PRD §83).</summary>
  public ProcessGrouping Grouping => this._view.Grouping;

  /// <summary>Start with a flat sorted list rather than the tree. Set before <see cref="Start"/>.</summary>
  public bool FlatMode {
    get => !this._view.TreeMode;
    set => this._view.TreeMode = !value;
  }

  /// <summary>The refresh interval in milliseconds.</summary>
  /// <remarks>
  /// The two summary plots are told it as well as the timer. Their axis is a minute of wall clock
  /// rather than a count of samples, and a machine sampled every four seconds would otherwise put
  /// four minutes of history under a label reading sixty seconds (PRD §45.4).
  /// </remarks>
  public int Interval {
    get => this._timer.Interval;
    set {
      this._timer.Interval = Math.Clamp(value, 250, 60_000);
      this._cpuPlot.SecondsPerSample = this._memoryPlot.SecondsPerSample = this._timer.Interval / 1000d;
    }
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
    // The first *process*, which in a grouped list is not the first node: a heading cannot be
    // selected, and asking for one leaves the pane empty and the capture showing an empty box
    // (PRD §83).
    if (this._binder.FirstProcessNode() is not { } node)
      return;

    this._tree.SelectedNode = node;
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

    // Into the content region rather than straight into the window: the rail beside it swaps what
    // is in here, and the process tree is one of the things it swaps to (PRD §10).
    this._content.Dock = DockStyle.Fill;
    this.Controls.Add(this._content);
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

    // Tick boxes, for the bulk actions §11 asks every table for. The terminal has had a gutter of
    // them since it grew a mouse; this is the same thing said with a check box, and it is a mark on
    // the row rather than a colour so that it survives a monochrome theme and a reader who cannot
    // see one (PRD §11, §74).
    this._tree.CheckBoxes = true;

    // The three history columns are drawn, not written.
    this._tree.CellPaint += this.OnCellPaint;
    // Deliberately not ColumnClick. The toolkit reports a header press as a bare column index with
    // no modifier state and no way to see a drag, and §11 wants three gestures on that header —
    // click to sort, shift-click to add a tie-breaker, and drag to move or resize. The control
    // raises ColumnClick only when something is subscribed to it, so leaving it alone hands the
    // whole header to the handlers below rather than having two of them fight over one press.
    this._tree.MouseDown += this.OnTreeMouseDown;
    this._tree.MouseMove += this.OnTreeMouseMove;
    this._tree.MouseUp += this.OnTreeMouseUp;
    // A heading is not a process: it cannot be selected, which is what makes it un-actionable —
    // every verb in this window reads the selection as a ProcessRow and declines without one
    // (PRD §83).
    this._tree.BeforeSelect += (_, e) => e.Cancel = e.Node?.Tag is GroupRow;
    this._tree.AfterSelect += (_, _) => this.UpdateDetails();
    // Double-click is how every tool of this kind opens a process, and the gesture people try first.
    this._tree.MouseDoubleClick += (_, _) => this.ShowProperties();
    this._tree.ContextMenuStrip = this.BuildContextMenu();
  }

  #region the header's three gestures (PRD §11)

  private int _pressedColumn = -1;
  private int _resizingColumn = -1;
  private int _pressX;
  private int _pressWidth;
  private bool _dragged;

  /// <summary>The header's own y range. A press below it belongs to the rows.</summary>
  private bool IsHeader(int y) => this._tree.ShowColumnHeaders && y >= 0 && y < this._tree.ItemHeight;

  /// <summary>
  /// A press in the header: near a boundary it grabs the boundary, otherwise it grabs the column.
  /// </summary>
  /// <remarks>
  /// Which of the two it is has to be decided here rather than on release, because a resize has to
  /// follow the pointer from the first pixel it moves. The x is put into the columns' own
  /// coordinates first: the table scrolls sideways now, and a header hit-test done in the control's
  /// coordinates picks whichever column happens to be under the unscrolled position.
  /// </remarks>
  private void OnTreeMouseDown(object? sender, MouseEventArgs e) {
    this._pressedColumn = -1;
    this._resizingColumn = -1;
    this._dragged = false;
    if (e.Button != MouseButtons.Left || !this.IsHeader(e.Y))
      return;

    var x = e.X + this._tree.HorizontalOffset;
    this._pressX = x;
    var edge = this._columns.EdgeAt(x);
    if (edge >= 0) {
      this._resizingColumn = edge;
      this._pressWidth = this._columns.WidthAt(edge);
      return;
    }

    this._pressedColumn = this._columns.HitTest(x);
    if (this._pressedColumn >= 0)
      this._columns.SetCurrent(this._pressedColumn);
  }

  private void OnTreeMouseMove(object? sender, MouseEventArgs e) {
    if (this._resizingColumn >= 0) {
      this._dragged = true;
      this._columns.SetWidth(this._resizingColumn, this._pressWidth + (e.X + this._tree.HorizontalOffset - this._pressX));
      // The widths, not the whole header: rebuilding the columns on every pointer move would drop
      // and recreate six controls a frame, and the header would flicker under the hand doing it.
      this.ApplyWidths();
      return;
    }

    if (this._pressedColumn >= 0 && Math.Abs(e.X + this._tree.HorizontalOffset - this._pressX) > _DragSlop)
      this._dragged = true;
  }

  /// <summary>How far the pointer may wander before a click becomes a drag.</summary>
  /// <remarks>
  /// Without it every click is a one-pixel drag, and a header that reorders itself when somebody
  /// meant to sort by it is worse than one that does not reorder at all.
  /// </remarks>
  private const int _DragSlop = 5;

  private void OnTreeMouseUp(object? sender, MouseEventArgs e) {
    var pressed = this._pressedColumn;
    var resizing = this._resizingColumn;
    var dragged = this._dragged;
    this._pressedColumn = -1;
    this._resizingColumn = -1;
    this._dragged = false;

    if (resizing >= 0) {
      this.RebuildColumns();
      return;
    }

    if (pressed < 0)
      return;

    if (dragged) {
      var target = this._columns.HitTest(e.X + this._tree.HorizontalOffset);
      if (target >= 0 && this._columns.MoveTo(pressed, target)) {
        this.RebuildColumns();
        this.Rebind();
      }

      return;
    }

    this.SortByColumn(pressed, (e.Modifiers & KeyModifiers.Shift) != 0);
  }

  /// <summary>
  /// What clicking a header means (PRD §11).
  /// </summary>
  /// <remarks>
  /// Shift-click adds a tie-breaker rather than replacing the sort, which is the one gesture every
  /// table with a multi-column sort has agreed on, and the one the terminal already binds.
  /// </remarks>
  private void SortByColumn(int index, bool addKey) {
    if ((uint)index >= (uint)this._columns.Count)
      return;

    var sortBy = this._columns.FieldAt(index);
    if (!ColumnSet.Info(sortBy).IsSortable) {
      // A history column has no text to sort by, and sorting by "the shape of a graph" is not a
      // thing. Clicking one does nothing rather than doing something arbitrary.
      return;
    }

    if (addKey) {
      this._view.AddSortKey(sortBy, sortBy.PrefersDescending());
      this.RebuildColumns();
      this.Rebind();
      return;
    }

    // Clicking the current column reverses it, the way every list does.
    if (this._view.SortColumn == sortBy)
      this._view.SortDescending = !this._view.SortDescending;
    else {
      this._view.SortColumn = sortBy;
      this._view.SortDescending = sortBy.PrefersDescending();
    }

    this._view.ClearSecondarySort();
    this.RebuildColumns();
    this.Rebind();
  }

  /// <summary>Pushes the chosen widths onto the header without recreating it.</summary>
  private void ApplyWidths() {
    var count = Math.Min(this._tree.Columns.Count, this._columns.Count);
    for (var i = 0; i < count; ++i)
      this._tree.Columns[i].Width = this._columns.WidthAt(i);

    this._stretchedTo = 0;
    this.StretchLastColumn();
  }

  #endregion

  /// <summary>Recreates the header from the chosen column set, in its chosen order and widths.</summary>
  private void RebuildColumns() {
    this._tree.Columns.Clear();
    for (var i = 0; i < this._columns.Count; ++i) {
      var column = this._columns.FieldAt(i);
      var info = ColumnSet.Info(column);
      var header = info.Header;
      if (info.IsSortable && column == this._view.SortColumn)
        header = this._view.SortDescending ? header + " ▾" : header + " ▴";
      else if (this.SecondarySortRank(column) is { } rank)
        // A digit, not a second arrow: two arrows in a header row say "sorted twice" and nothing
        // about which one wins. The terminal marks its tie-breakers the same way (PRD §11).
        header += " " + rank.ToString(CultureInfo.InvariantCulture);

      var which = column;
      this._tree.Columns.Add(new(header, this._columns.WidthAt(i), Cell) {
        TextAlign = info.RightAligned ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
      });

      // A grouping heading is not a process, so it has no cell in any column but the first — where
      // the binder has already put its label in the node's own text (PRD §83).
      string Cell(TreeNode node) => node.Tag is ProcessRow row ? row.TextOf(which) : string.Empty;
    }

    this.StretchLastColumn();
  }

  /// <summary>Which tie-breaker this column is, counting the primary as one.</summary>
  private int? SecondarySortRank(ProcessField field) {
    var keys = this._view.SecondarySort;
    for (var i = 0; i < keys.Count; ++i)
      if (keys[i].Field == field)
        return i + 2;

    return null;
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

    // The scrollbar sits over the right-hand edge, so the width of the control is not the width
    // available to draw in. Stretching the last column to the full width put its right-aligned
    // values underneath the scrollbar and cut the last character off every one of them: "6.8G" of
    // private bytes was drawn as "6.8", which is not a smaller number, it is a different one.
    //
    // Subtracted whether or not the bar is showing, because whether it is showing is not something
    // this side can ask. A process list long enough to need one is the ordinary case, and the cost
    // when there is none is that the last column ends a few pixels early.
    var available = this._tree.Width - this._theme.ScrollBarSize;
    if (available <= 0)
      return;

    var used = 0;
    for (var i = 0; i < count - 1; ++i)
      used += this._tree.Columns[i].Width;

    var last = this._columns.Count == count ? this._columns.WidthAt(count - 1) : 120;
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

    var info = ColumnSet.Info(this._columns.FieldAt(e.ColumnIndex));
    if (e.Node.Tag is not ProcessRow row) {
      // A grouping heading: its own band across the row, so it reads as a divider rather than as a
      // process with every cell empty (PRD §83).
      if (e.Node.Tag is GroupRow)
        e.Graphics.FillRectangle(RowPalette.GroupHeading(e.Theme), e.Bounds);

      return;
    }

    // A process leaning hard on something gets its cell marked, not its row. The row's colour
    // already says what kind of process it is, which is a different question — colouring the row
    // for both would mean one of the two facts quietly winning (PRD §23).
    if (RowPalette.HeatColour(row.HeatOf(info.Id), e.Theme) is { } heat)
      e.Graphics.FillRectangle(heat, e.Bounds);

    if (info.Series is not { } series) {
      this.PaintMatch(e, info, row.TextOf(info.Id));
      return;
    }

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
  /// Marks the run of characters the filter matched, inside the cell (PRD §11).
  /// </summary>
  /// <remarks>
  /// Behind the text rather than over it, and only the run: highlighting the whole cell would say
  /// "this row matched", which the list already says by showing the row at all. What somebody needs
  /// is <em>why</em> — and the word is often forty characters into a command line, where no amount
  /// of staring finds it.
  /// <para>
  /// Measured with the same renderer that draws the string, because a run located by counting
  /// characters lands in the wrong place in every proportional font (PRD §45.9).
  /// </para>
  /// </remarks>
  private void PaintMatch(TreeListViewCellPaintEventArgs e, FieldDescriptor info, string text) {
    if (this._highlight is not { Length: > 0 } needle || text.Length == 0)
      return;

    var comparison = this._view.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    var at = text.IndexOf(needle, comparison);
    if (at < 0)
      return;

    var font = e.Theme.DefaultFont;
    var before = at == 0 ? 0 : e.Graphics.MeasureText(text[..at], font).Width;
    var run = e.Graphics.MeasureText(text.Substring(at, needle.Length), font).Width;
    if (run <= 0)
      return;

    // Where the control is about to draw the string, which is not the cell's left edge. Every column
    // is inset by a pad; the tree column is also pushed right by one indent per level, by the
    // expander's own cell and by the tick box. Getting this wrong does not look like a rounding
    // error — the first capture drew every highlight out in the indent, four columns from the word
    // it had matched.
    var whole = e.Graphics.MeasureText(text, font).Width;
    var left = info.RightAligned
      ? e.Bounds.Right - _CellPad - whole + before
      : e.Bounds.X + this.TextInsetOf(e) + before;

    var clipped = Math.Min(run, e.Bounds.Right - 1 - left);
    if (clipped <= 0 || left >= e.Bounds.Right - 1)
      return;

    e.Graphics.FillRectangle(
      RowPalette.MatchHighlight(e.Theme),
      new(Math.Max(e.Bounds.X, left), e.Bounds.Y, clipped, e.Bounds.Height - 1)
    );
  }

  /// <summary>
  /// How far into a cell the control starts drawing its text.
  /// </summary>
  /// <remarks>
  /// These are the toolkit's own insets, mirrored here because it exposes no way to ask. That is a
  /// duplication and it is written down as one: if <c>TreeListView</c> ever changes them, the
  /// highlight lands beside the word rather than on it, and this is the line to change. The check
  /// box's size is at least a public constant, so only the pads are guesses.
  /// </remarks>
  private int TextInsetOf(TreeListViewCellPaintEventArgs e) {
    if (e.ColumnIndex != 0)
      return _CellPad;

    var indent = (e.Node.Level + 1) * e.Bounds.Height;
    return indent + (this._tree.CheckBoxes ? _CheckCellWidth : 0) + _CellPad;
  }

  private const int _CellPad = 2;

  /// <summary>The tick box's cell: fourteen pixels of box and four of gap, as the toolkit draws it.</summary>
  private const int _CheckCellWidth = 18;

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

  private ContextMenuStrip BuildContextMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(Item("Properties", () => this.UpdateDetails()));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("End task", this.EndTask));
    menu.Items.Add(Item("End process", () => this.Act("end", key => this._actions!.Terminate(key), _EndsWithoutAsking)));
    menu.Items.Add(Item("End process tree", this.EndTree));
    menu.Items.Add(Item("End the ticked processes", this.EndTicked));
    menu.Items.Add(Item("Restart", this.RestartProcess));
    menu.Items.Add(Item("Suspend", () => this.Act("suspend", key => this._actions!.Suspend(key))));
    menu.Items.Add(Item("Resume", () => this.Act("resume", key => this._actions!.Resume(key))));
    menu.Items.Add(Item("Send signal…", this.SendSignal));
    menu.Items.Add(this.FreezerMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(this.PriorityMenu());
    menu.Items.Add(this.IoPriorityMenu());
    menu.Items.Add(this.SchedulingMenu());
    menu.Items.Add(Item("Set affinity…", this.ChooseAffinity));
    menu.Items.Add(Item("Limits…", this.ShowLimits));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(this.NavigationMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("Read handle count", this.FillHandleCounts));
    menu.Items.Add(Item("Properties…", this.ShowProperties));
    menu.Items.Add(Item("Refresh details", () => this._details.Invalidate()));
    menu.Items.Add(new ToolStripSeparator());
    // Right-click is where a table's copy lives, whatever the menu bar also offers.
    menu.Items.Add(Item("Copy row, or every ticked row", this.CopyRows));
    menu.Items.Add(Item("Copy cell", this.CopyCell));
    menu.Items.Add(Item("Export table…", this.ExportTable));
    return menu;
  }

  /// <summary>
  /// The nice values worth offering, named rather than numbered.
  /// </summary>
  /// <remarks>
  /// Nice runs backwards — -20 is the most favourable and 19 the least — which almost nobody
  /// remembers, so the menu says what each one does and keeps the number beside it for the people
  /// who do. The five are the ones with names on every other tool; the whole range is available from
  /// the command line for anyone who wants 7.
  /// </remarks>
  private static readonly (string Label, int Nice)[] _Priorities = [
    ("Real-time (-20)", -20),
    ("High (-10)", -10),
    ("Normal (0)", 0),
    ("Below normal (5)", 5),
    ("Idle (19)", 19),
  ];

  private ToolStripMenuItem PriorityMenu() {
    var menu = new ToolStripMenuItem("Priority");
    foreach (var (label, nice) in _Priorities) {
      var value = nice;
      menu.DropDownItems.Add(Item(label, () => this.Act($"set priority to {value}", key => this._actions!.SetPriority(key, value))));
    }

    return menu;
  }

  /// <summary>
  /// The I/O classes (PRD §26).
  /// </summary>
  /// <remarks>
  /// Idle is the one worth having and the reason this menu exists: a backup or an indexer left at
  /// normal CPU priority but moved to idle I/O keeps running at full speed and simply yields the
  /// disk whenever anything else wants it. Real-time is offered and will usually be refused —
  /// naming it and explaining the refusal is more use than hiding it.
  /// </remarks>
  private ToolStripMenuItem IoPriorityMenu() {
    var menu = new ToolStripMenuItem("I/O priority");
    foreach (var (label, priority) in new (string, IoPriority)[] {
      ("Real-time", new(IoPriorityClass.Realtime, 0)),
      ("High", new(IoPriorityClass.BestEffort, 0)),
      ("Normal", new(IoPriorityClass.BestEffort, 4)),
      ("Low", new(IoPriorityClass.BestEffort, 7)),
      ("Idle", new(IoPriorityClass.Idle)),
    }) {
      var value = priority;
      menu.DropDownItems.Add(Item(label, () => this.Act($"set I/O priority to {value}", key => this._actions!.SetIoPriority(key, value))));
    }

    return menu;
  }

  /// <summary>
  /// The scheduler classes (PRD §25.2).
  /// </summary>
  /// <remarks>
  /// The names are the kernel's, in brackets, so that what the menu did can be checked against what
  /// <c>chrt</c> reports. The real-time entries are offered and will usually be refused — naming
  /// them and explaining the refusal is more use than hiding them, which is the same call the I/O
  /// menu above makes.
  /// </remarks>
  private ToolStripMenuItem SchedulingMenu() {
    var menu = new ToolStripMenuItem("Scheduling class");
    foreach (var choice in SchedulingClasses.Offered) {
      var chosen = choice;
      menu.DropDownItems.Add(Item(
        chosen.Name,
        () => this.Act(
          $"move to {chosen.Name}",
          key => this._actions!.SetSchedulingClass(key, chosen.Policy, chosen.Priority),
          chosen.IsRealTime
            ? "A real-time task cannot be preempted by an ordinary one. A real-time process that spins never gives the processor back."
            : null
        )
      ));
    }

    return menu;
  }

  /// <summary>
  /// Stopping a whole unit, which on Linux is the cgroup and not the process (PRD §25.1, §38).
  /// </summary>
  /// <remarks>
  /// A submenu of its own rather than two items beside Suspend, because it is not a stronger
  /// suspend — it is a different target. Suspend stops the one process and leaves everything it
  /// started running; freezing stops the cgroup, every cgroup below it, and anything either of them
  /// starts while it is frozen. Putting them side by side under one heading would invite exactly the
  /// reading §5.3 forbids.
  /// </remarks>
  private ToolStripMenuItem FreezerMenu() {
    var menu = new ToolStripMenuItem("Whole cgroup");
    menu.DropDownItems.Add(Item("Freeze…", () => this.Freeze(true)));
    menu.DropDownItems.Add(Item("Thaw", () => this.Freeze(false)));
    return menu;
  }

  /// <summary>
  /// Sends a signal the menu does not have an item for (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// The confirmation comes after the choosing rather than inside the dialog, so that it is the same
  /// confirmation every other destructive item here uses and names the same four things: the action,
  /// the target, its pid and what it costs (PRD §90).
  /// </remarks>
  private void SendSignal() {
    if (this._binder.SelectedRow is not { } row)
      return;

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    var chooser = new SignalDialog(row.Name, row.Pid);
    chooser.ShowDialog();
    if (!chooser.Accepted || chooser.Chosen is not { } signal)
      return;

    this.Act(
      $"send {Signals.Describe(signal)} to",
      key => this._actions!.SendSignal(key, signal),
      Signals.Consequence(signal)
    );
  }

  /// <summary>
  /// Freezes or thaws the cgroup the selected process is in (PRD §25.1, §38).
  /// </summary>
  /// <remarks>
  /// The confirmation names the cgroup and counts what is in it, because the selected row is one
  /// member of it and usually not the interesting one: freezing a service's process freezes the
  /// service, and freezing a container's shell freezes the container (PRD §5.5).
  /// </remarks>
  private void Freeze(bool frozen) {
    if (this._binder.SelectedRow is not { } row)
      return;

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    if (!frozen) {
      // Thawing is the reversal and costs nothing, so it is not confirmed — the same call
      // "End task" makes about being the reversible one.
      this.Report(this._actions.FreezeCgroup(row.Key, false));
      this.Refresh();
      return;
    }

    var cgroup = this._probe.DescribeCgroup(row.Key);
    if (cgroup is null) {
      MessageBox.Show(
        $"{row.Name} (PID {row.Pid}) is in no cgroup this build can read. Only the unified hierarchy "
        + "(cgroup v2) has a freezer.",
        "Process Manager"
      );

      return;
    }

    if (cgroup.Freezer is not { Supported: true }) {
      MessageBox.Show("This kernel's cgroups have no freezer; it arrived in Linux 5.2.", "Process Manager");
      return;
    }

    // Named for what it counts. `pids.current` counts threads, so a confirmation reading "892
    // processes" about a cgroup holding 58 of them states the consequence wrongly by an order of
    // magnitude — which is the one thing §5.5 exists to prevent.
    var members = cgroup.PidsCurrent.HasValue
      ? $"every process in that cgroup — {Humanize.Count(cgroup.PidsCurrent)} tasks between them"
      : "every process in that cgroup";

    var answer = MessageBox.Show(
      $"Freeze {cgroup.Path}?\n\n"
      + $"This stops {members}, not only {row.Name} (PID {row.Pid}). "
      + "They keep every file, socket and lock they hold, and each still reports itself as sleeping — "
      + "the kernel has no process state for frozen.",
      "Process Manager",
      MessageBoxButtons.YesNo
    );

    if (answer != DialogResult.Yes)
      return;

    this.Report(this._actions.FreezeCgroup(row.Key, true));
    this.Refresh();
  }

  /// <summary>
  /// Every ceiling on the selected process, and its standing with the out-of-memory killer
  /// (PRD §25.2, §25.5).
  /// </summary>
  private void ShowLimits() {
    if (this._binder.SelectedRow is not { } row)
      return;

    new ResourceLimitsDialog(this._probe, this._actions, row.Key, row.Name).ShowDialog();
    this.Refresh();
  }

  /// <summary>
  /// Getting from a process to the things around it (PRD §25.3).
  /// </summary>
  /// <remarks>
  /// Grouped rather than scattered through the menu, because they are all the same gesture — "take
  /// me to the thing this row refers to" — and because none of them changes anything, which is what
  /// keeps them away from the items that do (PRD §5.5).
  /// </remarks>
  private ToolStripMenuItem NavigationMenu() {
    var menu = new ToolStripMenuItem("Go to");
    menu.DropDownItems.Add(Item("Parent process", this.GoToParent));
    menu.DropDownItems.Add(Item("Child processes", this.GoToChildren));
    menu.DropDownItems.Add(new ToolStripSeparator());
    menu.DropDownItems.Add(Item("Executable folder", this.RevealExecutable));
    menu.DropDownItems.Add(Item("Executable properties…", this.ShowExecutableProperties));
    menu.DropDownItems.Add(new ToolStripSeparator());
    menu.DropDownItems.Add(Item("Search the web for this name…", this.SearchTheWeb));
    return menu;
  }

  /// <summary>
  /// Selects whatever started this process (PRD §25.3).
  /// </summary>
  /// <remarks>
  /// A parent that is not in the list is the ordinary case rather than an error: a process whose
  /// parent has exited was reparented to init, and one belonging to another user is filtered out of
  /// the view. Both are worth saying, because "nothing happened" is what a broken menu item looks
  /// like too.
  /// </remarks>
  private void GoToParent() {
    if (this._binder.SelectedRow is not { } row)
      return;

    if (!this._sampler.Current.TryGetProcess(row.Key, out var process)) {
      MessageBox.Show($"{row.Name} (PID {row.Pid}) has ended.", "Process Manager");
      return;
    }

    if (process.ParentPid <= 0) {
      MessageBox.Show($"{row.Name} (PID {row.Pid}) has no parent; it was started by the kernel.", "Process Manager");
      return;
    }

    if (!this.SelectPid(process.ParentPid))
      MessageBox.Show(
        $"The parent of {row.Name} is PID {process.ParentPid}, which is not in the list — "
        + "it has ended, or it belongs to a user the current filter hides.",
        "Process Manager"
      );
  }

  /// <summary>
  /// Opens the row and moves to the first process it started (PRD §25.3).
  /// </summary>
  /// <remarks>
  /// The tree is expanded rather than a list of children being offered: the children are already on
  /// screen a row below, and a dialog listing what the window can show would be answering a question
  /// the window itself answers better. Selecting the first is what makes the arrow keys carry on
  /// from there.
  /// </remarks>
  private void GoToChildren() {
    if (this._binder.SelectedRow is not { } row)
      return;

    var children = new List<int>();
    foreach (var process in this._sampler.Current.Processes)
      if (process.ParentPid == row.Pid && process.Pid != row.Pid)
        children.Add(process.Pid);

    if (children.Count == 0) {
      MessageBox.Show($"Nothing is running under {row.Name} (PID {row.Pid}).", "Process Manager");
      return;
    }

    if (!this._view.TreeMode) {
      this._view.TreeMode = true;
      this.Refresh();
    }

    this._binder.NodeFor(row.Key)?.Expand();
    children.Sort();
    if (!this.SelectPid(children[0]))
      MessageBox.Show(
        $"{row.Name} (PID {row.Pid}) has {children.Count} child process{(children.Count == 1 ? string.Empty : "es")}, "
        + "none of which the current filter shows.",
        "Process Manager"
      );
  }

  /// <summary>The executable of the selected process, or null when there is none to be had.</summary>
  /// <remarks>Silent: whether an absence is worth a dialog is the caller's to decide.</remarks>
  private string? SelectedImagePath()
    => this._binder.SelectedRow is { } row
      && this._sampler.Current.TryGetProcess(row.Key, out var process)
      && process.ImagePath is { Length: > 0 } path
        ? path
        : null;

  /// <summary>Says why there is no executable to show, which is not always a fault.</summary>
  private void ExplainMissingImage() {
    if (this._binder.SelectedRow is not { } row)
      return;

    MessageBox.Show(
      $"The executable of {row.Name} (PID {row.Pid}) could not be read. "
      + "A kernel thread has none, and another user's is not readable without privilege.",
      "Process Manager"
    );
  }

  private void RevealExecutable() {
    if (this.SelectedImagePath() is not { } path) {
      this.ExplainMissingImage();
      return;
    }

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    if (DesktopOpen.Reveal(path) is not { } request) {
      MessageBox.Show("This platform has no desktop opener to hand the folder to.", "Process Manager");
      return;
    }

    var result = this._actions.Launch(request);
    if (!result.Outcome.Succeeded)
      MessageBox.Show(result.Outcome.Detail ?? result.Outcome.Outcome.ToString(), "Process Manager");
  }

  private void ShowExecutableProperties() {
    if (this.BuildExecutableProperties() is { } dialog)
      dialog.ShowDialog();
    else
      this.ExplainMissingImage();
  }

  /// <summary>
  /// The same box, shown without blocking, for the capture leg (PRD §9.6).
  /// </summary>
  /// <remarks>
  /// A dialog nothing photographs is a dialog nobody has looked at since it was written. The
  /// performance page is on this leg for the same reason, and this one joins it because a hand-laid
  /// box is exactly the kind that renders as an empty rectangle while every test around it passes.
  /// </remarks>
  public FilePropertiesDialog? OpenExecutableProperties() {
    var dialog = this.BuildExecutableProperties();
    dialog?.Show();
    return dialog;
  }

  private FilePropertiesDialog? BuildExecutableProperties() {
    if (this.SelectedImagePath() is not { } path)
      return null;

    // What the probe already read about the image, so the box need not open the file again for an
    // answer somebody has: the architecture and the interpreter come from its ELF header (PRD §14).
    var extra = new List<KeyValuePair<string, string>>();
    if (this._probe.DescribeImage(this._binder.SelectedRow!.Key) is { } image) {
      extra.Add(new("architecture", image.Architecture ?? (image.HeaderRead ? "unknown" : "—")));
      extra.Add(new("interpreter", image.Interpreter ?? (image.HeaderRead ? "statically linked" : "—")));
      extra.Add(new("directory", image.WorkingDirectory ?? "—"));
    }

    return new(path, extra, this._actions);
  }

  /// <summary>
  /// Looks a process's name up on the web (PRD §25.3).
  /// </summary>
  /// <remarks>
  /// Confirmed, and the confirmation names where it is going. Every other item on this menu is
  /// local; this one puts the name of something running on this machine onto somebody else's server,
  /// and a menu item that did that without saying so would be a disclosure dressed as a convenience
  /// (PRD §70).
  /// </remarks>
  private void SearchTheWeb() {
    if (this._binder.SelectedRow is not { } row)
      return;

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    var answer = MessageBox.Show(
      $"Search {DesktopOpen.SearchEngine} for \"{row.Name}\"?\n\n"
      + "This sends the name of a program running on this machine over the network to a search engine.",
      "Process Manager",
      MessageBoxButtons.YesNo
    );

    if (answer != DialogResult.Yes)
      return;

    if (DesktopOpen.Search(row.Name) is not { } request) {
      MessageBox.Show("This platform has no desktop opener to hand the page to.", "Process Manager");
      return;
    }

    var result = this._actions.Launch(request);
    if (!result.Outcome.Succeeded)
      MessageBox.Show(result.Outcome.Detail ?? result.Outcome.Outcome.ToString(), "Process Manager");
  }

  /// <summary>
  /// "Which process is using this?" — and then goes there (PRD §33).
  /// </summary>
  private void FindResource() {
    var dialog = new FindResourceDialog(this._probe);
    dialog.ShowDialog();
    this.SelectPid(dialog.ChosenPid);
  }

  /// <summary>
  /// Points at a window and selects the process behind it (PRD §39).
  /// </summary>
  /// <remarks>
  /// A window whose owner cannot be found in the list is worth saying so about rather than silently
  /// doing nothing: on a Wayland session the answer is usually that the window is not an X11 one,
  /// and a program that just shrugs leaves somebody clicking the menu item again.
  /// </remarks>
  private void PickWindow() {
    var picker = new WindowPickerDialog(this._probe);
    picker.ShowDialog();
    if (this.SelectPid(picker.ChosenPid))
      return;

    var windows = this._probe.GetWindows();
    MessageBox.Show(
      picker.Picked is null
        ? $"No window could be identified under the pointer. {windows.Explain()}".TrimEnd()
        : $"The window belongs to pid {picker.Picked.Value.Pid}, which is not in the list.",
      "Process Manager"
    );
  }

  /// <summary>Selects a process by pid, if it is in the list.</summary>
  /// <returns>Whether it was found.</returns>
  private bool SelectPid(int pid) {
    if (pid < 0)
      return false;

    foreach (var process in this._sampler.Current.Processes) {
      if (process.Key.Pid != pid)
        continue;

      if (this._binder.RowFor(process.Key) is null)
        continue;

      // Expanding every ancestor first: a process nested under a collapsed parent cannot be
      // selected into view, and the whole point of finding it was to look at it.
      this._tree.SelectedNode = this.NodeFor(process.Key);
      this.UpdateDetails();
      return this._tree.SelectedNode is not null;
    }

    return false;
  }

  private TreeNode? NodeFor(ProcessKey key) {
    var node = this._binder.NodeFor(key);
    for (var parent = node?.Parent; parent is not null; parent = parent.Parent)
      parent.Expand();

    return node;
  }

  /// <summary>Which cores the selected process may run on.</summary>
  private void ChooseAffinity() {
    if (this._binder.SelectedRow is not { } row)
      return;

    var cores = this._cores.Topology.Cores.Count > 0
      ? this._cores.Topology.Cores.Count
      : Math.Max(1, this._sampler.Delta.PerCoreCount);

    var chooser = new AffinityChooser(row.Name, row.Pid, cores, this._cores.Topology);
    chooser.ShowDialog();
    if (!chooser.Accepted)
      return;

    this.Act("set affinity", key => this._actions!.SetAffinity(key, chooser.Mask));
  }

  private static ToolStripMenuItem Item(string text, Action action) {
    var item = new ToolStripMenuItem(text);
    item.Click += (_, _) => action();
    return item;
  }

  /// <summary>
  /// A menu item with a chord, which is how anything here is reachable without the mouse.
  /// </summary>
  /// <remarks>
  /// The form dispatches the chord through the menu strip, so it works wherever the focus happens
  /// to be — including in the filter box, which is the one place a bare letter would be typed
  /// rather than obeyed.
  /// </remarks>
  private static ToolStripMenuItem Shortcut(string text, Keys keys, Action action) {
    var item = Item(text, action);
    item.ShortcutKeys = keys;
    return item;
  }

  private void BuildStatus() {
    this._status.Dock = DockStyle.Bottom;
    this._status.Height = 22;
    this.Controls.Add(this._status);
  }

  #region the shell (PRD §10)

  /// <summary>
  /// The rail down the left, with the views that are always there (PRD §10).
  /// </summary>
  /// <remarks>
  /// Persistent, and that is the requirement rather than a style: every one of these was already
  /// collected and already printable from the command line, and §9's complaint was that there was no
  /// way to <em>get</em> to any of it. A menu item somebody has to remember exists is not navigation.
  /// </remarks>
  private void BuildRail() {
    this._rail.Dock = DockStyle.Left;
    // Wide enough for the longest caption. The rail collapses to icons on its own hamburger, and a
    // rail whose captions are cut off reads as one that is already collapsed.
    this._rail.Width = 168;
    this._rail.SelectedIndexChanged += (_, _) => this.ShowView(this._rail.SelectedIndex);
    this.Controls.Add(this._rail);
  }

  /// <summary>
  /// Builds the views and puts their names in the rail.
  /// </summary>
  /// <remarks>
  /// The order is the order §9 lists them in. Processes is first and is what the window opens on,
  /// because it is what the program is for.
  /// </remarks>
  private void BuildViews() {
    this.AddView("Processes", this._split, () => { }, () => $"{this._view.MatchCount} rows", () => this._view.MatchCount);
    // A window rather than a page: the performance view is modeless, has its own timer and its own
    // lifetime, and embedding a second copy of it here would mean two of everything it samples.
    this.AddView("Performance", null, this.ShowPerformance, () => "opens the performance window", () => 0);
    this.AddView("Startup", this._shell.StartupControl, this._shell.RefreshStartup, () => this._shell.StartupText, () => this._shell.StartupRows);
    this.AddView("Users", this._shell.SessionsControl, this._shell.RefreshSessions, () => this._shell.SessionsText, () => this._shell.SessionsRows);
    this.AddView("Services", this._shell.ServicesControl, this._shell.RefreshServices, () => this._shell.ServicesText, () => this._shell.ServicesRows);
    this.AddView("Network", this._shell.NetworkControl, this.RefreshNetwork, () => this._shell.NetworkText, () => this._shell.NetworkRows);
    this.AddView("Find resources", null, this.FindResource, () => "opens the find dialog", () => 0);

    // Opening a socket row goes to the process holding it, which is the question a connection list
    // is usually being read to answer (PRD §33, §40).
    this._shell.NetworkRowOpened += (_, _) => this.GoToSocketOwner();

    this.ShowView(0);
  }

  /// <summary>The rail's entries, top to bottom (PRD §9).</summary>
  public IReadOnlyList<string> ViewTitles {
    get {
      var titles = new List<string>(this._views.Count);
      foreach (var view in this._views)
        titles.Add(view.Title);

      return titles;
    }
  }

  /// <summary>Which view is in the content region.</summary>
  public string ShownView => this._shown?.Title ?? string.Empty;

  /// <summary>Chooses a view by name, the way clicking the rail does. False for a name it has not got.</summary>
  public bool ShowView(string title) {
    ArgumentNullException.ThrowIfNull(title);

    for (var i = 0; i < this._views.Count; ++i) {
      if (!string.Equals(this._views[i].Title, title, StringComparison.OrdinalIgnoreCase))
        continue;

      // Through the rail, so one gesture does one thing: assigning the index raises the event that
      // swaps the view. Calling both would open a window-opening entry's window twice.
      if (this._rail.SelectedIndex == i)
        // Except when it is already there — the rail raises nothing then, and a caller asking for
        // the view that is showing still means "collect it again".
        this.ShowView(i);
      else
        this._rail.SelectedIndex = i;

      return true;
    }

    return false;
  }

  /// <summary>
  /// What a view holds, in text — how a test with no display, and the capture log, read one
  /// (PRD §9.6).
  /// </summary>
  public string DescribeView(string title) {
    foreach (var view in this._views)
      if (string.Equals(view.Title, title, StringComparison.OrdinalIgnoreCase))
        return view.Describe();

    return string.Empty;
  }

  private void AddView(string title, Control? content, Action show, Func<string> describe, Func<int> rows) {
    this._views.Add(new(title, content, show, describe, rows));
    this._rail.AddItem(title);
  }

  /// <summary>
  /// Puts one view in the content region.
  /// </summary>
  /// <remarks>
  /// Swapped in and out rather than hidden: the toolkit's docking pass does not skip an invisible
  /// child, so a hidden view would go on reserving the whole content region and the visible one
  /// would be laid out into nothing. Removing a control returns it to its unrealized shape and
  /// adding it back realizes it again, which is exactly what is wanted.
  /// </remarks>
  private void ShowView(int index) {
    if ((uint)index >= (uint)this._views.Count)
      return;

    var view = this._views[index];
    if (view.Content is null) {
      // An entry that opens a window of its own leaves the content region — and the rail's
      // selection — where they were, so the rail never claims to be showing something it is not.
      //
      // Put back first, and then opened. The other way round the rail stays on the entry for as
      // long as the window it opened is modal, and stays there for good if opening it throws.
      if (this._shown is { } current)
        this._rail.SelectedIndex = this._views.IndexOf(current);

      view.Show();
      return;
    }

    if (ReferenceEquals(this._shown, view)) {
      view.Show();
      return;
    }

    if (this._shown?.Content is { } previous)
      this._content.Controls.Remove(previous);

    this._shown = view;
    view.Content.Dock = DockStyle.Fill;
    this._content.Controls.Add(view.Content);
    view.Show();
    this.UpdateCommandBar();
  }

  /// <summary>
  /// The strip of actions above the content (PRD §10).
  /// </summary>
  /// <remarks>
  /// Context-sensitive is the whole requirement: a command bar that offers "End task" while a list
  /// of services is showing is a menu with a different shape. What is enabled here follows the view
  /// and the selection, and what a view cannot do is disabled rather than silently doing nothing —
  /// which is the failure mode this program has already shipped once (PRD §26).
  /// </remarks>
  private void BuildCommandBar() {
    this._commands.Dock = DockStyle.Top;
    // A ToolStrip has no intrinsic height, the same way a MenuStrip has none: docked without one it
    // is present, mapped and nought pixels tall, which photographs exactly like a strip nobody added.
    this._commands.Height = 30;

    this._commands.Items.Add(Command("Properties", this.ShowProperties));
    this._commands.Items.Add(Command("End task", this.EndTask));
    this._commands.Items.Add(new ToolStripSeparator());
    this._lowerPaneButton = Command("Lower pane", this.ToggleLowerPane);
    this._lowerPaneButton.CheckOnClick = false;
    this._commands.Items.Add(this._lowerPaneButton);
    this._commands.Items.Add(Command("Columns…", this.ChooseColumns));
    this._commands.Items.Add(new ToolStripSeparator());
    this._commands.Items.Add(Command("Refresh", this.RefreshCurrentView));
    this.Controls.Add(this._commands);
  }

  private static ToolStripButton Command(string text, Action action) {
    var button = new ToolStripButton(text);
    button.Click += (_, _) => action();
    return button;
  }

  /// <summary>
  /// Enables what the view showing can actually do.
  /// </summary>
  /// <remarks>
  /// Only the process view has processes, so only it has the verbs that act on one. The rest keep
  /// Refresh, which is the one thing every view here has — none of them follows the sample tick.
  /// </remarks>
  private void UpdateCommandBar() {
    var processes = this._shown is null || ReferenceEquals(this._shown.Content, this._split);
    foreach (var item in this._commands.Items)
      if (item is ToolStripButton button && button.Text != "Refresh")
        button.Enabled = processes;

    if (this._lowerPaneButton is { } lower)
      lower.Text = this.LowerPaneVisible ? "Hide lower pane" : "Show lower pane";
  }

  /// <summary>Collects the showing view's rows again, which is the only way any of them updates.</summary>
  private void RefreshCurrentView() {
    if (this._shown is { } view)
      view.Show();

    this.Refresh();
  }

  private void RefreshNetwork() {
    var names = new Dictionary<int, string>();
    foreach (var process in this._sampler.Current.Processes)
      names[process.Pid] = process.Name;

    this._shell.RefreshNetwork(names);
  }

  private void GoToSocketOwner() {
    var pid = this._shell.SelectedNetworkPid;
    if (pid <= 0) {
      MessageBox.Show(
        "This socket's owning process is not visible from this account. Sockets held by other users' "
        + "processes cannot be attributed without privilege.",
        "Process Manager"
      );

      return;
    }

    this._rail.SelectedIndex = 0;
    if (!this.SelectPid(pid))
      MessageBox.Show($"The socket belongs to pid {pid}, which is not in the process list.", "Process Manager");
  }

  #endregion

  #region the lower pane (PRD §10)

  /// <summary>Whether the detail pane at the foot of the process view is showing.</summary>
  public bool LowerPaneVisible {
    get => !this._split.Panel2Collapsed;
    set {
      this._split.Panel2Collapsed = !value;
      if (this._lowerPaneItem is { } item)
        item.Checked = value;

      this.UpdateCommandBar();
    }
  }

  /// <summary>
  /// Shows or hides the lower pane — the defining Process Explorer interaction (PRD §10).
  /// </summary>
  /// <remarks>
  /// Reachable three ways, and that is the requirement rather than belt and braces: from the menu
  /// for somebody looking for it, from the command bar for somebody who works from the strip, and
  /// from Ctrl+D for somebody who has stopped looking at either. Collapsed rather than removed, so
  /// the splitter comes back where it was left.
  /// </remarks>
  private void ToggleLowerPane() => this.LowerPaneVisible = !this.LowerPaneVisible;

  #endregion

  private void BuildMenu() {
    // Docked *and* given a height. A MenuStrip has no intrinsic one — the toolkit's own demo assigns
    // its bounds by hand — so docking it Top without a height produces a menu that is present,
    // mapped and nought pixels tall, which photographs exactly like a menu that was never added.
    var menu = new MenuStrip { Dock = DockStyle.Top, Height = 26 };

    var view = new ToolStripMenuItem("View");
    view.DropDownItems.Add(this.BuildGroupingMenu());

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

    // The header carries the click now — see the gestures above — so this menu is what makes the
    // same thing reachable from the keyboard, which is a requirement rather than a convenience
    // (PRD §11 "keyboard sort", §74).
    var sort = new ToolStripMenuItem("Sort by");
    sort.DropDownItems.Add(Shortcut("Sort by the next column", Keys.F6, () => this.StepSort(1)));
    sort.DropDownItems.Add(Shortcut("Sort by the previous column", Keys.Shift | Keys.F6, () => this.StepSort(-1)));
    sort.DropDownItems.Add(Shortcut("Reverse", Keys.F7, () => {
      this._view.SortDescending = !this._view.SortDescending;
      this.RebuildColumns();
      this.Refresh();
    }));

    sort.DropDownItems.Add(new ToolStripSeparator());
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
    sort.DropDownItems.Add(this.BuildTieBreakerMenu());
    sort.DropDownItems.Add(Item("Sort by one column only", () => {
      this._view.ClearSecondarySort();
      this.RebuildColumns();
      this.Rebind();
      this._status.Text = "ties are broken by the pid again";
    }));

    menu.Items.Add(sort);
    menu.Items.Add(this.BuildColumnMenu());
    menu.Items.Add(this.BuildEditMenu());

    view.DropDownItems.Add(new ToolStripSeparator());

    // The one interaction §10 calls the highest-value single item in the document, and the third of
    // its three ways in. The chord is dispatched by the form through the menu strip, so it works
    // wherever the focus happens to be.
    this._lowerPaneItem = new("Lower pane") { ShortcutKeys = Keys.Control | Keys.D, Checked = true };
    this._lowerPaneItem.Click += (_, _) => this.ToggleLowerPane();
    view.DropDownItems.Add(this._lowerPaneItem);

    view.DropDownItems.Add(Shortcut("Filter…", Keys.F3, this.FocusFilter));
    view.DropDownItems.Add(Item("Select columns…", this.ChooseColumns));
    view.DropDownItems.Add(Item("Performance…", this.ShowPerformance));
    view.DropDownItems.Add(Item("Colour legend…", this.ShowLegend));
    view.DropDownItems.Add(Item("Highlighting thresholds…", this.EditThresholds));
    view.DropDownItems.Add(Item("Find handles or files…", this.FindResource));
    view.DropDownItems.Add(Item("Find window…", this.PickWindow));

    var process = new ToolStripMenuItem("Process");
    process.DropDownItems.Add(Item("End task", this.EndTask));
    process.DropDownItems.Add(Item("End process", () => this.Act("end", key => this._actions!.Terminate(key), _EndsWithoutAsking)));
    process.DropDownItems.Add(Item("End process tree", this.EndTree));
    process.DropDownItems.Add(Item("End the ticked processes", this.EndTicked));
    process.DropDownItems.Add(Item("Restart", this.RestartProcess));
    process.DropDownItems.Add(Item("Suspend", () => this.Act("suspend", key => this._actions!.Suspend(key))));
    process.DropDownItems.Add(Item("Resume", () => this.Act("resume", key => this._actions!.Resume(key))));
    process.DropDownItems.Add(Item("Send signal…", this.SendSignal));
    process.DropDownItems.Add(this.FreezerMenu());
    process.DropDownItems.Add(new ToolStripSeparator());
    process.DropDownItems.Add(Item("Limits…", this.ShowLimits));
    process.DropDownItems.Add(Item("Read handle count", this.FillHandleCounts));
    process.DropDownItems.Add(Item("Properties…", this.ShowProperties));
    process.DropDownItems.Add(Item("Refresh details", () => this._details.Invalidate()));
    menu.Items.Add(process);

    this.Controls.Add(menu);
  }

  #endregion

  #region refresh

  /// <summary>
  /// Redraws the list from the sample already taken (PRD §11).
  /// </summary>
  /// <remarks>
  /// What a keystroke gets. <see cref="Refresh"/> takes a sample, and a filter box that sampled the
  /// machine on every character would read a thousand /proc files per letter typed — and make every
  /// rate on screen jump while somebody was still deciding what to search for.
  /// </remarks>
  private void Rebind() {
    var snapshot = this._sampler.Current;
    var delta = this._sampler.Delta;
    this._view.Rebuild(snapshot, delta);
    this._binder.Sync(snapshot, delta, this._view);
    this.StretchLastColumn();
    this.UpdateStatus(snapshot, delta);
    this._tree.Invalidate();
  }

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
    if (this._performance is { } performance) {
      // The interval is a setting, and the page's time axis is drawn from it: a graph labelled sixty
      // seconds on a machine sampled every four is wrong by a factor of four and looks fine.
      performance.SecondsPerSample = this.Interval / 1000d;
      performance.UpdateFromSample();
    }
    foreach (var window in this._properties) {
      // The interval, for the same reason the performance page is told it: a graph labelled sixty
      // seconds on a machine sampled every four is wrong by a factor of four and looks fine.
      window.SecondsPerSample = this.Interval / 1000d;
      window.UpdateFromSample(snapshot, delta, this._binder.RowFor(window.Key), this._binder.HandleCountOf(window.Key));
    }

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
      this.LayOutFilterBar();
    }

    this._shell.Stretch();

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
      // MatchCount, not RowCount: a grouping heading takes a row and is not a process (PRD §83).
      $"{this._view.MatchCount} of {snapshot.ProcessCount} processes{this.TickedSuffix}  ·  "
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

  /// <summary>
  /// What every colour in the list means, and a way to change the two that are settable (PRD §23).
  /// </summary>
  /// <remarks>
  /// The legend is told the bands rather than reading them from a static, so the sentences beside
  /// the warm and hot swatches are the numbers this window is judging by this second. A change made
  /// from inside it comes back through the event and is applied here, because the rows have to be
  /// re-classified before anything looks different.
  /// </remarks>
  private void ShowLegend() {
    var legend = new LegendWindow(ProcessRow.Thresholds);
    legend.ThresholdsChanged += (_, thresholds) => this.ApplyThresholds(thresholds);
    legend.ShowDialog();
  }

  private void EditThresholds() {
    var dialog = new HighlightThresholdsDialog(ProcessRow.Thresholds);
    dialog.ShowDialog();
    if (dialog.Accepted)
      this.ApplyThresholds(dialog.Thresholds);
  }

  /// <summary>
  /// Takes a new set of bands and re-judges every row against them.
  /// </summary>
  /// <remarks>
  /// The refresh is the point. The heat of a cell is worked out once per sample and cached beside
  /// its text, so a threshold changed without one leaves the table marked by the old numbers until
  /// something else happens to redraw it — which looks exactly like a dialog that did nothing.
  /// </remarks>
  private void ApplyThresholds(UsageThresholds thresholds) {
    ProcessRow.Thresholds = thresholds;
    this.Refresh();
  }

  private void ChooseColumns() {
    var chooser = new ColumnChooser(this._columns.Fields);
    chooser.ShowDialog();
    if (!chooser.Accepted)
      return;

    this._columns.Apply(chooser.Selection);
    this.RebuildColumns();
    this.Rebind();
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

  /// <summary>
  /// What a destructive action costs, said in the confirmation rather than left to be discovered
  /// (PRD §5.5, §90).
  /// </summary>
  /// <remarks>
  /// The sentence exists because "Are you sure?" is not a question anybody can answer. A prompt has
  /// to name the action, the target, its pid and what may be lost — and the difference between these
  /// and "end task" is precisely that these do not ask the program first.
  /// </remarks>
  private const string _EndsWithoutAsking
    = "This stops it immediately without asking it to save anything, and unsaved work in it will be lost.";

  private const string _RestartEndsWithoutAsking
    = "It is asked to stop and then started again with the same arguments in the same directory. "
    + "Unsaved work in it may be lost, and it will be a different process with a different pid.";

  private void Act(string what, Func<ProcessKey, ActionResult> action, string? consequence = null) {
    if (this._binder.SelectedRow is not { } row)
      return;

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    // Confirmed before it happens, and the target named unambiguously — a pid on its own is not a
    // name, and the row under the pointer may have moved (PRD §6.4).
    var question = $"{char.ToUpper(what[0], CultureInfo.CurrentCulture)}{what[1..]} {row.Name} (PID {row.Pid})?";
    var answer = MessageBox.Show(
      consequence is null ? question : $"{question}\n\n{consequence}",
      "Process Manager",
      MessageBoxButtons.YesNo
    );

    if (answer != DialogResult.Yes)
      return;

    this.Report(action(row.Key));
    this.Refresh();
  }

  /// <summary>
  /// Puts an action's answer in front of somebody — including the ones that succeeded with something
  /// to say.
  /// </summary>
  /// <remarks>
  /// Most successes are silent, because the list redrawing is the answer. The exceptions carry a
  /// detail, and it is always something the outcome alone does not convey: "its window was asked to
  /// close" and "it has no window, so SIGTERM was sent" are both successes and are not the same
  /// thing to have happened (PRD §72.3).
  /// </remarks>
  private void Report(ActionResult result) {
    if (!result.Succeeded)
      MessageBox.Show(result.Detail ?? result.Outcome.ToString(), "Process Manager");
    else if (result.Detail is { Length: > 0 } detail)
      MessageBox.Show(detail, "Process Manager");
  }

  /// <summary>
  /// Asks the program to close rather than telling it to (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// Deliberately at the top of the menu and deliberately not confirmed: it is the reversible one.
  /// The program is asked, may put up its own "save your changes?" and may decline — which is the
  /// whole reason it sits beside "End process" instead of replacing it.
  /// </remarks>
  private void EndTask() {
    if (this._binder.SelectedRow is not { } row || this._actions is null)
      return;

    this.Report(this._actions.EndTask(row.Key));
    this.Refresh();
  }

  /// <summary>
  /// Ends a process and everything under it (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// The confirmation counts the descendants and says so, because "and everything under it" is the
  /// part somebody needs to weigh and the part a plain "Are you sure?" hides. A shell with a build
  /// under it and a shell on its own are the same row and very different requests (PRD §90).
  /// </remarks>
  private void EndTree() {
    if (this._binder.SelectedRow is not { } row)
      return;

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    // Deepest first — see Query.ProcessTree.DescendantsFirst for why the order is not incidental.
    var order = ProcessTree.DescendantsFirst(this._sampler.Current, row.Pid);
    if (order.Count == 0) {
      MessageBox.Show($"{row.Name} (PID {row.Pid}) is no longer in the list.", "Process Manager");
      return;
    }

    var descendants = order.Count - 1;
    var question = descendants == 0
      ? $"End {row.Name} (PID {row.Pid})? Nothing is running under it."
      : $"End {row.Name} (PID {row.Pid}) and the {descendants} process{(descendants == 1 ? string.Empty : "es")} running under it?";

    if (MessageBox.Show($"{question}\n\n{_EndsWithoutAsking}", "Process Manager", MessageBoxButtons.YesNo) != DialogResult.Yes)
      return;

    this.Report(this._actions.TerminateTree(order));
    this.Refresh();
  }

  private void RestartProcess() => this.Act(
    "restart",
    key => {
      var result = this._actions!.Restart(key);
      return result.Outcome.Succeeded
        ? new(ActionOutcome.Succeeded, $"started again as pid {result.Pid}")
        : result.Outcome;
    },
    _RestartEndsWithoutAsking
  );


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

    var window = new ProcessPropertiesWindow(
      this._probe,
      row.Key,
      row.Name,
      this._actions,
      this._settings.HideUnavailableTabs ? UnavailableTabs.Hidden : UnavailableTabs.Disabled
    ) { SecondsPerSample = this.Interval / 1000d };

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

  /// <summary>
  /// Opens the selected process's properties window and returns it, so a capture run can photograph
  /// it too (PRD §9.6, §26).
  /// </summary>
  /// <remarks>
  /// §26 is almost entirely about pages, and the two new ones are laid out by arithmetic — the kind
  /// that renders as an empty rectangle while every test around it passes.
  /// </remarks>
  public ProcessPropertiesWindow? OpenProperties() {
    this.ShowProperties();
    // Fed one sample immediately, because a properties window opened and photographed in the same
    // callback would otherwise be a set of empty lists and six empty graphs.
    if (this._properties.Count == 0)
      return null;

    var window = this._properties[^1];
    window.UpdateFromSample(
      this._sampler.Current,
      this._sampler.Delta,
      this._binder.RowFor(window.Key),
      this._binder.HandleCountOf(window.Key)
    );

    return window;
  }

  /// <summary>
  /// Puts the list into a grouping, for the capture run (PRD §9.6, §83).
  /// </summary>
  /// <remarks>
  /// A grouping is the one thing about this table that a photograph of the default view cannot show,
  /// and a heading row that is drawn wrongly — the wrong colour, the wrong height, selectable when it
  /// should not be — passes every test in the suite. So the capture takes a second picture with the
  /// rows grouped, and this is what it asks for.
  /// </remarks>
  public string ShowGrouping(ProcessGrouping grouping) {
    this.GroupBy(grouping);
    // Selected again, because the previous selection was a process the regrouping moved: the pane
    // under the table would otherwise photograph as whatever it happened to keep.
    this.SelectFirstRow();
    // And scrolled back to the very top afterwards. Selecting the first *process* scrolls it into
    // view, which in a grouped list pushes the heading above it off the top — so the picture came
    // out with its first row apparently belonging to no group at all.
    this._tree.TopIndex = 0;
    return this.DescribeForCapture();
  }

  /// <summary>
  /// Types into the filter box, for the capture run (PRD §9.6, §11).
  /// </summary>
  /// <remarks>
  /// Match highlighting is measured text drawn behind other text. It is the exact shape of defect
  /// this project keeps shipping — right in the tests, one column out on screen — so the capture
  /// photographs it rather than trusting it.
  /// </remarks>
  public string ShowFilter(string text) {
    ArgumentNullException.ThrowIfNull(text);
    this._filterBox.Text = text;
    // The box raises TextChanged from the peer, which does not exist until the window is realized;
    // asking directly means the capture photographs the filter it set rather than the one before it.
    this.ApplyFilter();
    this.SelectFirstRow();
    this._tree.TopIndex = 0;
    return this.DescribeForCapture();
  }

  /// <summary>
  /// Puts the columns through the moves §11 asks for, and says what each did (PRD §9.6).
  /// </summary>
  /// <remarks>
  /// Text and no picture: a width is a number, and the number is better evidence than a photograph
  /// of it. What a photograph is needed for is that the header and the cells agree afterwards, which
  /// the grouped and filtered captures either side of this already show.
  /// </remarks>
  public string ExerciseColumns() {
    // What was there before, put back at the end. The capture runs against the real settings file
    // and the window writes it once a second, so a run that left the columns reset would quietly
    // throw away the layout of whoever regenerated the screenshots.
    var opened = this._columns.Fields;
    var openedWidths = this._columns.ChosenWidths;

    var builder = new System.Text.StringBuilder();
    builder.AppendLine($"columns opened:  {this.DescribeColumns()}");
    this.AutoSize(everyColumn: true);
    builder.AppendLine($"columns fitted:  {this.DescribeColumns()}");
    this._columns.SetCurrent(1);
    this.MoveColumn(-1);
    builder.AppendLine($"columns moved:   {this.DescribeColumns()}");
    this.ResizeColumn(_ResizeStep * 4);
    builder.AppendLine($"columns resized: {this.DescribeColumns()}");
    this.ResetColumns();
    builder.AppendLine($"columns reset:   {this.DescribeColumns()}");

    this._columns.Apply(opened);
    foreach (var (field, width) in openedWidths)
      this._columns.Restore(field, width);

    this._columns.SetCurrent(0);
    this.RebuildColumns();
    this.Rebind();
    builder.AppendLine($"columns put back:{this.DescribeColumns()}");
    return builder.ToString();
  }

  /// <summary>
  /// What each of the rail's views holds, counted rather than quoted (PRD §9.6).
  /// </summary>
  /// <remarks>
  /// Counts and no contents, deliberately. The empty-view detector is the number; the rows
  /// themselves are this machine's services, its logins and its open sockets, and none of that
  /// belongs in a log that goes into a public repository — which is the same call the capture
  /// script's private pid namespace makes about the process list.
  /// </remarks>
  public string DescribeShellForCapture() {
    var builder = new System.Text.StringBuilder();
    var opened = this.ShownView;
    foreach (var view in this._views) {
      if (view.Content is null) {
        builder.AppendLine($"  {view.Title,-16} (opens a window of its own)");
        continue;
      }

      this.ShowView(view.Title);
      // The heading is the first line and says how many of what came back, including which of the
      // two things "none" means. Everything after it is the machine's own business.
      var heading = view.Describe().Split('\n')[0];
      builder.AppendLine($"  {view.Title,-16} {view.Rows()} row(s) — {heading}");
    }

    this.ShowView(opened);
    return builder.ToString();
  }

  #region the filter box (PRD §11, §56)

  private readonly Panel _filterBar = new();
  private readonly Label _filterLabel = new();
  private readonly TextBox _filterBox = new();
  private readonly CheckBox _matchCase = new();
  private readonly Label _filterNote = new();
  private string? _highlight;

  /// <summary>
  /// The strip above the table: the query, whether it minds case, and what it made of what was typed.
  /// </summary>
  /// <remarks>
  /// The window had no filter at all — the query language of §56 was reachable from the terminal and
  /// from <c>--filter</c>, and from nowhere in the GUI — which is also why §11's case-sensitivity and
  /// match-highlighting rows both read "terminal only". A box is the smallest thing that fixes all
  /// three.
  /// <para>
  /// What the parser said goes beside it rather than in a dialog. A query that will not parse is
  /// the ordinary state of a query somebody is halfway through typing, and the list keeps working
  /// meanwhile: an unparsable query falls back to a plain substring search, exactly as it does in
  /// the terminal.
  /// </para>
  /// </remarks>
  private void BuildFilterBar() {
    this._filterBar.Dock = DockStyle.Top;
    this._filterBar.Height = 30;

    this._filterLabel.Text = "Filter";
    this._filterBox.PlaceholderText = "name, or cpu>50 AND user:root — F3 to come back here";
    this._filterBox.TextChanged += (_, _) => this.ApplyFilter();

    this._matchCase.Text = "Match case";
    this._matchCase.CheckedChanged += (_, _) => {
      this._view.CaseSensitive = this._matchCase.Checked;
      this.ApplyFilter();
    };

    this._filterBar.Controls.Add(this._filterLabel);
    this._filterBar.Controls.Add(this._filterBox);
    this._filterBar.Controls.Add(this._matchCase);
    this._filterBar.Controls.Add(this._filterNote);
    this.Controls.Add(this._filterBar);
    this.LayOutFilterBar();
  }

  private void LayOutFilterBar() {
    const int Gap = 6;
    var width = Math.Max(360, this._filterBar.Width);
    // Right to left, because the two on the right have widths of their own and the box takes what is
    // left: a box sized from the left would run under the check box on a narrow window.
    var noteWidth = Math.Min(320, Math.Max(80, width / 3));
    var caseWidth = 110;
    this._filterLabel.Bounds = new(Gap, 6, 44, 20);
    var boxLeft = Gap + 48;
    var boxWidth = Math.Max(120, width - boxLeft - caseWidth - noteWidth - (Gap * 3));
    this._filterBox.Bounds = new(boxLeft, 4, boxWidth, 22);
    this._matchCase.Bounds = new(boxLeft + boxWidth + Gap, 5, caseWidth, 20);
    this._filterNote.Bounds = new(boxLeft + boxWidth + Gap + caseWidth + Gap, 6, noteWidth, 20);
  }

  /// <summary>
  /// Takes what was typed, and says what became of it.
  /// </summary>
  /// <remarks>
  /// The highlight is the typed text only while it is a plain word. Once it contains an operator it
  /// is a query rather than a string, and picking the letters "cpu&gt;50" out of a command line
  /// would mark something nobody searched for — which is the rule the terminal already follows.
  /// </remarks>
  private void ApplyFilter() {
    var text = this._filterBox.Text;
    this._view.TextFilter = string.IsNullOrEmpty(text) ? null : text;
    this._highlight = text.Length > 0 && text.AsSpan().IndexOfAny(":<>=/") < 0 ? text : null;

    this._filterNote.Text = text.Length == 0
      ? string.Empty
      : ProcessQuery.TryParse(text, out _, out var error, this._view.CaseSensitive) || error is null
        ? string.Empty
        : error;

    this.Rebind();
  }

  /// <summary>Puts the caret in the filter box, which is what F3 and Ctrl+F are for (PRD §56).</summary>
  private void FocusFilter() => this._filterBox.Focus();

  #endregion

  #region columns, copying and exporting (PRD §11)

  /// <summary>
  /// The column menu: everything §11 asks a table's header to be able to do.
  /// </summary>
  /// <remarks>
  /// A menu as well as the header gestures, and not instead of them. The gestures are what a hand
  /// reaches for; the menu is what makes every one of them reachable from the keyboard, which §11's
  /// "keyboard sort" row and §74 both require — and it is the only place the current column has a
  /// name, so it is also where somebody finds out which column the keyboard is on.
  /// </remarks>
  private ToolStripMenuItem BuildColumnMenu() {
    var menu = new ToolStripMenuItem("Columns");
    menu.DropDownItems.Add(Item("Select columns…", this.ChooseColumns));
    menu.DropDownItems.Add(new ToolStripSeparator());
    menu.DropDownItems.Add(Shortcut("Previous column", Keys.Control | Keys.Left, () => this.StepColumn(-1)));
    menu.DropDownItems.Add(Shortcut("Next column", Keys.Control | Keys.Right, () => this.StepColumn(1)));
    menu.DropDownItems.Add(new ToolStripSeparator());
    menu.DropDownItems.Add(Shortcut("Move left", Keys.Control | Keys.Shift | Keys.Left, () => this.MoveColumn(-1)));
    menu.DropDownItems.Add(Shortcut("Move right", Keys.Control | Keys.Shift | Keys.Right, () => this.MoveColumn(1)));
    menu.DropDownItems.Add(Shortcut("Narrower", Keys.Control | Keys.OemMinus, () => this.ResizeColumn(-_ResizeStep)));
    menu.DropDownItems.Add(Shortcut("Wider", Keys.Control | Keys.Oemplus, () => this.ResizeColumn(_ResizeStep)));
    menu.DropDownItems.Add(new ToolStripSeparator());
    menu.DropDownItems.Add(Shortcut("Fit this column", Keys.Control | Keys.D1, () => this.AutoSize(false)));
    menu.DropDownItems.Add(Shortcut("Fit every column", Keys.Control | Keys.D2, () => this.AutoSize(true)));
    menu.DropDownItems.Add(Shortcut("Reset columns", Keys.Control | Keys.D0, this.ResetColumns));
    return menu;
  }

  /// <summary>How much one keypress moves a column boundary. A pixel a press would be useless.</summary>
  private const int _ResizeStep = 12;

  private void StepColumn(int delta) {
    this._columns.MoveCurrent(delta);
    this.SayCurrentColumn();
  }

  /// <summary>
  /// Names the column the keyboard is on.
  /// </summary>
  /// <remarks>
  /// The header cannot show it — the toolkit draws the header itself and offers no per-column state
  /// to draw with — so the status bar says it instead. Without this, "wider" and "copy cell" both
  /// act on a column nobody can see the identity of, which is indistinguishable from acting on the
  /// wrong one.
  /// </remarks>
  private void SayCurrentColumn() {
    if (this._columns.Count == 0)
      return;

    this._status.Text = $"column: {ColumnSet.Info(this._columns.CurrentField).Header}"
      + $"  ({this._columns.WidthAt(this._columns.Current)} px)";
  }

  private void MoveColumn(int delta) {
    if (!this._columns.Reorder(delta))
      return;

    this.RebuildColumns();
    this.Rebind();
    this.SayCurrentColumn();
  }

  private void ResizeColumn(int delta) {
    this._columns.ResizeCurrent(delta);
    this.ApplyWidths();
    this.SayCurrentColumn();
  }

  private void ResetColumns() {
    this._columns.Reset(ColumnSet.Default);
    this.RebuildColumns();
    this.Rebind();
    this._status.Text = "columns are back to the defaults";
  }

  /// <summary>
  /// Fits one column, or all of them, to the rows on screen (PRD §11).
  /// </summary>
  /// <remarks>
  /// Measured against what is showing rather than against every process, which is what auto-sizing
  /// means here and in the terminal: reading a thousand command lines to widen a column nobody is
  /// looking at costs a frame, and a column fitted to the widest value in the whole table is usually
  /// fitted to something scrolled far out of sight.
  /// </remarks>
  private void AutoSize(bool everyColumn) {
    if (MeasureText.Instance is not { } measure) {
      this._status.Text = "there is no display to measure text against";
      return;
    }

    var first = everyColumn ? 0 : this._columns.Current;
    var last = everyColumn ? this._columns.Count - 1 : this._columns.Current;
    for (var i = first; i <= last; ++i)
      this._columns.AutoSize(i, this.MeasureColumn(measure, i));

    this.RebuildColumns();
    this._status.Text = everyColumn
      ? "every column fits what is on screen"
      : $"{ColumnSet.Info(this._columns.CurrentField).Header} fits what is on screen";
  }

  private int MeasureColumn(MeasureText measure, int index) {
    var field = this._columns.FieldAt(index);
    // The header too: a column narrower than its own caption is a column whose caption is an
    // ellipsis, which names nothing.
    var widest = measure.WidthOf(ColumnSet.Info(field).Header + " ▾");
    var visible = this._tree.VisibleNodeCount;
    for (var row = this._tree.TopIndex; row < visible; ++row) {
      if (this._tree.NodeAt(row) is not { } node)
        break;

      var text = node.Tag switch {
        ProcessRow process => index == 0 ? process.Label : process.TextOf(field),
        GroupRow group when index == 0 => group.Text,
        _ => string.Empty,
      };

      if (text.Length == 0)
        continue;

      // The tree column carries the indentation and the expander, which are as much a part of the
      // width it needs as the text is.
      var indent = index == 0 ? (node.Level + 2) * this._tree.ItemHeight : 0;
      widest = Math.Max(widest, measure.WidthOf(text) + indent);
    }

    // The cell inset the control draws with, on both sides, plus a hair so the value is not flush
    // against the rule down the right-hand edge.
    return widest + 6;
  }

  /// <summary>
  /// Copying, in the three shapes §11 asks for.
  /// </summary>
  /// <remarks>
  /// Raw values, not what the column shows: a cell copied out of a monitor is on its way into
  /// something that will do arithmetic with it, and "1.5G" is not a number (PRD §76). This is the
  /// same call the terminal's copy keys make.
  /// </remarks>
  private ToolStripMenuItem BuildEditMenu() {
    var menu = new ToolStripMenuItem("Edit");
    // Ctrl+C is the rows, not the cell. §95 asks for the rows when there is no cell selection, and
    // this table has no cell selection to speak of — the current column is a keyboard position, not
    // something the header can be drawn to show. So the obvious chord does the obvious thing, and
    // the cell, which needs somebody to have chosen a column first, gets the deliberate one.
    menu.DropDownItems.Add(Shortcut("Copy row, or every ticked row", Keys.Control | Keys.C, this.CopyRows));
    menu.DropDownItems.Add(Shortcut("Copy cell", Keys.Control | Keys.Shift | Keys.C, this.CopyCell));
    menu.DropDownItems.Add(new ToolStripSeparator());
    menu.DropDownItems.Add(Shortcut("Tick every row", Keys.Control | Keys.A, () => this.TickAll(invert: false)));
    menu.DropDownItems.Add(Item("Invert the ticks", () => this.TickAll(invert: true)));
    menu.DropDownItems.Add(Item("Clear the ticks", this.ClearTicks));
    menu.DropDownItems.Add(new ToolStripSeparator());
    menu.DropDownItems.Add(Shortcut("Find…", Keys.Control | Keys.F, this.FocusFilter));
    menu.DropDownItems.Add(Shortcut("Export table…", Keys.Control | Keys.E, this.ExportTable));
    return menu;
  }

  private void CopyCell() {
    if (this._binder.SelectedRow is not { } row) {
      this._status.Text = "nothing is selected";
      return;
    }

    var index = this._view.FindRow(row.Key);
    if (index < 0)
      return;

    var field = this._columns.CurrentField;
    var view = this._view.Rows[index];
    ref readonly var process = ref this._sampler.Current.Processes[view.Index];
    var text = FieldAccessor.RawText(field, in process, this._sampler.Delta, view.Index)
      ?? row.TextOf(field);

    this.PutOnClipboard(text, $"{ColumnSet.Info(field).Header} of {row.Name}");
  }

  /// <summary>
  /// The ticked rows, or the selected one when nothing is ticked.
  /// </summary>
  /// <remarks>
  /// One item rather than two, and the same rule the terminal's <c>Y</c> follows: somebody who has
  /// ticked rows means those, and somebody who has not means the one under the cursor. A separate
  /// "copy ticked rows" that did nothing when nothing was ticked would be an item that looks broken
  /// most of the time.
  /// </remarks>
  private void CopyRows() {
    var ticked = this.TickedKeys();
    if (ticked.Count > 0) {
      this.PutOnClipboard(this.RowsAsText(ticked.Contains), $"{ticked.Count} rows");
      return;
    }

    if (this._binder.SelectedRow is not { } row) {
      this._status.Text = "nothing is selected";
      return;
    }

    this.PutOnClipboard(this.RowsAsText(key => key == row.Key), "one row");
  }

  /// <summary>A header line and one tab-separated line per row, over the columns that are showing.</summary>
  private string RowsAsText(Func<ProcessKey, bool> wanted) {
    var fields = this.ExportableFields();
    var builder = new System.Text.StringBuilder(256);
    foreach (var field in fields)
      builder.Append(ColumnSet.Info(field).Header).Append('\t');

    if (fields.Length > 0)
      --builder.Length;

    builder.Append('\n');

    var processes = this._sampler.Current.Processes;
    foreach (var row in this._view.Rows) {
      if (row.IsGroupHeader)
        continue;

      ref readonly var process = ref processes[row.Index];
      if (!wanted(process.Key))
        continue;

      foreach (var field in fields)
        builder
          .Append(FieldAccessor.RawText(field, in process, this._sampler.Delta, row.Index) ?? string.Empty)
          .Append('\t');

      if (fields.Length > 0)
        --builder.Length;

      builder.Append('\n');
    }

    return builder.ToString();
  }

  /// <summary>The showing columns that have text. A drawn history has none, so it is not one.</summary>
  private ProcessField[] ExportableFields() {
    var fields = new List<ProcessField>(this._columns.Count);
    for (var i = 0; i < this._columns.Count; ++i) {
      var field = this._columns.FieldAt(i);
      if (!ColumnSet.Info(field).IsGraph)
        fields.Add(field);
    }

    return [.. fields];
  }

  private void PutOnClipboard(string text, string what) {
    NativeForms.Clipboard.SetText(text);
    this._status.Text = $"copied {what}";
  }

  /// <summary>
  /// Writes the table out, in whichever of the six formats the file name asks for (PRD §61).
  /// </summary>
  /// <remarks>
  /// The columns that are showing and the rows that pass the filter — what is on screen, which is
  /// what somebody who chose both of those means by "the table". The format comes from the
  /// extension, so choosing <c>.csv</c> in the box is the whole of choosing CSV.
  /// </remarks>
  private void ExportTable() {
    var dialog = new SaveFileDialog {
      Title = "Export the process table",
      FileName = "processes.tsv",
      Filter = "Tab separated (*.tsv)|*.tsv|Comma separated (*.csv)|*.csv|JSON (*.json)|*.json"
        + "|JSON lines (*.jsonl)|*.jsonl|Markdown (*.md)|*.md|Plain text (*.txt)|*.txt",
    };

    if (dialog.ShowDialog() != DialogResult.OK || dialog.FileName.Length == 0)
      return;

    if (!Exporter.TryParseFormat(Path.GetExtension(dialog.FileName).TrimStart('.'), out var format))
      format = ExportFormat.Tsv;

    try {
      using (var writer = new StreamWriter(dialog.FileName, false))
        Exporter.Write(
          writer,
          format,
          this._sampler.Current,
          this._sampler.Delta,
          this._view,
          this.ExportableFields(),
          this._view.TreeMode
        );

      this._status.Text = $"wrote {this._view.MatchCount} rows to {dialog.FileName} as {format}";
    } catch (IOException problem) {
      MessageBox.Show(problem.Message, "Process Manager");
    } catch (UnauthorizedAccessException problem) {
      MessageBox.Show(problem.Message, "Process Manager");
    }
  }

  #endregion

  #region ticking rows (PRD §11)

  /// <summary>
  /// The processes somebody has ticked.
  /// </summary>
  /// <remarks>
  /// Read off the tree rather than kept in a set beside it, so the two can never disagree about what
  /// is ticked — and a heading's tick, if the toolkit ever lets one be set, is not a process and
  /// contributes nothing.
  /// </remarks>
  private HashSet<ProcessKey> TickedKeys() {
    var ticked = new HashSet<ProcessKey>();
    for (var row = 0; row < this._tree.VisibleNodeCount; ++row)
      if (this._tree.NodeAt(row) is { Checked: true, Tag: ProcessRow process })
        ticked.Add(process.Key);

    return ticked;
  }

  private void TickAll(bool invert) {
    for (var row = 0; row < this._tree.VisibleNodeCount; ++row)
      if (this._tree.NodeAt(row) is { Tag: ProcessRow } node)
        node.Checked = invert ? !node.Checked : true;

    this._status.Text = $"{this.TickedKeys().Count} rows ticked";
  }

  private void ClearTicks() {
    for (var row = 0; row < this._tree.VisibleNodeCount; ++row)
      if (this._tree.NodeAt(row) is { Tag: ProcessRow } node)
        node.Checked = false;

    this._status.Text = "nothing is ticked now";
  }

  /// <summary>What the status bar adds when rows are ticked, so the count is never a surprise.</summary>
  private string TickedSuffix {
    get {
      var ticked = this.TickedKeys().Count;
      return ticked == 0 ? string.Empty : $", {ticked} ticked";
    }
  }

  /// <summary>
  /// Ends every ticked process, having said how many there are (PRD §11, §90).
  /// </summary>
  /// <remarks>
  /// The count is the whole of the confirmation's job here. "End 14 processes?" and "End Firefox?"
  /// are the same gesture and very different requests, and a bulk action that does not name its
  /// size is one somebody agrees to without knowing what they agreed to.
  /// </remarks>
  private void EndTicked() {
    var ticked = this.TickedKeys();
    if (ticked.Count == 0) {
      this._status.Text = "no rows are ticked";
      return;
    }

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    var answer = MessageBox.Show(
      $"End the {ticked.Count} ticked process{(ticked.Count == 1 ? string.Empty : "es")}?\n\n{_EndsWithoutAsking}",
      "Process Manager",
      MessageBoxButtons.YesNo
    );

    if (answer != DialogResult.Yes)
      return;

    var sent = 0;
    var refused = 0;
    foreach (var key in ticked) {
      if (this._actions.Terminate(key).Succeeded)
        ++sent;
      else
        ++refused;
    }

    this.ClearTicks();
    this._status.Text = refused == 0
      ? $"ended {sent} processes"
      : $"ended {sent}; {refused} refused";

    this.Refresh();
  }

  #endregion

  /// <summary>
  /// Moves the sort to the next showing column, which is what a keyboard sort is (PRD §11).
  /// </summary>
  /// <remarks>
  /// Around the columns that are on screen rather than around the whole registry: a key that steps
  /// through a hundred and twenty fields, most of them hidden, sorts by things nobody can see the
  /// result of.
  /// </remarks>
  private void StepSort(int direction) {
    var sortable = new List<ProcessField>(this._columns.Count);
    for (var i = 0; i < this._columns.Count; ++i)
      if (ColumnSet.Info(this._columns.FieldAt(i)).IsSortable)
        sortable.Add(this._columns.FieldAt(i));

    if (sortable.Count == 0)
      return;

    var index = sortable.IndexOf(this._view.SortColumn);
    index = ((index < 0 ? 0 : index) + direction + sortable.Count) % sortable.Count;
    this._view.SortColumn = sortable[index];
    this._view.SortDescending = sortable[index].PrefersDescending();
    this._view.ClearSecondarySort();
    this.RebuildColumns();
    this.Rebind();
    this._status.Text = $"sorted by {ColumnSet.Info(sortable[index]).Header}";
  }

  /// <summary>
  /// The columns that break a tie in the first one (PRD §11).
  /// </summary>
  /// <remarks>
  /// Sorting by state and then by memory is the request behind most of the two-column sorts anybody
  /// asks for: a group of rows with identical values in the first column is exactly where a second
  /// one earns its place. Shift-clicking a header does the same thing with the mouse.
  /// </remarks>
  private ToolStripMenuItem BuildTieBreakerMenu() {
    var menu = new ToolStripMenuItem("Break ties with");
    foreach (var column in (ReadOnlySpan<ProcessField>)[
      ProcessField.CpuPercent,
      ProcessField.PrivateBytes,
      ProcessField.WorkingSetBytes,
      ProcessField.ThreadCount,
      ProcessField.State,
      ProcessField.Name,
      ProcessField.UserName,
      ProcessField.StartTime,
    ]) {
      var chosen = column;
      menu.DropDownItems.Add(Item(column.Header(), () => {
        this._view.AddSortKey(chosen, chosen.PrefersDescending());
        this.RebuildColumns();
        this.Rebind();
        this._status.Text = $"ties are broken by {ColumnSet.Info(chosen).Header} now";
      }));
    }

    return menu;
  }

  #region grouping (PRD §83)

  private void GroupBy(ProcessGrouping grouping) {
    this._view.Grouping = grouping;
    this.Rebind();
    this._status.Text = grouping == ProcessGrouping.None
      ? "the rows are one flat list again"
      : $"grouped by {Settings.UserSettings.NameOfGrouping(grouping)} — {this._view.Groups.Count} heading(s)";
  }

  /// <summary>
  /// How the rows are arranged (PRD §83).
  /// </summary>
  /// <remarks>
  /// The tree is one of the entries rather than a separate toggle, because picking one of these is
  /// picking how the list is arranged and the tree is one of the answers. §83's other three —
  /// application and publisher — are absent: naming a group needs something to read it off, and this
  /// program has no notion of an application and no signature verification. A heading that guessed
  /// would be a heading that is not true.
  /// </remarks>
  private ToolStripMenuItem BuildGroupingMenu() {
    var menu = new ToolStripMenuItem("Group by");
    foreach (var (grouping, label) in (ReadOnlySpan<(ProcessGrouping, string)>)[
      (ProcessGrouping.None, "Nothing — one flat list"),
      (ProcessGrouping.ParentTree, "Parent tree"),
      (ProcessGrouping.User, "User"),
      (ProcessGrouping.Session, "Session"),
      (ProcessGrouping.Service, "Service"),
      (ProcessGrouping.Executable, "Executable"),
      (ProcessGrouping.Container, "Container"),
      (ProcessGrouping.Cgroup, "Cgroup"),
      (ProcessGrouping.Package, "Package"),
    ]) {
      var chosen = grouping;
      menu.DropDownItems.Add(Item(label, () => this.GroupBy(chosen)));
    }

    return menu;
  }

  #endregion

  private void ShowPerformance() {
    if (this._performance is not null) {
      this._performance.UpdateFromSample();
      return;
    }

    var window = new PerformanceWindow(this._probe, this._sampler) { SecondsPerSample = this.Interval / 1000d };
    // Forgetting it on close is what keeps the tick from refreshing a window that is gone.
    window.FormClosed += (_, _) => this._performance = null;
    this._performance = window;
    window.Show();
  }

}
