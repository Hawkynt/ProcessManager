using System.Drawing;
using System.Text;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// What the machine is and what it is doing (PRD §45, §46, §47).
/// </summary>
/// <remarks>
/// <para>
/// The shape §45 asks for: a rail of resources down the left, and the selected one's history and
/// figures filling the rest. One entry per processor, disk and adapter, each carrying its own
/// headline reading so the rail answers "which of these is busy" before anything is clicked.
/// </para>
/// <para>
/// Four levels of information, and nothing jumps a level (§45.2): the graphs, the live measurements,
/// the hardware specifications beside them, and the engineering diagnostics collapsed underneath.
/// The last of those is what keeps a memory page — which has thirty figures worth showing and twelve
/// worth reading first — from becoming a wall.
/// </para>
/// <para>
/// Modeless, and refreshed from the main window's sample tick. It was modal and painted once, which
/// made it a performance page whose numbers never moved — the one thing such a page must not be.
/// </para>
/// <para>
/// Every figure comes from <see cref="PerformanceReport"/>, the same source <c>--host</c> renders,
/// so the window and the terminal cannot disagree about the machine (PRD §58).
/// </para>
/// </remarks>
public sealed class PerformanceWindow : Form {

  private const int _RailWidth = 230;
  private const int _MenuHeight = 26;

  /// <summary>
  /// How many samples every ring holds: fifteen minutes at a second each, and a little over.
  /// </summary>
  /// <remarks>
  /// The longest span §45.4 offers, because a span that can be selected and has no history behind it
  /// is a menu entry that draws an empty graph.
  /// </remarks>
  private const int _HistorySamples = 960;

  /// <summary>The shortest a graph may be squeezed to before the numbers stop taking room.</summary>
  private const int _MinimumPlotHeight = 120;

  private readonly ISystemProbe _probe;
  private readonly Sampler _sampler;
  private readonly ResourceRail _rail = new() { AccessibleName = "Resources", AccessibleRole = AccessibleRole.List };
  private readonly HistoryPlot _plot = new();
  private readonly MenuStrip _menu = new() { AccessibleName = "Main menu", AccessibleRole = AccessibleRole.MenuBar };
  private readonly CheckBox _perCore = new() { Text = "Per logical processor" };

  /// <summary>
  /// The third of §46's graph modes, and only on a machine that has more than one node.
  /// </summary>
  /// <remarks>
  /// Beside the per-core box rather than replacing it, and the two are exclusive: they are two ways
  /// of dividing the same processor and showing both at once would put a node's plot beside the
  /// plots of the very cores it is the mean of.
  /// </remarks>
  private readonly CheckBox _perNode = new() { Text = "Per NUMA node", Visible = false };

