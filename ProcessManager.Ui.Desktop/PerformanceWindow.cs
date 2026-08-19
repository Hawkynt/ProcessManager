using System.Drawing;
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

  private readonly ISystemProbe _probe;
  private readonly Sampler _sampler;
  private readonly ListBox _rail = new();
  private readonly HistoryPlot _plot = new();
  private readonly Label _heading = new();
  private readonly List<Label> _labels = [];
  private readonly List<Label> _values = [];

  /// <summary>One ring per resource, keyed by section title and added to as devices appear.</summary>
  private readonly Dictionary<string, HistoryRing<Rate>> _history = new(StringComparer.Ordinal);

  private IReadOnlyList<PerformanceSection> _sections = [];
  private string _shown = string.Empty;

  public PerformanceWindow(ISystemProbe probe, Sampler sampler) {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(sampler);

    this._probe = probe;
    this._sampler = sampler;

    this.Text = "System information";
    this.Bounds = new(0, 0, 940, 700);

    this._rail.Bounds = new(10, 10, _RailWidth, 650);
    this._rail.SelectedIndexChanged += (_, _) => this.ShowSelected(force: true);
    this.Controls.Add(this._rail);

    var right = _RailWidth + 24;
    this._heading.Bounds = new(right, 12, 660, 20);
    this.Controls.Add(this._heading);

    this._plot.Bounds = new(right, 36, 660, 200);
    this.Controls.Add(this._plot);

    // Built once at the widest a section gets, then filled and blanked. Adding and removing controls
    // every tick would make a page somebody watches for a minute flicker once a second.
    for (var i = 0; i < _Rows; ++i) {
      var label = new Label { Bounds = new(right, 250 + (i * 18), 200, 16) };
      var value = new Label { Bounds = new(right + 210, 250 + (i * 18), 450, 16) };
      this._labels.Add(label);
      this._values.Add(value);
      this.Controls.Add(label);
      this.Controls.Add(value);
    }

    this.UpdateFromSample();
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
      this._probe.DescribeInterface
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

      if (!this._history.TryGetValue(section.Title, out var ring)) {
        ring = new(600);
        this._history[section.Title] = ring;
      }

      ring.Add(section.Primary);
    }
  }

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
      if (Plottable(section) || section.Title == "System")
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
        : Math.Clamp(selected < 0 ? 0 : selected, 0, this._rail.Items.Count - 1);

      return;
    }

    for (var i = 0; i < wanted.Count; ++i)
      this._rail.Items[i] = this.Entry(wanted[i]);
  }

  /// <summary>A rail entry: what the resource is, and what it is doing right now.</summary>
  private string Entry(string title) {
    foreach (var section in this._sections)
      if (section.Title == title)
        return section.PrimaryLabel.Length > 0 ? $"{title}   {section.PrimaryLabel}" : title;

    return title;
  }

  /// <summary>The resource's name, without the reading appended to it.</summary>
  private static string NameOf(object? entry) {
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

    if (force || !string.Equals(this._shown, title, StringComparison.Ordinal)) {
      this._shown = title;
      this._heading.Text = title;

      // One plot whose series is swapped, rather than a plot per resource: the page shows one
      // resource at a time, which is what the rail is for.
      this._plot.ClearSeries();
      this._plot.Caption = title;
      if (this._history.TryGetValue(title, out var ring))
        this._plot.AddSeries(ring, ColourFor(title), title);
    }

    this._plot.Maximum = chosen.PrimaryMaximum > 0 ? chosen.PrimaryMaximum : this.Ceiling(title);
    this._plot.Value = chosen.PrimaryLabel;
    this._plot.Invalidate();

    for (var i = 0; i < this._labels.Count; ++i) {
      var has = i < chosen.Rows.Count;
      this._labels[i].Text = has ? chosen.Rows[i].Label : string.Empty;
      this._values[i].Text = has ? chosen.Rows[i].Value : string.Empty;
    }
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

  private PerformanceSection? Find(string title) {
    foreach (var section in this._sections)
      if (section.Title == title)
        return section;

    return null;
  }

  /// <summary>Each kind of resource keeps its own colour, so the plot says what it is.</summary>
  private static Color ColourFor(string title) => title switch {
    "Processor" => Color.FromArgb(0x2E, 0x8B, 0x57),
    "Memory" => Color.FromArgb(0x46, 0x82, 0xB4),
    _ when title.StartsWith("Disk", StringComparison.Ordinal) => Color.FromArgb(0xB8, 0x86, 0x0B),
    _ => Color.FromArgb(0x9A, 0x5F, 0xB8),
  };

}
