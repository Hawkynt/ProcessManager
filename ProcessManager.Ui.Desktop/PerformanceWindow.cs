using System.Drawing;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

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
  private const int _Rows = 24;

  /// <summary>
  /// The plot area at its shortest: one graph.
  /// </summary>
  /// <remarks>
  /// A stack takes more, and the statistics move down to make room — five graphs sharing two hundred
  /// pixels are forty pixels each, which is a line and not a shape. The area grows to
  /// <see cref="_TallPlotHeight"/> and the columns follow it.
  /// </remarks>
  private static readonly Rectangle _PlotArea = new(0, 36, 880, 210);

  private const int _TallPlotHeight = 430;

  private const int _ColumnWidth = 380;

  private readonly ISystemProbe _probe;
  private readonly Sampler _sampler;
  private readonly ResourceRail _rail = new();
  private readonly HistoryPlot _plot = new();
  private readonly CheckBox _perCore = new() { Text = "Per logical processor" };

  /// <summary>One small plot per core, built when the core count is first known.</summary>
  private readonly List<HistoryPlot> _corePlots = [];
  private readonly Label _heading = new();

  /// <summary>The hardware this page is about, top right — §45.1's header.</summary>
  private readonly Label _model = new();

  private readonly Label _liveHeading = new() { Text = "Live" };
  private readonly Label _hardwareHeading = new() { Text = "Hardware" };
  private readonly List<Label> _labels = [];
  private readonly List<Label> _values = [];

  /// <summary>One ring per resource, keyed by section title and added to as devices appear.</summary>
  private readonly Dictionary<string, HistoryRing<Rate>> _history = new(StringComparer.Ordinal);

  /// <summary>The second series where a resource has one: kernel time under total CPU (PRD §46).</summary>
  private readonly Dictionary<string, HistoryRing<Rate>> _secondary = new(StringComparer.Ordinal);

  private IReadOnlyList<PerformanceSection> _sections = [];
  private string _shown = string.Empty;
  private int _statisticsTop = -1;

  public PerformanceWindow(ISystemProbe probe, Sampler sampler) {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(sampler);

    this._probe = probe;
    this._sampler = sampler;

    this.Text = "System information";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;
    // §45.1's reference size, near enough: the rail plus a plot area wide enough that a minute of
    // history is a minute of pixels.
    this.Bounds = new(0, 0, 1180, 780);

    this._rail.Bounds = new(10, 10, _RailWidth, 730);
    this._rail.SelectedIndexChanged += (_, _) => this.ShowSelected(force: true);
    this.Controls.Add(this._rail);

    var right = _RailWidth + 24;
    this._heading.Bounds = new(right, 10, 300, 22);
    this.Controls.Add(this._heading);

    // The model to the right of the name, which is where §45.1 puts it: what this is, then what it
    // actually is. Right-aligned so a long part number grows away from the name rather than into it.
    this._model.Bounds = new(right + 310, 12, 560, 20);
    this._model.TextAlign = ContentAlignment.TopRight;
    this.Controls.Add(this._model);

    this._plot.Bounds = _PlotArea with { X = right };
    this._liveHeading.Bounds = new(right, _PlotArea.Bottom + 34, 200, 16);
    this.Controls.Add(this._plot);

    // The processor's two views, as one box rather than as twenty rail entries: a machine with
    // twenty cores would bury the disks under them, and the question "overall or per core" is one
    // switch and not twenty destinations (PRD §46).
    this._perCore.Bounds = new(right, _PlotArea.Bottom + 6, 220, 20);
    this._perCore.CheckedChanged += (_, _) => this.ShowSelected(force: true);
    this._perCore.Visible = false;
    this.Controls.Add(this._perCore);

    // Two columns, because the live measurements and the hardware facts answer different questions
    // and reading them as one list is what makes a performance page look like a data dump (§45.1).
    this.Controls.Add(this._liveHeading);
    this.Controls.Add(this._hardwareHeading);

    // Built once at the widest a section gets, then filled and blanked. Adding and removing controls
    // every tick would make a page somebody watches for a minute flicker once a second.
    for (var i = 0; i < _Rows; ++i) {
      var label = new Label();
      var value = new Label();
      this._labels.Add(label);
      this._values.Add(value);
      this.Controls.Add(label);
      this.Controls.Add(value);
    }

    this.LayOutStatistics(_PlotArea.Bottom);

    this.UpdateFromSample();
  }

  /// <summary>
  /// What the page is showing, in text — the capture run's evidence that §45's layout survived.
  /// </summary>
  public string DescribeForCapture() {
    var builder = new System.Text.StringBuilder();
    builder.AppendLine($"page rail:    {this._rail.Items.Count} resources, showing '{this._shown}'");
    builder.AppendLine($"page header:  {this._heading.Text} / {this._model.Text}");

    var live = 0;
    var hardware = 0;
    for (var i = 0; i < this._labels.Count; ++i) {
      if (this._labels[i].Text.Length == 0)
        continue;

      if (i < _Rows / 2)
        ++live;
      else
        ++hardware;
    }

    builder.AppendLine($"page stats:   {live} live, {hardware} hardware");
    return builder.ToString();
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
      this._probe.DescribeGpus
    );

    this.RecordHistory();
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
      if (!Plottable(section))
        continue;

      // One ring per series, so a GPU's six move independently and a disk's transfer rate is not
      // overwritten by its active time.
      foreach (var graph in section.Series)
        this.Ring(SeriesKey(section.Title, graph.Label)).Add(graph.Value);

      if (!this._history.TryGetValue(section.Title, out var ring)) {
        ring = new(600);
        this._history[section.Title] = ring;
      }

      ring.Add(section.Primary);

      if (!section.HasSecondary)
        continue;

      if (!this._secondary.TryGetValue(section.Title, out var under)) {
        under = new(600);
        this._secondary[section.Title] = under;
      }

      under.Add(section.Secondary);
    }
  }

  /// <summary>The ring for one series, made the first time it is asked for.</summary>
  private HistoryRing<Rate> Ring(string key) {
    if (this._history.TryGetValue(key, out var ring))
      return ring;

    ring = new(600);
    this._history[key] = ring;
    return ring;
  }

  /// <summary>
  /// A series is identified by its section and its label together, with a separator no label can
  /// contain — "Temperature" belongs to a GPU, and two GPUs each have one.
  /// </summary>
  private static string SeriesKey(string section, string label) => $"{section}\u0000{label}";

  private static bool Plottable(PerformanceSection section)
    => section.PrimaryMaximum > 0 || section.Primary.HasValue;

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
      if (section.IsTopLevel && (Plottable(section) || section.Title == "System"))
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
        : selected < 0 ? this.BusiestOf(wanted) : Math.Clamp(selected, 0, this._rail.Items.Count - 1);

      return;
    }

    for (var i = 0; i < wanted.Count; ++i)
      this._rail.Items[i] = this.Entry(wanted[i]);
  }

  /// <summary>
  /// Which resource to open on: whatever is under the greatest load (PRD §45.3).
  /// </summary>
  /// <remarks>
  /// Only the ones on a fixed 0–100 scale are compared, because those are the only ones whose
  /// numbers mean the same thing. A network adapter's headline is bytes per second, and eleven
  /// thousand of those is not busier than a processor at eleven percent — it is a different quantity
  /// wearing a larger number.
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
      if (this.Find(titles[i]) is not { PrimaryMaximum: 100 } section || !section.Primary.HasValue)
        continue;

      if (section.Primary.Value <= highest)
        continue;

      highest = section.Primary.Value;
      best = i;
    }

    return best;
  }

  /// <summary>
  /// Puts the two statistic columns under whatever height the plots ended up taking.
  /// </summary>
  private void LayOutStatistics(int plotBottom) {
    if (this._statisticsTop == plotBottom)
      return;

    this._statisticsTop = plotBottom;
    var right = _RailWidth + 24;
    var top = plotBottom + 34;

    this._liveHeading.Bounds = new(right, top, 200, 16);
    this._hardwareHeading.Bounds = new(right + _ColumnWidth, top, 200, 16);

    var perColumn = _Rows / 2;
    for (var i = 0; i < this._labels.Count; ++i) {
      var column = i >= perColumn ? _ColumnWidth : 0;
      var row = i % perColumn;
      this._labels[i].Bounds = new(right + column, top + 22 + (row * 18), 160, 16);
      this._values[i].Bounds = new(right + column + 165, top + 22 + (row * 18), 200, 16);
    }
  }

  /// <summary>A rail entry: what the resource is, what it is doing, and how long it has been.</summary>
  private ResourceRow Entry(string title) {
    foreach (var section in this._sections) {
      if (section.Title != title)
        continue;

      this._history.TryGetValue(title, out var ring);
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

    var parts = this.PartsOf(title);
    this._perCore.Visible = parts.Count > 0;
    this._perCore.Text = $"Per logical processor ({parts.Count})";
    var split = this._perCore.Visible && this._perCore.Checked;

    // Three shapes, in order of specificity: the cores when asked for, a resource's own stack of
    // series where it has more than one, and otherwise the single plot.
    var series = chosen.Series;
    var stacked = !split && series.Count > 1;
    this.LayOutPlots(split ? parts : [], stacked ? (title, series) : default);
    this._plot.Visible = !split && !stacked;

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
      if (this._history.TryGetValue(title, out var ring))
        this._plot.AddSeries(ring, ColourFor(title), title);

      // Kernel time second, so it draws over the total rather than under it — the reader is looking
      // for how much of a busy core is kernel, which is a comparison and not a sum.
      if (chosen.HasSecondary && this._secondary.TryGetValue(title, out var under))
        this._plot.AddSeries(under, RowPalette.CpuKernel, chosen.SecondaryLabel);
    }

    this._plot.Maximum = chosen.PrimaryMaximum > 0 ? chosen.PrimaryMaximum : this.Ceiling(title);
    this._plot.Value = chosen.PrimaryLabel;
    this._plot.Invalidate();

    for (var i = 0; i < parts.Count && split && i < this._corePlots.Count; ++i) {
      var plot = this._corePlots[i];
      plot.Value = parts[i].PrimaryLabel;
      plot.Invalidate();
    }

    this.FillColumns(chosen);
  }

  /// <summary>
  /// The live measurements down the left column and the hardware facts down the right.
  /// </summary>
  /// <remarks>
  /// Both columns are as long as the page has slots for, and every unused slot is blanked rather
  /// than removed: a page whose controls come and go once a second flickers even when nothing on it
  /// has changed.
  /// </remarks>
  private void FillColumns(PerformanceSection chosen) {
    var perColumn = _Rows / 2;
    var live = 0;
    var hardware = 0;

    foreach (var row in chosen.Rows) {
      var slot = row.IsHardware
        ? hardware < perColumn ? perColumn + hardware++ : -1
        : live < perColumn ? live++ : -1;

      if (slot < 0)
        continue;

      this._labels[slot].Text = row.Label;
      this._values[slot].Text = row.Value;
    }

    for (var i = live; i < perColumn; ++i) {
      this._labels[i].Text = string.Empty;
      this._values[i].Text = string.Empty;
    }

    for (var i = perColumn + hardware; i < this._labels.Count; ++i) {
      this._labels[i].Text = string.Empty;
      this._values[i].Text = string.Empty;
    }

    this._hardwareHeading.Text = hardware > 0 ? "Hardware" : string.Empty;
  }

  /// <summary>
  /// What the header's right-hand side says: the model where the section names one, and otherwise
  /// whatever came after the dash in the title.
  /// </summary>
  private static string ModelOf(PerformanceSection section, string fallback) {
    foreach (var row in section.Rows)
      if (row.Label is "Model" or "Adapter" && row.Value.Length > 0)
        return row.Value;

    return fallback;
  }

  /// <summary>
  /// The top of the scale for a series with no natural ceiling.
  /// </summary>
  /// <remarks>
  /// The largest reading so far with a little headroom, and a floor of 64 KB/s so an idle adapter is
  /// a flat line rather than noise amplified to full height — the same reasoning as the per-process
  /// history scales (PRD §8.2).
  /// </remarks>
  private double Ceiling(string title) {
    if (!this._history.TryGetValue(title, out var ring))
      return 100;

    var highest = 0d;
    for (var i = 0; i < ring.Count; ++i)
      if (ring[i].HasValue)
        highest = Math.Max(highest, ring[i].Value);

    return Math.Max(highest * 1.15, 64 * 1024);
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

    this.LayOutStatistics(_PlotArea.Bottom);
    while (this._corePlots.Count < parts.Count) {
      var plot = new HistoryPlot { Visible = false };
      this._corePlots.Add(plot);
      this.Controls.Add(plot);
    }

    for (var i = parts.Count; i < this._corePlots.Count; ++i)
      this._corePlots[i].Visible = false;

    if (parts.Count == 0)
      return;

    var columns = Math.Min(8, parts.Count);
    var rows = (parts.Count + columns - 1) / columns;
    var area = _PlotArea with { X = _RailWidth + 24 };
    var width = area.Width / columns;
    var height = area.Height / rows;

    for (var i = 0; i < parts.Count; ++i) {
      var plot = this._corePlots[i];
      plot.Bounds = new(
        area.X + ((i % columns) * width),
        area.Y + ((i / columns) * height),
        width - 2,
        height - 2
      );

      plot.Caption = parts[i].Title;
      plot.Maximum = 100;
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
  /// Full width and short rather than side by side: these are six different quantities against one
  /// shared time axis, and stacking them lets the eye read down a moment — the spike in utilisation
  /// and the rise in temperature four seconds later — which side-by-side plots cannot show
  /// (PRD §50.1).
  /// </remarks>
  private void LayOutStack(string title, IReadOnlyList<PerformanceGraph> series) {
    while (this._corePlots.Count < series.Count) {
      var plot = new HistoryPlot { Visible = false };
      this._corePlots.Add(plot);
      this.Controls.Add(plot);
    }

    for (var i = series.Count; i < this._corePlots.Count; ++i)
      this._corePlots[i].Visible = false;

    // A stack gets the taller area: five graphs in two hundred pixels are forty pixels each, which
    // is a line rather than a shape.
    var area = _PlotArea with { X = _RailWidth + 24, Height = _TallPlotHeight };
    var height = area.Height / series.Count;
    for (var i = 0; i < series.Count; ++i) {
      var plot = this._corePlots[i];
      plot.Bounds = new(area.X, area.Y + (i * height), area.Width, height - 2);
      plot.Caption = series[i].Label;
      plot.Value = series[i].ValueLabel;
      plot.Maximum = series[i].Maximum > 0 ? series[i].Maximum : this.Ceiling(SeriesKey(title, series[i].Label));
      plot.Visible = true;
      plot.ClearSeries();
      plot.AddSeries(this.Ring(SeriesKey(title, series[i].Label)), AccentFor(series[i].Accent), series[i].Label);
      plot.Invalidate();
    }

    this.LayOutStatistics(area.Bottom);
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
    _ when title.StartsWith("Core ", StringComparison.Ordinal) => "cpu",
    _ when title.StartsWith("Disk", StringComparison.Ordinal) => "io",
    _ when title.StartsWith("GPU", StringComparison.Ordinal) => "gpu",
    _ => "network",
  });

}