  /// <summary>How the processors are arranged, read once — nothing repartitions while we watch.</summary>
  private readonly CpuTopology _topology;
  private readonly Button _pause = new() { Text = "Pause" };
  private readonly Button _inspect = new() { Text = "Expand" };
  private readonly ComboBox _spanBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };

  /// <summary>Memory's composition bar, and the line under it that names what the pointer is over.</summary>
  private readonly CompositionBar _composition = new() { Visible = false, AccessibleName = "Memory composition", AccessibleRole = AccessibleRole.Graphic };

  private readonly Label _compositionHint = new() { Visible = false };

  /// <summary>One small plot per core, or one per series where a resource has several.</summary>
  private readonly List<HistoryPlot> _corePlots = [];
  private readonly Label _heading = new();

  /// <summary>The hardware this page is about, top right — §45.1's header.</summary>
  private readonly Label _model = new();

  private readonly Label _liveHeading = new() { Text = "Live" };
  private readonly Label _hardwareHeading = new() { Text = "Hardware" };

  /// <summary>The control that opens and closes the fourth level (§45.2).</summary>
  /// <remarks>
  /// A button rather than a label with a click handler: a <see cref="Label"/> is the platform's own
  /// static widget wherever it can be, and a static widget has no pointer of its own on either
  /// backend — an expander made of one is a control that silently does nothing.
  /// </remarks>
  private readonly Button _diagnosticsHeading = new();

  private readonly List<Label> _labels = [];
  private readonly List<Label> _values = [];
  private readonly List<Label> _diagnosticLabels = [];
  private readonly List<Label> _diagnosticValues = [];

  /// <summary>One ring per resource, keyed by section title and added to as devices appear.</summary>
  private readonly Dictionary<string, HistoryRing<Rate>> _history = new(StringComparer.Ordinal);

  /// <summary>The second series where a resource has one: kernel time under total CPU (PRD §46).</summary>
  private readonly Dictionary<string, HistoryRing<Rate>> _secondary = new(StringComparer.Ordinal);

  private IReadOnlyList<PerformanceSection> _sections = [];
  private string _shown = string.Empty;

  /// <summary>Where the two statistic columns start, and how much room each has.</summary>
  private Rectangle _plotArea;
  private int _statisticsTop;
  private int _rowsPerColumn;
  private int _columnWidth;

  private bool _compact;
  private bool _diagnosticsOpen;

  /// <summary>How many samples the drawing is behind the collection, which is what pausing is.</summary>
  private int _frozenSamples = -1;

  /// <summary>What the last layout was computed for, so an unchanged page is not moved.</summary>
  private (Size Client, int Pitch, int Rows, int Diagnostics, bool Open, bool Tall, int Longest) _shape;

  /// <summary>
  /// Whether the resource on screen has no graph, and so hands its plot area to its figures.
  /// </summary>
  /// <remarks>
  /// The overview and the activity lists are pages of text: they measure nothing, they are given no
  /// graph (§45.6), and leaving the space where the graph would have been empty above them wastes
  /// half a window on a page that has twenty-four rows to show.
  /// </remarks>
  private bool _tall;

  /// <summary>
  /// What the collapsed block holds for the resource on screen: how many rows, and how long the
  /// longest of them is.
  /// </summary>
  /// <remarks>
  /// Per resource rather than the largest anybody has, which the two statistic columns deliberately
  /// are. The block is at the bottom and its height comes out of the graphs, so reserving memory's
  /// twenty rows on a processor page that has five would squeeze every graph on the page to its
  /// floor to leave room for nothing.
  /// </remarks>
  private (int Rows, int Longest) _diagnostics;

  /// <param name="openOnBusiest">
  /// Whether to open on whatever is under the greatest load. Null reads the setting from the file,
  /// which is what the program does; a caller that says either way is not asking about anybody's
  /// configuration — which is what lets this be tested without one (PRD §67).
  /// </param>
  /// <param name="historyMultiplier">
  /// Requested older-history horizon. Null follows the settings file when the ordinary application
  /// constructor is used. Tests that supply <paramref name="openOnBusiest"/> keep their historical
  /// isolation and use the built-in multiplier unless they explicitly supply this value too.
  /// </param>
  public PerformanceWindow(ISystemProbe probe, Sampler sampler, bool? openOnBusiest = null, double? historyMultiplier = null) {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(sampler);

    this._probe = probe;
    this._sampler = sampler;
    this._topology = probe.DescribeTopology();

    // Product construction asks the file once and uses it for both performance-page preferences.
    // Tests have historically supplied openOnBusiest specifically to avoid depending on a real
    // profile; keep that property by not reading the file merely to obtain the new history setting.
    var settings = openOnBusiest is null ? SettingsStore.Load() : null;
    this.OpenOnBusiest = openOnBusiest ?? settings!.PerformanceOpensOnBusiest;
    this.HistoryMultiplier = historyMultiplier
      ?? (settings is null
        ? UserSettings.DefaultPerformanceHistoryMultiplier
        : settings.CompressPerformanceHistory ? settings.PerformanceHistoryMultiplier : 1);

    this.Text = "System information";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;
    // §45.1's reference size, near enough: the rail plus a plot area wide enough that a minute of
    // history is a minute of pixels.
    this.Bounds = new(0, 0, 1180, 780);
    // §45.1's floor. Without it the content's own size becomes the minimum and the page can only
    // ever be made larger.
    this.MinimumSize = new(900, 600);

    this._rail.SelectedIndexChanged += (_, _) => this.ShowSelected(force: true);
    this._rail.ContextMenuStrip = this.BuildResourceMenu();
    this.Controls.Add(this._rail);

    // First and second, in this order: the header is the two of them, and everything that reads the
    // window back — the capture log, the tests — identifies them by being first.
    // Large and left, per §45.1's header — the resource is the title of the page, and the hardware
    // beside it is the answer to a second question.
    this._heading.Font = new(this._heading.Font.Family, 13.5f, FontStyle.Bold);
    this.Controls.Add(this._heading);
    this._model.TextAlign = ContentAlignment.TopRight;
    this.Controls.Add(this._model);

    this.Controls.Add(this._plot);
    this.Controls.Add(this._composition);
    this.Controls.Add(this._compositionHint);

    // The processor's two views, as one box rather than as twenty rail entries: a machine with
    // twenty cores would bury the disks under them, and the question "overall or per core" is one
    // switch and not twenty destinations (PRD §46).
    this._perCore.CheckedChanged += (_, _) => {
      if (this._perCore.Checked)
        this._perNode.Checked = false;

      this.ShowSelected(force: true);
    };

    this._perCore.Visible = false;
    this.Controls.Add(this._perCore);

    this._perNode.CheckedChanged += (_, _) => {
      if (this._perNode.Checked)
        this._perCore.Checked = false;

      this.ShowSelected(force: true);
    };

    this.Controls.Add(this._perNode);

    this.BuildGraphControls();

    // Two columns, because the live measurements and the hardware facts answer different questions
    // and reading them as one list is what makes a performance page look like a data dump (§45.1).
    this.Controls.Add(this._liveHeading);
    this.Controls.Add(this._hardwareHeading);

    this._diagnosticsHeading.Click += (_, _) => this.ToggleDiagnostics();
    this.Controls.Add(this._diagnosticsHeading);
    this.NameDiagnosticsHeading();

    this.BuildMenu();
    this.WatchComposition();
    this.Resize += (_, _) => this.ApplyLayout();

    this.ApplyLayout();
    this.UpdateFromSample();
  }

  /// <summary>
  /// How far apart the samples are, which is what turns a span in seconds into a count.
  /// </summary>
  /// <remarks>
  /// Told rather than assumed: the main window's interval is a setting, and a page whose axis says
  /// sixty seconds while the machine is sampled every four is wrong by a factor of four without
  /// anything on it looking wrong (PRD §72).
  /// </remarks>
  public double SecondsPerSample {
    get;
    set {
      field = value <= 0 ? 1 : value;
      this.ApplySpan();
    }
  } = 1;

  /// <summary>
  /// How many seconds the newest, uncompressed part of the graphs covers (PRD §45.4).
  /// </summary>
  /// <remarks>
  /// Set here rather than only through the drop-down, because the same recent span has to reach the
  /// rail's sparklines as well as the plots — one page, one time axis.
  /// </remarks>
  public int SpanSeconds {
    get;
    set {
      if (field == value || value < 1)
        return;

      field = value;
      this.ApplySpan();
    }
  } = 60;

  /// <summary>
  /// Requested total history horizon relative to <see cref="SpanSeconds"/>; one means linear.
  /// </summary>
  /// <remarks>
  /// The renderer caps this to what its backing ring can actually retain. Keeping the request on the
  /// page rather than on one plot means the rail, a per-core wall and a stacked GPU page always use
  /// the same time semantics.
  /// </remarks>
  public double HistoryMultiplier {
    get;
    set {
      var normalized = double.IsFinite(value) ? Math.Clamp(value, 1d, 64d) : 1d;
      if (Math.Abs(field - normalized) < 0.000001)
        return;

      field = normalized;
      this.ApplySpan();
    }
  } = UserSettings.DefaultPerformanceHistoryMultiplier;

  /// <summary>The real visible horizon of the currently displayed graphs, after retention capping.</summary>
  public double VisibleHistorySeconds {
    get {
      foreach (var plot in this.Plots())
        if (plot.Visible)
          return plot.VisibleSpanSeconds;

      return this.SpanSeconds;
    }
  }

  /// <summary>Whether the drawing is frozen. Collection carries on regardless (PRD §45.4).</summary>
  public bool Paused => this._frozenSamples >= 0;

  /// <summary>
  /// Whether the page opens on whatever is under the greatest load (PRD §45.3, §67).
  /// </summary>
  /// <remarks>
  /// Read from the settings file when the window is built, and only ever consulted while nothing is
  /// selected: once a reader has chosen a resource, a disk that gets briefly busy must not take the
  /// page away from them.
  /// </remarks>
  public bool OpenOnBusiest { get; set; } = true;

  #region the strip above the graphs (PRD §45.4, §45.8)

  private void BuildGraphControls() {
    foreach (var span in _Spans)
      this._spanBox.Items.Add(span.Label);

    this._spanBox.SelectedIndex = 1;
    this._spanBox.AccessibleName = "Recent history span";
    this._spanBox.SelectedIndexChanged += (_, _) => this.ChooseSpan(this._spanBox.SelectedIndex);
    this.Controls.Add(this._spanBox);

    this._pause.AccessibleName = "Pause the graphs";
    this._pause.Click += (_, _) => this.TogglePause();
    this.Controls.Add(this._pause);

    this._inspect.AccessibleName = "Current, minimum, maximum and average";
    this._inspect.Click += (_, _) => this.Inspect();
    this.Controls.Add(this._inspect);
  }

  /// <summary>The recent spans §45.4 offers, shortest first.</summary>
  private static readonly (string Label, int Seconds)[] _Spans = [
    ("30 seconds", 30),
    ("60 seconds", 60),
    ("2 minutes", 120),
    ("5 minutes", 300),
    ("15 minutes", 900),
  ];

  private void ChooseSpan(int index) {
    if ((uint)index >= (uint)_Spans.Length)
      return;

    this.SpanSeconds = _Spans[index].Seconds;
    if (this._spanBox.SelectedIndex != index)
      this._spanBox.SelectedIndex = index;
  }

  /// <summary>Puts the time mode on every plot and on the rail, so one page has one time axis.</summary>
  private void ApplySpan() {
    foreach (var plot in this.Plots()) {
      plot.SpanSeconds = this.SpanSeconds;
      plot.SecondsPerSample = this.SecondsPerSample;
      plot.HistoryMultiplier = this.HistoryMultiplier;
      plot.Invalidate();
    }

    this._rail.Samples = Math.Max(1, (int)Math.Round(this.SpanSeconds / this.SecondsPerSample));
    this._rail.HistoryMultiplier = this.HistoryMultiplier;
    this._rail.Invalidate();
  }

  private IEnumerable<HistoryPlot> Plots() {
    yield return this._plot;
    foreach (var plot in this._corePlots)
      yield return plot;
  }

  /// <summary>
  /// Freezes the drawing without clearing the history or stopping collection (PRD §45.4).
  /// </summary>
  /// <remarks>
  /// Counted in samples rather than remembered as a timestamp: every tick while paused pushes the
  /// drawn window one sample further back, so a plot paused on a spike still shows that spike ten
  /// minutes later — including after the ring it lives in has wrapped.
  /// </remarks>
  public void TogglePause() => this.SetPaused(!this.Paused);

  public void SetPaused(bool paused) {
    if (paused == this.Paused)
      return;

    this._frozenSamples = paused ? 0 : -1;
    this._pause.Text = paused ? "Resume" : "Pause";
    this.ApplyFreeze();
  }

  private void ApplyFreeze() {
    var skip = Math.Max(0, this._frozenSamples);
    foreach (var plot in this.Plots()) {
      plot.SkipNewest = skip;
      plot.Paused = this.Paused;
      plot.Invalidate();
    }

    this._rail.SkipNewest = skip;
    this._rail.Invalidate();
  }

  /// <summary>
  /// The inspection view §45.4 asks a double-click for: current, minimum, maximum and average.
  /// </summary>
  private void Inspect() {
    var text = new StringBuilder();
    var visible = this.VisibleHistorySeconds;
    text.Append(this._shown).Append(" — ");
    if (this.HistoryMultiplier <= 1.000001)
      text.Append("last ").Append(Duration(visible));
    else
      text.Append(Duration(visible)).Append(" visible; newest ").Append(Duration(this.SpanSeconds))
        .Append(" at ordinary resolution");

    text.AppendLine();
    text.AppendLine();
    foreach (var plot in this.Plots()) {
      if (!plot.Visible)
        continue;

      var statistics = plot.Statistics();
      if (statistics.Length > 0)
        text.AppendLine(statistics);
    }

    new InspectionWindow(this._shown, text.ToString()).Show();
  }

  /// <summary>A compact truthful duration for graph menus and inspection text.</summary>
  private static string Duration(double seconds) => seconds switch {
    >= 3600 => $"{seconds / 3600:0.#} hours",
    >= 120 => $"{seconds / 60:0.#} minutes",
    _ => $"{seconds:0.#} seconds",
  };

  #endregion

  #region menus and the clipboard (PRD §45.8)

  private void BuildMenu() {
    // Docked would leave the absolutely-placed content underneath it; a MenuStrip also has no
    // intrinsic height, so it is given both by hand. Without the height it is present, mapped and
    // nought pixels tall, which photographs exactly like a menu that was never added.
    this._menu.Bounds = new(0, 0, this.ClientSize.Width, _MenuHeight);

    var view = new ToolStripMenuItem("View");
    // Ctrl+1…Ctrl+6 pick the six §45.8 names, by prefix rather than by index: a machine with no
    // discrete graphics has no GPU entry, and the shortcut for one that is not there must do
    // nothing rather than land on whatever took its place.
    view.DropDownItems.Add(Item("Overview", () => this.SelectStarting("System"), Keys.Control | Keys.D1));
    view.DropDownItems.Add(Item("Processor", () => this.SelectStarting("Processor"), Keys.Control | Keys.D2));
    view.DropDownItems.Add(Item("Memory", () => this.SelectStarting("Memory"), Keys.Control | Keys.D3));
    view.DropDownItems.Add(Item("Disk", () => this.SelectStarting("Disk"), Keys.Control | Keys.D4));
    view.DropDownItems.Add(Item("Network", () => this.SelectStarting("Network"), Keys.Control | Keys.D5));
    view.DropDownItems.Add(Item("GPU", () => this.SelectStarting("GPU"), Keys.Control | Keys.D6));
    view.DropDownItems.Add(new ToolStripSeparator());
    view.DropDownItems.Add(Item("Comfortable", () => this.SetDensity(compact: false)));
    view.DropDownItems.Add(Item("Compact", () => this.SetDensity(compact: true)));
    view.DropDownItems.Add(new ToolStripSeparator());
    view.DropDownItems.Add(Item("Engineering diagnostics", this.ToggleDiagnostics));
    this._menu.Items.Add(view);

    var graph = new ToolStripMenuItem("Graph");
    graph.DropDownItems.Add(Item("Pause", () => this.SetPaused(true), Keys.Space));
    graph.DropDownItems.Add(Item("Resume", () => this.SetPaused(false), Keys.F5));
    graph.DropDownItems.Add(new ToolStripSeparator());
    for (var i = 0; i < _Spans.Length; ++i) {
      var chosen = i;
      graph.DropDownItems.Add(Item(_Spans[i].Label, () => this.ChooseSpan(chosen)));
    }

    graph.DropDownItems.Add(this.BuildHistoryMenu());
    graph.DropDownItems.Add(new ToolStripSeparator());
    graph.DropDownItems.Add(Item("Expand…", this.Inspect));
    graph.DropDownItems.Add(Item("Show logical processors", () => this._perCore.Checked = !this._perCore.Checked));
    // Does nothing on a machine with one node, like Ctrl+6 on a machine with no discrete graphics:
    // the mode has nothing to divide, and landing somewhere else would be worse than doing nothing.
    graph.DropDownItems.Add(Item("Show NUMA nodes", () => this._perNode.Checked = !this._perNode.Checked));
    this._menu.Items.Add(graph);

    var copy = new ToolStripMenuItem("Copy");
    copy.DropDownItems.Add(Item("Copy current values", this.CopyCurrent, Keys.Control | Keys.C));
    copy.DropDownItems.Add(Item("Copy full diagnostics", this.CopyEverything));
    this._menu.Items.Add(copy);

    this.Controls.Add(this._menu);
  }

  /// <summary>The same commands on the rail, where §45.8 puts them: on the resource itself.</summary>
  private ContextMenuStrip BuildResourceMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(Item("Copy current values", this.CopyCurrent));
    menu.Items.Add(Item("Copy full diagnostics", this.CopyEverything));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("Pause", () => this.SetPaused(true)));
    menu.Items.Add(Item("Resume", () => this.SetPaused(false)));
    menu.Items.Add(new ToolStripSeparator());
    var span = new ToolStripMenuItem("Change graph");
    for (var i = 0; i < _Spans.Length; ++i) {
      var chosen = i;
      span.DropDownItems.Add(Item(_Spans[i].Label, () => this.ChooseSpan(chosen)));
    }

    menu.Items.Add(span);
    menu.Items.Add(this.BuildHistoryMenu());
    menu.Items.Add(Item("Show logical processors", () => this._perCore.Checked = !this._perCore.Checked));
    menu.Items.Add(Item("Engineering diagnostics", this.ToggleDiagnostics));
    return menu;
  }

  /// <summary>
  /// Session-local graph horizon override. Persistent defaults live in Settings; this menu is the
  /// quick troubleshooting control beside the graph itself.
  /// </summary>
  private ToolStripMenuItem BuildHistoryMenu() {
    var history = new ToolStripMenuItem("Older history");
    history.DropDownItems.Add(Item("Linear — selected span only", () => this.HistoryMultiplier = 1));
    foreach (var multiplier in UserSettings.OfferedPerformanceHistoryMultipliers) {
      var chosen = multiplier;
      history.DropDownItems.Add(Item($"{chosen:0.#}× history", () => this.HistoryMultiplier = chosen));
    }

    return history;
  }

  private static ToolStripMenuItem Item(string text, Action action, Keys shortcut = Keys.None) {
    var item = new ToolStripMenuItem(text);
    if (shortcut != Keys.None)
      item.ShortcutKeys = shortcut;

    item.Click += (_, _) => action();
    return item;
  }

  /// <summary>The selected resource's figures, as text somebody can paste into a mail (PRD §45.8).</summary>
  private void CopyCurrent() => Clipboard.SetText(this.CurrentValuesText());

  /// <summary>The selected resource as text. Public so a test can read what would be copied.</summary>
  public string CurrentValuesText() => this.Describe(this.Find(this._shown));

  /// <summary>
  /// The whole machine, which is what makes a support conversation possible (PRD §45.8).
  /// </summary>
  /// <remarks>
  /// Every section and every level, including the diagnostics the window keeps collapsed: the point
  /// of this is to be pasted somewhere else, and what is worth hiding from a reader who is looking
  /// at the machine is exactly what is worth sending to one who is not.
  /// </remarks>
  private void CopyEverything() => Clipboard.SetText(this.DiagnosticsText());

  /// <summary>The whole machine as text. Public so a test can read what would be copied.</summary>
  public string DiagnosticsText() {
    var text = new StringBuilder();
    foreach (var section in this._sections) {
      if (text.Length > 0)
        text.AppendLine();

      text.Append(this.Describe(section));
    }

    return text.ToString();
  }

  private string Describe(PerformanceSection? section) {
    if (section is not { } chosen)
      return string.Empty;

    var text = new StringBuilder();
    text.AppendLine(chosen.Title);
    foreach (var row in chosen.Rows)
      text.Append("  ").Append(row.Label.PadRight(22)).AppendLine(row.Value);

    return text.ToString();
  }

  /// <summary>Moves the rail to the first resource whose title starts with <paramref name="prefix"/>.</summary>
  private void SelectStarting(string prefix) {
    for (var i = 0; i < this._rail.Items.Count; ++i)
      if (NameOf(this._rail.Items[i]).StartsWith(prefix, StringComparison.Ordinal)) {
        this._rail.SelectedIndex = i;
        return;
      }
  }

  #endregion

  #region density and the collapsed block (PRD §45.2, §45.7)

  /// <summary>
  /// Comfortable or compact (PRD §45.7).
  /// </summary>
  /// <remarks>
  /// Compact is not merely smaller: it tightens the rows, shortens the rail's sparklines and opens
  /// the diagnostics, because somebody who has asked for density has asked to see more at once
  /// rather than to see the same thing in less space.
  /// </remarks>
  public void SetDensity(bool compact) {
    if (this._compact == compact)
      return;

    this._compact = compact;
    this._rail.Compact = compact;
    this._diagnosticsOpen = compact;
    this.NameDiagnosticsHeading();
    this.ShowSelected(force: true);
  }

  public bool IsCompact => this._compact;

  public bool DiagnosticsOpen => this._diagnosticsOpen;

  public void ToggleDiagnostics() {
    this._diagnosticsOpen = !this._diagnosticsOpen;
    this.NameDiagnosticsHeading();
    this.ShowSelected(force: true);
  }

  /// <summary>
  /// The expander's own text, which carries its state in a word as well as in a glyph.
  /// </summary>
  /// <remarks>
  /// §45.9: nothing is identified by colour or by an icon alone. "Show" and "Hide" are what a screen
  /// reader announces, and the triangle is for the eye.
  /// </remarks>
  private void NameDiagnosticsHeading() {
    this._diagnosticsHeading.Text = this._diagnosticsOpen
      ? "▾  Engineering diagnostics — hide"
      : "▸  Engineering diagnostics — show";

    this._diagnosticsHeading.AccessibleName = this._diagnosticsHeading.Text;
  }

  private int RowPitch => this._compact ? 16 : 20;

  #endregion

  /// <summary>
  /// Selects a resource by name, for a capture run that wants to photograph a particular page.
  /// </summary>
  /// <returns>Whether there is such a resource.</returns>
  public bool Show(string title) {
    for (var i = 0; i < this._rail.Items.Count; ++i) {
      if (!string.Equals(NameOf(this._rail.Items[i]), title, StringComparison.Ordinal))
        continue;

      this._rail.SelectedIndex = i;
      return true;
    }

    return false;
  }

  /// <summary>
  /// What the page is showing, in text — the capture run's evidence that §45's layout survived.
  /// </summary>
  public string DescribeForCapture() {
    var builder = new StringBuilder();
    builder.AppendLine($"page rail:    {this._rail.Items.Count} resources, showing '{this._shown}'");
    builder.AppendLine($"page header:  {this._heading.Text} / {this._model.Text}");

    var live = Filled(this._labels, 0, this._rowsPerColumn);
    var hardware = Filled(this._labels, this._rowsPerColumn, this._labels.Count);
    var diagnostics = Filled(this._diagnosticLabels, 0, this._diagnosticLabels.Count);

    var plots = 0;
    foreach (var plot in this.Plots())
      if (plot.Visible)
        ++plots;

    builder.AppendLine($"page stats:   {live} live, {hardware} hardware, {diagnostics} diagnostics");
    builder.AppendLine(
      $"page graphs:  {plots} visible, {this.SpanSeconds} s recent, {this.HistoryMultiplier:0.###}× requested, "
      + $"{this.VisibleHistorySeconds:0.#} s visible, {(this.Paused ? "paused" : "live")}"
    );
    builder.AppendLine($"page plots:   {this._plotArea.Width}x{this._plotArea.Height} at {this._plotArea.X},{this._plotArea.Y}");
    builder.AppendLine($"page bar:     {(this._composition.Visible ? "composition shown" : "none")}");
    return builder.ToString();
  }

  /// <summary>
  /// One line per resource: what each page would show if it were selected.
  /// </summary>
  /// <remarks>
  /// Thirteen pages and one photograph. A picture proves the layout of the resource it was taken on
  /// and says nothing about the twelve behind it — which is how a page whose single graph was laid
  /// out at nought by nought pixels survived a capture run that photographed the one page with a
  /// stack of them. This walks the rail and writes down what each one draws (PRD §9.6).
  /// </remarks>
  public string DescribeEveryPageForCapture() {
    var was = this._rail.SelectedIndex;
    var builder = new StringBuilder();
    for (var i = 0; i < this._rail.Items.Count; ++i) {
      this._rail.SelectedIndex = i;
      this.ShowSelected(force: true);

      var plots = 0;
      var smallest = int.MaxValue;
      foreach (var plot in this.Plots())
        if (plot.Visible) {
          ++plots;
          smallest = Math.Min(smallest, plot.Height);
        }

      var live = Filled(this._labels, 0, this._rowsPerColumn);
      var hardware = Filled(this._labels, this._rowsPerColumn, this._labels.Count);
      var diagnostics = DiagnosticsOf(this.Find(this._shown) ?? default).Rows;
      builder.AppendLine(
        $"  {this._shown,-24} {plots} graph(s) at {(plots == 0 ? 0 : smallest)} px · "
        + $"{live} live, {hardware} hardware, {diagnostics} diagnostic"
      );
    }

    this._rail.SelectedIndex = was;
    this.ShowSelected(force: true);
    return builder.ToString();
  }

  private static int Filled(List<Label> labels, int from, int to) {
    var filled = 0;
    for (var i = from; i < Math.Min(to, labels.Count); ++i)
      if (labels[i].Text.Length > 0)
        ++filled;

    return filled;
  }

  /// <summary>
  /// Rereads everything. Called on every sample tick for as long as the page is open.
  /// </summary>
  public void UpdateFromSample() {
    this._sections = PerformanceReport.Build(
      this._probe.DescribeHost(),
      this._sampler.Current,
      this._sampler.Delta,
      this._probe.DescribeDisk,
      this._probe.DescribeInterface,
      this._probe.DescribeGpus,
      topology: this._topology
    );

    this.RecordHistory();

    // A paused page falls one sample further behind on every tick, which is what keeps the picture
    // on the second it was paused on rather than letting it drift forward with the ring.
    if (this.Paused) {
      ++this._frozenSamples;
      this.ApplyFreeze();
    }

    this.SyncRail();
    this.ShowSelected(force: false);
  }

  /// <summary>
  /// Appends each resource's headline to its own ring.
  /// </summary>
  /// <remarks>
  /// Every resource, not only the one on screen: selecting a disk that has been idle for a minute
  /// should show that minute of idleness rather than starting blank.
  /// </remarks>
  private void RecordHistory() {
    foreach (var section in this._sections) {
      if (!section.HasPrimary)
        continue;

      // One ring per series, so a GPU's six move independently and a disk's transfer rate is not
      // overwritten by its active time.
      foreach (var graph in section.Series) {
        this.Ring(SeriesKey(section.Title, graph.Label)).Add(graph.Value);
        // And one more for a second line where the series has one — a disk's writes under its
        // reads, an adapter's send under its receive. Only where it was asked for: a ring fed
        // default(Rate) would draw a confident nought along the floor (PRD §5.3).
        if (graph.HasCompanion)
          this.Ring(CompanionKey(section.Title, graph.Label)).Add(graph.Companion);
      }

      this.Ring(section.Title).Add(section.Primary);

      if (!section.HasSecondary)
        continue;

      if (!this._secondary.TryGetValue(section.Title, out var under)) {
        under = new(_HistorySamples);
        this._secondary[section.Title] = under;
      }

      under.Add(section.Secondary);
    }
  }

  /// <summary>The ring for one series, made the first time it is asked for.</summary>
  private HistoryRing<Rate> Ring(string key) {
    if (this._history.TryGetValue(key, out var ring))
      return ring;

    ring = new(_HistorySamples);
    this._history[key] = ring;
    return ring;
  }

  /// <summary>
  /// A series is identified by its section and its label together, with a separator no label can
  /// contain — "Temperature" belongs to a GPU, and two GPUs each have one.
  /// </summary>
  private static string SeriesKey(string section, string label) => $"{section}\0{label}";

  /// <summary>
  /// And the second line of one, kept apart by the same separator.
  /// </summary>
  /// <remarks>
  /// A key of its own rather than a second ring hanging off the first, because everything that reads
  /// history here — the plot, the ceiling, the inspection view — takes a ring and a name.
  /// </remarks>
  private static string CompanionKey(string section, string label) => $"{section}\0{label}\0second";

  /// <summary>
  /// Keeps the rail in step with the sections.
  /// </summary>
  /// <remarks>
  /// The entries are rebuilt only when the set of resources changes — a disk appearing, an adapter
  /// going away — because rebuilding the list every second takes the selection with it.
  /// </remarks>
  private void SyncRail() {
    var wanted = new List<string>(this._sections.Count);
    foreach (var section in this._sections)
      if (section.IsTopLevel)
        wanted.Add(section.Title);

    var changed = wanted.Count != this._rail.Items.Count;
    if (!changed)
      for (var i = 0; i < wanted.Count; ++i)
        if (!string.Equals(NameOf(this._rail.Items[i]), wanted[i], StringComparison.Ordinal)) {
          changed = true;
          break;
        }

    if (changed) {
      var selected = this._rail.SelectedIndex;
      this._rail.Items.Clear();
      foreach (var title in wanted)
        this._rail.Items.Add(this.Entry(title));

      this._rail.SelectedIndex = this._rail.Items.Count == 0
        ? -1
        : selected < 0
          ? this.OpenOnBusiest ? this.BusiestOf(wanted) : 0
          : Math.Clamp(selected, 0, this._rail.Items.Count - 1);

      return;
    }

    for (var i = 0; i < wanted.Count; ++i)
      this._rail.Items[i] = this.Entry(wanted[i]);
  }

  /// <summary>
  /// Which resource to open on: whatever is under the greatest load (PRD §45.3).
  /// </summary>
  /// <remarks>
  /// Only the ones that measure a load are compared, and only on a fixed 0–100 scale. Both halves
  /// are needed. A network adapter's headline is bytes per second, and eleven thousand of those is
  /// not busier than a processor at eleven percent — it is a different quantity wearing a larger
  /// number. And a battery at 100 % charge or a sensor chip reading 65 °C on a hundred-degree scale
  /// are percentages of the right shape that measure no load at all: without the second test the
  /// page opened on a fully charged battery on every laptop (PRD §45.3).
  /// <para>
  /// Ties go to the earlier entry, which puts the processor first when the machine is idle. Somebody
  /// opening this page on a quiet machine expects the processor, not whichever disk happened to
  /// round up.
  /// </para>
  /// </remarks>
  private int BusiestOf(List<string> titles) {
    var best = 0;
    var highest = double.NegativeInfinity;
    for (var i = 0; i < titles.Count; ++i) {
      if (this.Find(titles[i]) is not { PrimaryMaximum: 100, PrimaryIsLoad: true } section || !section.Primary.HasValue)
        continue;

      if (section.Primary.Value <= highest)
        continue;

      highest = section.Primary.Value;
      best = i;
    }

    return best;
  }

  /// <summary>
  /// Puts the composition bar under the plots, and takes it away for every resource that has none.
  /// </summary>
  /// <remarks>
  /// It sits between the graphs and the statistics because that is where it is read: the graph says
  /// how much memory is gone, the bar says what it went to, and the numbers underneath give the
  /// exact figures for whichever band prompted the question.
  /// </remarks>
  private void ShowComposition(PerformanceSection chosen) {
    var has = chosen.Composition.HasValue;
    this._composition.Visible = has;
    this._compositionHint.Visible = has;
    if (!has)
      return;

    this._composition.Composition = chosen.Composition;
    this._compositionHint.Text = this._composition.HoverText;
  }

  /// <summary>Keeps the line under the bar in step with the pointer, between sample ticks.</summary>
  private void WatchComposition() =>
    this._composition.MouseMove += (_, _) => {
      if (this._compositionHint.Text != this._composition.HoverText)
        this._compositionHint.Text = this._composition.HoverText;
    };

  #region layout (PRD §45.1)

  /// <summary>
  /// Lays the whole page out for the window's current size.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The statistics are sized first and the graphs get the rest, which is the inversion that fixes
  /// the bug this page shipped with: the plot area was a constant, the columns held whatever fitted
  /// under it, and a memory page with fifteen live figures showed twelve of them and dropped the
  /// last three off the bottom of the window without a mark. Nothing in a screenshot says that a
  /// number that was never drawn is missing.
  /// </para>
  /// <para>
  /// The row count is the largest any resource needs rather than the one on screen, so moving
  /// between resources does not shuffle the numbers up and down the page by thirty pixels — and so
  /// the graphs, which grow horizontally with the window, keep one height across the whole rail
  /// (§45.1).
  /// </para>
  /// </remarks>
  private void ApplyLayout() {
    var client = this.ClientSize;
    if (client.Width < 200 || client.Height < 200)
      return;

    var pitch = this.RowPitch;
    var rows = this._tall ? this.RowCapacity() : this.RowsNeeded();
    var diagnostics = this._diagnostics.Rows;

    // Nothing that decides the geometry has changed, so nothing is moved. This runs on every sample
    // tick as well as on every drag of the window's edge, and assigning bounds to sixty controls a
    // second is how a page somebody is reading acquires a flicker.
    var shape = (client, pitch, rows, diagnostics, this._diagnosticsOpen, this._tall, this._diagnostics.Longest);
    if (shape == this._shape)
      return;

    this._shape = shape;
    this._menu.Bounds = new(0, 0, client.Width, _MenuHeight);

    var top = _MenuHeight + 8;
    var left = _RailWidth + 24;
    var width = Math.Max(240, client.Width - left - 12);
    this._rail.Bounds = new(10, top, _RailWidth, Math.Max(80, client.Height - top - 10));

    this._heading.Bounds = new(left, top, Math.Min(460, width), 26);
    this._model.Bounds = new(left + Math.Min(460, width) + 8, top + 6, Math.Max(80, width - Math.Min(460, width) - 8), 20);

    // The strip that carries §45.4's controls, right-aligned above the graphs: the span, the pause
    // and the inspection view, plus the processor's own switch on the left where it belongs to the
    // page rather than to the graphs.
    var strip = top + 30;
    // The per-core box keeps its full width: at two hundred pixels its own label was drawn as
    // "Per logical processor (16" — a control clipped by the control beside it, which is the same
    // failure as a statistic dropped off the bottom of a column.
    this._perCore.Bounds = new(left, strip, 220, 22);
    this._perNode.Bounds = new(left + 226, strip, 170, 22);
    this._inspect.Bounds = new(left + width - 90, strip, 90, 24);
    this._pause.Bounds = new(left + width - 186, strip, 90, 24);
    // A little taller than the buttons beside it: a drop-down list draws its own frame inside its
    // bounds, and at the buttons' height the text it is showing is clipped by it.
    this._spanBox.Bounds = new(left + width - 306, strip - 1, 114, 27);

    // As many columns as the longest value can live in. The processor's feature lists are sixty
    // characters and a third of the width truncates every one of them to "MMX, SSE, SSE2…", which
    // is a row that costs a line and answers nothing.
    var columns = this.DiagnosticColumnsFor(width);
    var diagnosticRows = this._diagnosticsOpen ? (diagnostics + columns - 1) / columns : 0;

    // Bar, the line that names the band under the pointer, and the gaps around them — reserved
    // whether or not this resource has one, so a page with no bar does not float its numbers thirty
    // pixels higher than the page beside it.
    const int CompositionHeight = 56;
    var statisticsHeight = 22 + (rows * pitch);
    var plotTop = strip + 30;

    // Never more of the block than there is window left under the graphs' floor. Everything below
    // this point would be drawn past the bottom edge, where it is indistinguishable from a figure
    // the machine never reported.
    var spare = client.Height - 12 - plotTop - _MinimumPlotHeight - CompositionHeight - statisticsHeight - 26;
    diagnosticRows = Math.Min(diagnosticRows, Math.Max(0, spare / pitch));
    var diagnosticsHeight = 26 + (diagnosticRows * pitch);
    var plotHeight = Math.Max(
      _MinimumPlotHeight,
      client.Height - 12 - diagnosticsHeight - statisticsHeight - CompositionHeight - plotTop
    );

    this._plotArea = this._tall ? new(left, plotTop, width, 0) : new(left, plotTop, width, plotHeight);
    // The single plot fills the area on its own. Leaving this out is what made every page with one
    // graph — the processor, a disk, an adapter — draw its graph at nought by nought pixels: a page
    // whose whole top half was blank, which the capture log still described as one graph showing.
    this._plot.Bounds = this._plotArea;
    this._composition.Bounds = new(left, this._plotArea.Bottom + 8, width, 26);
    this._compositionHint.Bounds = new(left, this._plotArea.Bottom + 38, width, 16);

    this._statisticsTop = this._tall ? plotTop : this._plotArea.Bottom + CompositionHeight;
    this._rowsPerColumn = rows;
    this._columnWidth = width / 2;

    this._liveHeading.Bounds = new(left, this._statisticsTop, 200, 16);
    this._hardwareHeading.Bounds = new(left + this._columnWidth, this._statisticsTop, 200, 16);
    this.LayOutRows(this._labels, this._values, left, this._statisticsTop + 20, rows, this._columnWidth, 2, pitch);

    var diagnosticsTop = this._statisticsTop + 22 + (rows * pitch);
    this._diagnosticsHeading.Bounds = new(left, diagnosticsTop, 320, 18);
    this.LayOutRows(
      this._diagnosticLabels,
      this._diagnosticValues,
      left,
      diagnosticsTop + 22,
      diagnosticRows,
      width / columns,
      columns,
      pitch
    );
  }

  /// <summary>
  /// Puts a pool of label and value pairs into a grid, column by column.
  /// </summary>
  private void LayOutRows(List<Label> labels, List<Label> values, int left, int top, int rows, int columnWidth, int columns, int pitch) {
    this.EnsurePool(labels, values, rows * columns);
    var narrow = columnWidth < 260;
    for (var i = 0; i < labels.Count; ++i) {
      var column = rows > 0 ? i / rows : 0;
      var row = rows > 0 ? i % rows : 0;
      if (column >= columns || rows == 0) {
        // Beyond the grid: parked off the page rather than drawn on top of something else.
        labels[i].Bounds = new(0, -100, 10, 10);
        values[i].Bounds = new(0, -100, 10, 10);
        continue;
      }

      var x = left + (column * columnWidth);
      var labelWidth = narrow ? 120 : 160;
      // As tall as the pitch rather than two pixels short of it: a nine-point line needs fifteen
      // pixels, and at the compact pitch of sixteen those two pixels are the descenders.
      labels[i].Bounds = new(x, top + (row * pitch), labelWidth, pitch);
      values[i].Bounds = new(x + labelWidth + 5, top + (row * pitch), columnWidth - labelWidth - 14, pitch);
    }
  }

  /// <summary>
  /// Grows a pool of labels to the size the layout wants.
  /// </summary>
  /// <remarks>
  /// Grown and never shrunk, and filled rather than added and removed per tick: a page whose
  /// controls come and go once a second flickers even when nothing on it has changed.
  /// </remarks>
  private void EnsurePool(List<Label> labels, List<Label> values, int wanted) {
    while (labels.Count < wanted) {
      var label = new Label();
      var value = new Label();
      // The value carries the click because it is the half naming the process; the label beside it
      // says "Processor" or nothing at all. Which process, if any, is filled in when the row is
      // written and cleared when it is not, so a pooled control cannot keep the identity of whatever
      // it last displayed (PRD §8.2).
      var slot = values.Count;
      value.Click += (_, _) => this.RowClicked(slot);
      labels.Add(label);
      values.Add(value);
      this.Controls.Add(label);
      this.Controls.Add(value);
    }

    while (this._subjects.Count < wanted)
      this._subjects.Add(default);
  }

  /// <summary>Which process each value label is currently naming, or nothing.</summary>
  private readonly List<ProcessKey> _subjects = [];

  /// <summary>
  /// Somebody asked to see the process a top-five entry names (PRD §51).
  /// </summary>
  /// <remarks>
  /// An event rather than this window reaching into the main one. It is modeless and outlives any
  /// particular selection over there, so it says what was asked for and lets the window that owns
  /// the table decide whether the process is still in it.
  /// </remarks>
  public event EventHandler<ProcessKey>? ProcessChosen;

  private void RowClicked(int slot) {
    if (slot < 0 || slot >= this._subjects.Count)
      return;

    var subject = this._subjects[slot];
    if (subject.Pid != 0)
      this.ProcessChosen?.Invoke(this, subject);
  }

  /// <summary>
  /// How many rows a column has to hold: the most any resource needs, not the one on screen.
  /// </summary>
  private int RowsNeeded() {
    var needed = 1;
    foreach (var section in this._sections) {
      // Only the resources that have a graph. A page that is a list rather than a measurement — the
      // host description, the activity lists — has no plot area to leave room under, and letting its
      // twenty-four rows set the height for everybody would push every graph on the page into a
      // strip and leave half the memory page blank.
      if (!section.HasPrimary || !section.IsTopLevel)
        continue;

      var live = 0;
      var hardware = 0;
      foreach (var row in section.Rows)
        switch (row.Level) {
          case PerformanceRowLevel.Hardware: ++hardware; break;
          case PerformanceRowLevel.Diagnostic: break;
          default: ++live; break;
        }

      needed = Math.Max(needed, Math.Max(live, hardware));
    }

    // Never more than the window can actually show. The cap is a floor under the graphs rather than
    // a fixed number of rows, so a taller window shows more and a short one still draws a graph.
    return Math.Max(4, Math.Min(needed, this.RowCapacity() - ((_MinimumPlotHeight + 56) / this.RowPitch)));
  }

  /// <summary>How many rows fit between the top of the plot area and the bottom of the window.</summary>
  private int RowCapacity()
    => Math.Max(4, (this.ClientSize.Height - _MenuHeight - 8 - 60 - 48) / this.RowPitch);

  /// <summary>How many columns the collapsed block is laid out in, from the longest value in it.</summary>
  private int DiagnosticColumnsFor(int width) {
    if (this._diagnostics.Longest > 44)
      return 1;

    return width >= 900 && this._diagnostics.Longest <= 26 ? 3 : 2;
  }

  /// <summary>How many diagnostic rows one section has, and how long its longest value is.</summary>
  private static (int Rows, int Longest) DiagnosticsOf(PerformanceSection section) {
    var count = 0;
    var longest = 0;
    // default(PerformanceSection) has a null row list rather than an empty one, and a caller that
    // could not find its section hands one over.
    foreach (var row in section.Rows ?? []) {
      if (row.Level != PerformanceRowLevel.Diagnostic)
        continue;

      ++count;
      longest = Math.Max(longest, row.Value.Length);
    }

    return (count, longest);
  }

  #endregion

  /// <summary>A rail entry: what the resource is, what it is doing, and how long it has been.</summary>
  private ResourceRow Entry(string title) {
    foreach (var section in this._sections) {
      if (section.Title != title)
        continue;

      // A section that measures nothing gets no ring and no sparkline. It used to get both, because
      // default(Rate) is a confident zero — so the host description and the activity lists each drew
      // a flat line along the floor of a graph of nothing (PRD §5.3).
      HistoryRing<Rate>? ring = null;
      if (section.HasPrimary)
        this._history.TryGetValue(title, out ring);

      return new(
        section.RailName,
        section.PrimaryLabel,
        section.RailDetail,
        ring,
        section.PrimaryMaximum > 0 ? section.PrimaryMaximum : this.Ceiling(title),
        ColourFor(title),
        title
      );
    }

    return new(title, string.Empty, string.Empty, null, 100, ColourFor(title), title);
  }

  /// <summary>The resource's name, without the readings beside it.</summary>
  private static string NameOf(object? entry) {
    if (entry is ResourceRow row)
      return row.Key;

    var text = entry?.ToString() ?? string.Empty;
    var gap = text.IndexOf("   ", StringComparison.Ordinal);
    return gap < 0 ? text : text[..gap];
  }

  private void ShowSelected(bool force) {
    var index = this._rail.SelectedIndex;
    if ((uint)index >= (uint)this._rail.Items.Count)
      return;

    var title = NameOf(this._rail.Items[index]);
    if (this.Find(title) is not { } chosen)
      return;

    this._tall = chosen.Series.Count == 0;
    this._diagnostics = DiagnosticsOf(chosen);
    this.ApplyLayout();

    var cores = this.PartsOf(title);
    // Only the processor has nodes under it, and only on a machine with more than one — the report
    // builds none otherwise, so this is empty rather than switched off by name here.
    var nodes = this.PartsOf(PerformanceReport.NodeGroup);
    this._perCore.Visible = cores.Count > 0;
    this._perCore.Text = $"Per logical processor ({cores.Count})";
    this._perNode.Visible = cores.Count > 0 && nodes.Count > 0;
    this._perNode.Text = $"Per NUMA node ({nodes.Count})";

    List<PerformanceSection> parts = this._perNode.Visible && this._perNode.Checked ? nodes
      : this._perCore.Visible && this._perCore.Checked ? cores
      : [];

    var split = parts.Count > 0;

    // Three shapes, in order of specificity: the parts when asked for, a resource's own stack of
    // series where it has more than one, and otherwise the single plot.
    //
    // A lone series with a second line goes through the stack as well, because only that path draws
    // companions. An adapter has exactly one graph — receive against send — and through the single
    // plot it drew the sum of the two as one filled area and quietly dropped the second line
    // (PRD §49).
    var series = chosen.Series;
    var stacked = !split && (series.Count > 1 || (series.Count == 1 && series[0].HasCompanion));
    this.LayOutPlots(parts, stacked ? (title, series) : default);
    this._plot.Visible = !split && !stacked && series.Count > 0;
    this.ShowComposition(chosen);

    if (force || !string.Equals(this._shown, title, StringComparison.Ordinal)) {
      this._shown = title;
      // The name without whatever device it names — "Disk", not "Disk — nvme0n1" — because the
      // device is the model, and the model belongs on the right (§45.1).
      var dash = title.IndexOf(" — ", StringComparison.Ordinal);
      this._heading.Text = dash < 0 ? title : title[..dash];
      this._model.Text = ModelOf(chosen, dash < 0 ? string.Empty : title[(dash + 3)..]);

      // One plot whose series is swapped, rather than a plot per resource: the page shows one
      // resource at a time, which is what the rail is for.
      this._plot.ClearSeries();
      this._plot.Caption = title;
      this._plot.AccessibleName = title;
      if (series.Count > 0 && this._history.TryGetValue(title, out var ring))
        this._plot.AddSeries(ring, ColourFor(title), title);

      // Kernel time second, so it draws over the total rather than under it — the reader is looking
      // for how much of a busy core is kernel, which is a comparison and not a sum.
      if (chosen.HasSecondary && this._secondary.TryGetValue(title, out var under))
        this._plot.AddSeries(under, RowPalette.CpuKernel, chosen.SecondaryLabel);
    }

    this._plot.Maximum = chosen.PrimaryMaximum > 0 ? chosen.PrimaryMaximum : this.Ceiling(title);
    this._plot.Unit = series.Count > 0 ? series[0].Unit : PerformanceUnit.Percent;
    this._plot.ScaleLabel = ScaleLabelFor(chosen.PrimaryMaximum, this._plot.Maximum, this._plot.Unit);
    this._plot.Value = chosen.PrimaryLabel;
    this._plot.Invalidate();

    for (var i = 0; i < parts.Count && split && i < this._corePlots.Count; ++i) {
      var plot = this._corePlots[i];
      plot.Value = parts[i].PrimaryLabel;
      plot.Invalidate();
    }

    this.ApplySpan();
    this.ApplyFreeze();
    this.FillColumns(chosen);
  }

  /// <summary>
  /// The live measurements down the left column, the hardware facts down the right, and the
  /// engineering diagnostics in the block underneath (PRD §45.2).
  /// </summary>
  /// <remarks>
  /// Every unused slot is blanked rather than removed, and a figure that does not fit says so: a row
  /// dropped off the bottom of a column is indistinguishable from a machine that never reported it.
  /// </remarks>
  private void FillColumns(PerformanceSection chosen) {
    var perColumn = this._rowsPerColumn;
    this.EnsurePool(this._labels, this._values, perColumn * 2);

    var live = 0;
    var hardware = 0;
    var diagnostics = 0;
    var dropped = 0;

    foreach (var row in chosen.Rows) {
      switch (row.Level) {
        case PerformanceRowLevel.Diagnostic: {
          if (!this._diagnosticsOpen || diagnostics >= this._diagnosticLabels.Count) {
            ++diagnostics;
            continue;
          }

          this._diagnosticLabels[diagnostics].Text = row.Label;
          this._diagnosticValues[diagnostics].Text = row.Value;
          ++diagnostics;
          continue;
        }

        case PerformanceRowLevel.Hardware: {
          if (hardware >= perColumn) {
            ++dropped;
            continue;
          }

          this._labels[perColumn + hardware].Text = row.Label;
          this._values[perColumn + hardware].Text = row.Value;
          this._subjects[perColumn + hardware] = row.Subject;
          ++hardware;
          continue;
        }

        default: {
          if (live >= perColumn) {
            ++dropped;
            continue;
          }

          this._labels[live].Text = row.Label;
          this._values[live].Text = row.Value;
          this._subjects[live] = row.Subject;
          ++live;
          continue;
        }
      }
    }

    for (var i = live; i < perColumn; ++i) {
      this._labels[i].Text = string.Empty;
      this._values[i].Text = string.Empty;
      this._subjects[i] = default;
    }

    for (var i = perColumn + hardware; i < this._labels.Count; ++i) {
      this._labels[i].Text = string.Empty;
      this._values[i].Text = string.Empty;
      this._subjects[i] = default;
    }

    var shownDiagnostics = this._diagnosticsOpen ? Math.Min(diagnostics, this._diagnosticLabels.Count) : 0;
    for (var i = shownDiagnostics; i < this._diagnosticLabels.Count; ++i) {
      this._diagnosticLabels[i].Text = string.Empty;
      this._diagnosticValues[i].Text = string.Empty;
    }

    // The same admission the columns make: a window too short to hold the block says how much of it
    // it is not showing rather than ending in the middle of a list.
    if (shownDiagnostics > 0 && diagnostics > shownDiagnostics)
      this._diagnosticValues[shownDiagnostics - 1].Text += $"   (+{diagnostics - shownDiagnostics} more, resize)";

    this._hardwareHeading.Text = hardware > 0 ? "Hardware" : string.Empty;
    this._liveHeading.Text = live > 0 ? "Live" : string.Empty;
    this._diagnosticsHeading.Visible = diagnostics > 0;

    // A window too short for everything says how much it is not showing. Silently dropping the last
    // three rows of a column is the failure this page had, and it is invisible in a screenshot.
    if (dropped > 0 && live > 0)
      this._values[Math.Min(live, perColumn) - 1].Text += $"   (+{dropped} more, resize)";
  }

  /// <summary>
  /// What the header's right-hand side says: the model where the section names one, and otherwise
  /// whatever came after the dash in the title.
  /// </summary>
  private static string ModelOf(PerformanceSection section, string fallback) {
    foreach (var row in section.Rows)
      if (row.Label is "Model" or "Adapter" && row.Value.Length > 0)
        return row.Value;

    // Memory has no model to give — nothing readable without root says what the sticks are — so the
    // header carries how much there is, which is the next most identifying thing about it.
    foreach (var row in section.Rows)
      if (row.Label is "Total" or "Usable" && row.Value.Length > 0)
        return row.Value;

    return fallback;
  }

  /// <summary>
  /// How the top of a scale reads — <c>100%</c>, <c>16.0G</c>, <c>130.0 W</c> (§45.4).
  /// </summary>
  /// <remarks>
  /// In the series' own unit, and that is the whole point of the parameter: every ceiling that was
  /// not a percentage used to be rendered as a quantity of bytes, so a graphics card's power graph
  /// was labelled "130 B" — a wattage in bytes, in the corner of a plot whose caption said watts
  /// two inches away (PRD §76).
  /// </remarks>
  private static string ScaleLabelFor(double declared, double actual, PerformanceUnit unit) {
    // The one §45.4 spells out, and only for a scale that really is a percentage: a temperature
    // plotted against a fixed hundred degrees is also a fixed scale, and "100%" on it is a unit
    // that graph does not use.
    if (declared == 100 && unit == PerformanceUnit.Percent)
      return "100%";

    var ceiling = Math.Max(0, actual);
    return unit switch {
      PerformanceUnit.Bytes => Humanize.Bytes(Counter.Of((ulong)ceiling)),
      PerformanceUnit.BytesPerSecond => Humanize.BytesPerSecond(Rate.Of(ceiling)),
      PerformanceUnit.Watts => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} W", ceiling),
      PerformanceUnit.Celsius => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0} °C", ceiling),
      PerformanceUnit.Count => Humanize.Count(Counter.Of((ulong)ceiling)),
      _ => Humanize.Percent(Rate.Of(ceiling)) + " %",
    };
  }

  /// <summary>
  /// The top of the scale for a series with no natural ceiling.
  /// </summary>
  /// <remarks>
  /// The largest reading so far with a little headroom, and a floor of 64 KB/s so an idle adapter is
  /// a flat line rather than noise amplified to full height — the same reasoning as the per-process
  /// history scales (PRD §8.2).
  /// <para>
  /// Rounded up to a power of two rather than taken exactly, which is §45.4's hysteresis: an exact
  /// ceiling moves with every sample, and a shape that rescales once a second cannot be read at all.
  /// A scale that only ever changes by doubling stays put through the noise and still follows a real
  /// change within one step.
  /// </para>
  /// </remarks>
  private double Ceiling(string title) {
    if (!this._history.TryGetValue(title, out var ring))
      return 100;

    var highest = 0d;
    for (var i = 0; i < ring.Count; ++i)
      if (ring[i].HasValue)
        highest = Math.Max(highest, ring[i].Value);

    var wanted = Math.Max(highest * 1.15, 64 * 1024);
    var step = 64d * 1024;
    while (step < wanted)
      step *= 2;

    return step;
  }

  /// <summary>The sections that live under a heading — the cores under the processor.</summary>
  private List<PerformanceSection> PartsOf(string title) {
    var parts = new List<PerformanceSection>();
    foreach (var section in this._sections)
      if (string.Equals(section.PartOf, title, StringComparison.Ordinal))
        parts.Add(section);

    return parts;
  }

  /// <summary>
  /// Puts one small plot on screen per part, in a grid, or takes them all away again.
  /// </summary>
  /// <remarks>
  /// The plots are built once and reused, because a core count does not change while a machine is
  /// running — and adding and removing controls on a page somebody is watching would flicker it
  /// once a second even when nothing moved.
  /// <para>
  /// Rows before columns: eight across is about the narrowest a plot can be and still show a shape,
  /// so a machine with more than eight cores gets a second row rather than thinner plots.
  /// </para>
  /// </remarks>
  private void LayOutPlots(List<PerformanceSection> parts, (string Title, IReadOnlyList<PerformanceGraph> Series) stack) {
    if (stack.Series is { Count: > 0 }) {
      this.LayOutStack(stack.Title, stack.Series);
      return;
    }

    this.GrowPlots(parts.Count);
    for (var i = parts.Count; i < this._corePlots.Count; ++i)
      this._corePlots[i].Visible = false;

    if (parts.Count == 0)
      return;

    var columns = Math.Min(8, parts.Count);
    var rows = (parts.Count + columns - 1) / columns;
    var width = this._plotArea.Width / columns;
    var height = this._plotArea.Height / rows;

    for (var i = 0; i < parts.Count; ++i) {
      var plot = this._corePlots[i];
      plot.Bounds = new(
        this._plotArea.X + ((i % columns) * width),
        this._plotArea.Y + ((i / columns) * height),
        width - 2,
        height - 2
      );

      plot.Caption = parts[i].Title;
      plot.AccessibleName = parts[i].Title;
      plot.Maximum = 100;
      plot.Unit = PerformanceUnit.Percent;
      plot.ScaleLabel = string.Empty;
      plot.Visible = true;
      plot.ClearSeries();
      if (this._history.TryGetValue(parts[i].Title, out var ring))
        plot.AddSeries(ring, ColourFor(parts[i].Title), parts[i].Title);

      if (parts[i].HasSecondary && this._secondary.TryGetValue(parts[i].Title, out var under))
        plot.AddSeries(under, RowPalette.CpuKernel, parts[i].SecondaryLabel);
    }
  }

  /// <summary>
  /// A resource's own series, stacked down the plot area.
  /// </summary>
  /// <remarks>
  /// Full width and short rather than side by side: these are several different quantities against
  /// one shared time axis, and stacking them lets the eye read down a moment — the spike in
  /// utilisation and the rise in temperature four seconds later — which side-by-side plots cannot
  /// show (PRD §50.1).
  /// </remarks>
  private void LayOutStack(string title, IReadOnlyList<PerformanceGraph> series) {
    this.GrowPlots(series.Count);
    for (var i = series.Count; i < this._corePlots.Count; ++i)
      this._corePlots[i].Visible = false;

    var height = this._plotArea.Height / series.Count;
    for (var i = 0; i < series.Count; ++i) {
      var plot = this._corePlots[i];
      plot.Bounds = new(this._plotArea.X, this._plotArea.Y + (i * height), this._plotArea.Width, height - 2);
      var graph = series[i];
      plot.Caption = graph.Label;
      plot.AccessibleName = $"{title} — {graph.Label}";
      plot.Value = graph.ValueLabel;
      // Both lines share one scale, so the ceiling is the higher of the two: an adapter receiving a
      // hundred times what it sends would otherwise draw its send line off the top of the plot.
      plot.Maximum = graph.Maximum > 0
        ? graph.Maximum
        : graph.HasCompanion
          ? Math.Max(this.Ceiling(SeriesKey(title, graph.Label)), this.Ceiling(CompanionKey(title, graph.Label)))
          : this.Ceiling(SeriesKey(title, graph.Label));

      plot.Unit = graph.Unit;
      plot.ScaleLabel = ScaleLabelFor(graph.Maximum == 100 ? 100 : 0, plot.Maximum, graph.Unit);
      plot.Visible = true;
      plot.ClearSeries();
      var accent = AccentFor(graph.Accent);
      plot.AddSeries(this.Ring(SeriesKey(title, graph.Label)), accent, graph.FirstLabel);
      // The second line in a lighter shade of the same accent, not in another hue: they are two
      // halves of one quantity, and §45.5 gives the whole resource one colour. Stroked over the
      // first one's fill rather than filled beside it — two areas on one axis hide each other, and
      // two lines in two shades of one colour are a pair nobody can tell apart at one pixel wide.
      if (graph.HasCompanion)
        plot.AddSeries(
          this.Ring(CompanionKey(title, graph.Label)),
          SeriesPainter.Lighten(accent, 110),
          graph.CompanionLabel,
          filled: false
        );

      plot.Invalidate();
    }
  }

  private void GrowPlots(int wanted) {
    while (this._corePlots.Count < wanted) {
      var plot = new HistoryPlot { Visible = false };
      plot.Expanded += (_, _) => this.Inspect();
      this._corePlots.Add(plot);
      this.Controls.Add(plot);
    }
  }

  /// <summary>Each kind of series keeps its own colour, so a page of six says six things.</summary>
  private static Color AccentFor(string accent) => accent switch {
    "cpu" => RowPalette.Cpu,
    "memory" => RowPalette.Memory,
    "io" => RowPalette.Io,
    "temperature" => RowPalette.CpuKernel,
    "fan" => Color.FromArgb(0xC8, 0x5A, 0xC8),
    "power" => Color.FromArgb(0x9A, 0xD8, 0x30),
    "gpu" => Color.FromArgb(0x30, 0xC0, 0xB0),
    "network" => Color.FromArgb(0xE0, 0x8C, 0x2C),
    _ => Color.FromArgb(0x9A, 0x5F, 0xB8),
  };

  private PerformanceSection? Find(string title) {
    foreach (var section in this._sections)
      if (section.Title == title)
        return section;

    return null;
  }

  /// <summary>
  /// A resource's own colour, used by its sparkline and by its graph alike.
  /// </summary>
  /// <remarks>
  /// One accent per resource across the whole window is what lets the eye follow one thing from the
  /// rail to the plot (§45.5). The two used to be worked out separately and disagreed: a GPU's rail
  /// sparkline was orange and its graphs teal, which reads as two different resources.
  /// <para>
  /// A core keeps the processor's own colour. The reader is comparing one core with the whole
  /// machine, and a different hue per core would say those are different kinds of thing.
  /// </para>
  /// </remarks>
  private static Color ColourFor(string title) => AccentFor(title switch {
    "Processor" => "cpu",
    "Memory" => "memory",
    // A core and a NUMA node are both the processor divided up, and both keep its colour: a
    // different hue would say they are a different kind of thing from the machine they are part of.
    _ when title.StartsWith("Core ", StringComparison.Ordinal) => "cpu",
    _ when title.StartsWith("Node ", StringComparison.Ordinal) => "cpu",
    _ when title.StartsWith("Disk", StringComparison.Ordinal) => "io",
    _ when title.StartsWith("GPU", StringComparison.Ordinal) => "gpu",
    _ => "network",
  });

}

/// <summary>
/// What a graph did over its span: current, minimum, maximum and average (PRD §45.4).
/// </summary>
/// <remarks>
/// A window rather than a tooltip, because it is the answer to a deliberate question — a
/// double-click, or Expand — and because somebody comparing two moments wants it to stay put while
/// they look at the graph behind it.
/// </remarks>
internal sealed class InspectionWindow : Form {

  public InspectionWindow(string title, string text) {
    this.Text = $"{title} — statistics";
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 560, 320);
    this.Controls.Add(new Label { Bounds = new(14, 12, 530, 290), Text = text });
  }

}
