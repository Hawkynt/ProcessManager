using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// A scrolling time plot of one or more series, drawn as an instrument rather than as a chart.
/// </summary>
/// <remarks>
/// NativeForms has no plotting control, so this is ours (PRD §7.2). What matters:
/// <list type="bullet">
/// <item>It reads a <see cref="HistoryRing{T}"/> of <see cref="Rate"/> directly, so nothing is copied
/// or projected per frame.</item>
/// <item>A <see cref="Rate"/> without a value breaks the line rather than being drawn as zero. A gap
/// in sampling is not a quiet second, and a plot that draws it as one is lying (PRD §3.3).</item>
/// <item>The horizontal axis is time and not sample count, so the axis labels — "60 seconds ago" and
/// "Now" — say something true about the pixels under them (PRD §45.4).</item>
/// </list>
/// </remarks>
public sealed class HistoryPlot : OwnerDrawnControl {

  private readonly List<Series> _series = [];

  /// <param name="Filled">
  /// Whether this series is drawn as an area or as a line, where it differs from the plot's own
  /// setting. Per series and not per plot, because the pair a two-line graph draws is most legible
  /// as one of each: the area is the direction that dominates and the line over it is the other,
  /// which two lines in two shades of one accent are not (PRD §48, §49).
  /// </param>
  private sealed record Series(HistoryRing<Rate> Values, Color Color, string Label, bool? Filled = null);

  public HistoryPlot() =>
    // Not OnDoubleClick: Control.DoubleClick is raised by PerformClick and never by the pointer, so
    // a handler on it is a gesture that quietly does nothing.
    this.MouseDoubleClick += (_, _) => this.Expanded?.Invoke(this, EventArgs.Empty);

  /// <summary>
  /// Focusable, unlike most owner-drawn controls, so that Tab reaches every graph and the arrow keys
  /// can walk its cursor — §45.9's requirement that keyboard navigation reach every graph control.
  /// </summary>
  protected override bool Focusable => true;

  /// <summary>Where the pointer is, in pixels from the left, or -1 for nowhere.</summary>
  private int _hoverX = -1;

  /// <summary>
  /// Fill the area under each series instead of stroking its outline.
  /// </summary>
  /// <remarks>
  /// On by default because it is what the reference tools do and it is easier to read: at a hundred
  /// pixels tall a filled area shows its shape at a glance where a one-pixel line has to be traced.
  /// A stroked plot is still the better choice when several series overlap heavily, which is why
  /// this is a property and not a decision.
  /// </remarks>
  public bool Filled { get; set; } = true;

  /// <summary>The reading to print in the corner, over the plot. Empty for none.</summary>
  public string Value { get; set; } = string.Empty;

  /// <summary>The top of the scale. 100 for percentages; set higher for rates.</summary>
  public double Maximum { get; set; } = 100;

  /// <summary>
  /// What one sample is measured in, for the readings under the cursor.
  /// </summary>
  /// <remarks>
  /// Told rather than inferred from the scale. A plot whose ceiling is not a hundred could be
  /// holding bytes, bytes a second, degrees or watts, and rendering a disk's 1.2 MB/s as "1.2M" is
  /// a small lie of exactly the kind this program exists not to tell (PRD §76).
  /// </remarks>
  public PerformanceUnit Unit { get; set; } = PerformanceUnit.Percent;

  /// <summary>
  /// How the top of the scale reads — <c>100%</c>, <c>16 GB</c> (PRD §45.4).
  /// </summary>
  /// <remarks>
  /// In the corner because a filled area at two thirds of the height means nothing at all until the
  /// reader knows two thirds of what.
  /// </remarks>
  public string ScaleLabel { get; set; } = string.Empty;

  /// <summary>Drawn in the top-left corner, so a wall of plots is readable without a legend.</summary>
  /// <remarks>
  /// It is painted rather than written as text, so nothing announces it: a plot has no <c>Text</c>
  /// for the toolkit to fall back on, and a wall of them reads as "graph, graph, graph". Setting the
  /// caption therefore names the control too, unless a caller has already named it something better
  /// (PRD §74).
  /// </remarks>
  public string Caption {
    get;
    set {
      field = value;
      if (string.IsNullOrEmpty(this.AccessibleName))
        this.AccessibleName = value;
    }
  } = string.Empty;

  /// <summary>How many seconds the width covers (PRD §45.4).</summary>
  public int SpanSeconds { get; set; } = 60;

  /// <summary>How far apart the samples are, which is what turns the span into a sample count.</summary>
  public double SecondsPerSample { get; set; } = 1;

  /// <summary>
  /// How many of the newest samples to leave undrawn — what "paused" is made of.
  /// </summary>
  /// <remarks>
  /// Pause freezes the drawing without clearing the history or stopping collection (PRD §45.4), so
  /// it is counted in samples rather than remembered as an index: a ring that wraps while somebody
  /// is reading a paused plot would leave an index pointing at a different second than the one they
  /// paused on, and the plot would drift without ever looking wrong.
  /// </remarks>
  public int SkipNewest { get; set; }

  /// <summary>Whether to say so, which a frozen plot must, or it is simply a broken one.</summary>
  public bool Paused { get; set; }

  /// <summary>Raised on a double-click, which §45.4 makes the gesture for the inspection view.</summary>
  public event EventHandler? Expanded;

  /// <summary>
  /// Drops every series.
  /// </summary>
  /// <remarks>
  /// For a plot whose subject changes — the performance page swaps one series for another as the
  /// selection moves, rather than building a plot per resource.
  /// </remarks>
  public void ClearSeries() => this._series.Clear();

  /// <summary>
  /// The colour of each series, in the order they were added.
  /// </summary>
  /// <remarks>
  /// So a caller — and a test — can ask what a plot is actually drawing. §45.5 requires a resource's
  /// sparkline and its graph to be the same colour, and the two are worked out in different places;
  /// nothing but reading the plot back can check that they agree.
  /// </remarks>
  public IReadOnlyList<Color> SeriesColours {
    get {
      var colours = new Color[this._series.Count];
      for (var i = 0; i < colours.Length; ++i)
        colours[i] = this._series[i].Color;

      return colours;
    }
  }

  /// <param name="filled">
  /// Null follows <see cref="Filled"/>, which is what nearly every series wants; false strokes this
  /// one over whatever is under it.
  /// </param>
  public void AddSeries(HistoryRing<Rate> values, Color color, string label = "", bool? filled = null) {
    ArgumentNullException.ThrowIfNull(values);
    this._series.Add(new(values, color, label, filled));
  }

  /// <summary>How many samples the axis is wide.</summary>
  private int Samples => Math.Max(1, (int)Math.Round(this.SpanSeconds / Math.Max(0.05, this.SecondsPerSample)));

  #region what the pointer is over (PRD §45.4)

  /// <summary>
  /// The timestamp and readings under the pointer, or empty when it is over nothing.
  /// </summary>
  /// <remarks>
  /// Relative rather than a wall clock: "12 s ago" is what somebody watching a scrolling graph
  /// actually wants, and a plot that has been paused for a minute would otherwise label its newest
  /// sample with a time that has since passed.
  /// </remarks>
  public string HoverText { get; private set; } = string.Empty;

  /// <summary>
  /// Raised whenever the cursor lands somewhere new, however it got there (PRD §28).
  /// </summary>
  /// <remarks>
  /// One event for the pointer and the arrow keys alike, because the page that echoes the readings
  /// into its footer must follow both. It hung off <c>MouseMove</c> at first, which left the
  /// keyboard drawing its cursor on the plot while the footer went on reporting wherever the mouse
  /// had last been — a reading beside the wrong moment, which is worse than no reading (PRD §45.9).
  /// </remarks>
  public event EventHandler? CursorMoved;

  /// <summary>
  /// Puts the cursor at a pixel from the left — what a mouse move does, without a mouse.
  /// </summary>
  /// <remarks>
  /// Public so the gesture is testable at all: <c>OnMouseMove</c> is raised by a realized canvas and
  /// nothing else, so a window-less test can otherwise say nothing about what a reader sees when
  /// they point at a graph (PRD §9.6).
  /// </remarks>
  public void PointAt(int x) {
    var was = this._hoverX;
    this._hoverX = x;
    this.UpdateHoverText();
    this.CursorMoved?.Invoke(this, EventArgs.Empty);
    if (was != this._hoverX)
      this.Invalidate();
  }

  /// <summary>
  /// Walks the cursor along the axis — what an arrow key does. Starts at the newest sample.
  /// </summary>
  public void MoveCursor(int step) {
    if (this.Width < 2)
      return;

    this.PointAt(this._hoverX < 0 ? this.Width - 1 : Math.Clamp(this._hoverX + step, 0, this.Width - 1));
  }

  /// <summary>Takes the cursor off the plot, which is what leaving it does.</summary>
  public void ClearCursor() {
    if (this._hoverX < 0)
      return;

    this._hoverX = -1;
    this.HoverText = string.Empty;
    this.CursorMoved?.Invoke(this, EventArgs.Empty);
    this.Invalidate();
  }

  protected override void OnMouseMove(MouseEventArgs e) => this.PointAt(e.X);

  protected override void OnMouseLeave(EventArgs e) => this.ClearCursor();

  /// <summary>
  /// Arrow keys walk the cursor along the axis, so the tooltip is reachable without a mouse
  /// (PRD §45.9).
  /// </summary>
  protected override void OnKeyDown(KeyEventArgs e) {
    var step = e.KeyCode switch {
      Keys.Left => -1,
      Keys.Right => 1,
      _ => 0,
    };

    if (step == 0)
      return;

    this.MoveCursor(step);
    e.Handled = true;
  }

  private void UpdateHoverText() {
    if (this._hoverX < 0 || this._series.Count == 0 || this.Width < 2) {
      this.HoverText = string.Empty;
      return;
    }

    var perSample = this.Width / (double)this.Samples;
    var age = (int)Math.Round((this.Width - 1 - this._hoverX) / perSample);
    var text = new System.Text.StringBuilder();
    text.Append(age <= 0 ? "now" : $"{age * this.SecondsPerSample:0.#} s ago");

    foreach (var series in this._series) {
      var count = Math.Max(0, series.Values.Count - this.SkipNewest);
      var index = count - 1 - age;
      var label = series.Label.Length > 0 ? series.Label + " " : string.Empty;
      // Outside the history the plot has is not a reading of zero — it is a part of the axis this
      // machine has not been running long enough to fill.
      text.Append(index < 0 || index >= series.Values.Count
        ? $"  ·  {label}{Humanize.Placeholder(UnknownReason.NotSampledYet)}"
        : $"  ·  {label}{this.Reading(series.Values[index])}");
    }

    this.HoverText = text.ToString();
  }

  /// <summary>One sample, in the unit the series was declared in.</summary>
  private string Reading(Rate value) {
    if (!value.HasValue)
      return Humanize.Placeholder(value.Reason);

    return this.Unit switch {
      PerformanceUnit.Bytes => Humanize.Bytes(Counter.Of((ulong)Math.Max(0, value.Value))),
      PerformanceUnit.BytesPerSecond => Humanize.BytesPerSecond(value),
      PerformanceUnit.Celsius => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} °C", value.Value),
      PerformanceUnit.Watts => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} W", value.Value),
      // A count is a whole number of things and reads as one. Falling through to the percentage
      // branch below would have printed a thread count as "42 %".
      PerformanceUnit.Count => Humanize.Count(Counter.Of((ulong)Math.Max(0, value.Value))),
      _ => Humanize.Percent(value) + " %",
    };
  }

  #endregion

  /// <summary>Current, minimum, maximum and average over the drawn span (PRD §45.4).</summary>
  public string Statistics() {
    if (this._series.Count == 0)
      return string.Empty;

    var text = new System.Text.StringBuilder();
    foreach (var series in this._series) {
      var count = Math.Max(0, series.Values.Count - this.SkipNewest);
      var take = Math.Min(count, this.Samples);
      var lowest = double.PositiveInfinity;
      var highest = double.NegativeInfinity;
      var total = 0d;
      var seen = 0;
      for (var i = count - take; i < count; ++i) {
        var value = series.Values[i];
        if (!value.HasValue)
          continue;

        lowest = Math.Min(lowest, value.Value);
        highest = Math.Max(highest, value.Value);
        total += value.Value;
        ++seen;
      }

      if (text.Length > 0)
        text.Append('\n');

      text.Append(series.Label.Length > 0 ? series.Label : this.Caption).Append(": ");
      if (seen == 0) {
        text.Append(Humanize.Placeholder(UnknownReason.NotSampledYet));
        continue;
      }

      var current = series.Values[count - 1];
      text.Append("current ").Append(this.Reading(current))
        .Append(", minimum ").Append(this.Reading(Rate.Of(lowest)))
        .Append(", maximum ").Append(this.Reading(Rate.Of(highest)))
        .Append(", average ").Append(this.Reading(Rate.Of(total / seen)));
    }

    return text.ToString();
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var theme = this.Theme;
    var bounds = new Rectangle(0, 0, this.Width, this.Height);

    // Black ground and a green graticule, which is what a monitor's plot has looked like since
    // before any of these tools existed. It is deliberately *not* the theme's field colour: this is
    // an instrument, and an instrument that changes contrast with the desktop is harder to read at a
    // glance than one that always looks the same.
    g.FillRectangle(RowPalette.PlotBackground, bounds);

    // A graticule rather than four rules: the vertical lines give the eye something to measure
    // horizontal movement against, which is most of what a scrolling plot is for.
    const int Cell = 16;
    for (var y = Cell; y < this.Height; y += Cell)
      g.DrawLine(RowPalette.PlotGrid(theme), 0, y, this.Width, y);

    for (var x = this.Width % Cell; x < this.Width; x += Cell)
      g.DrawLine(RowPalette.PlotGrid(theme), x, 0, x, this.Height);

    foreach (var series in this._series)
      SeriesPainter.Draw(
        g,
        bounds,
        series.Values,
        this.Maximum,
        series.Color,
        this.Samples,
        this.SkipNewest,
        series.Filled ?? this.Filled
      );

    this.DrawCursor(g);
    g.DrawRectangle(theme.Border, new(0, 0, this.Width - 1, this.Height - 1));

    // The caption and the current reading sit inside the plot, top-left, the way the reference tools
    // place them — a label outside would cost a row of pixels the plot can use.
    var caption = this.Value.Length > 0 && this.Caption.Length > 0
      ? $"{this.Caption}: {this.Value}"
      : this.Caption + this.Value;

    if (caption.Length > 0)
      g.DrawText(caption, theme.DefaultFont, RowPalette.PlotInk(theme, PlotInkKind.Caption), new(4, 2, this.Width - 8, 16), ContentAlignment.TopLeft);

    if (this.ScaleLabel.Length > 0)
      Shadowed(g, this.ScaleLabel, theme, new(4, 2, this.Width - 8, 16), ContentAlignment.TopRight);

    this.DrawAxis(g, theme);
  }

  /// <summary>
  /// What the two ends of the axis are, spelled out (PRD §45.4).
  /// </summary>
  /// <remarks>
  /// Only where there is room for them. On the sixteen plots of a per-core grid the labels would be
  /// most of the picture, and the grid's axis is the same as the big plot's above it.
  /// </remarks>
  private void DrawAxis(IGraphics g, ITheme theme) {
    if (this.Height < 56 || this.Width < 220)
      return;

    var strip = new Rectangle(4, this.Height - 18, this.Width - 8, 16);
    Shadowed(g, Ago(this.SpanSeconds), theme, strip, ContentAlignment.TopLeft);
    Shadowed(g, this.Paused ? "Paused" : "Now", theme, strip, ContentAlignment.TopRight);
  }

  /// <summary>"60 seconds ago", "15 minutes ago" — the far end of the axis, in the units it is set in.</summary>
  private static string Ago(int seconds)
    => seconds >= 120 ? $"{seconds / 60} minutes ago" : $"{seconds} seconds ago";

  /// <summary>
  /// A rule down the sample the pointer is on, and its readings beside it.
  /// </summary>
  /// <remarks>
  /// The readings are drawn on the plot rather than in a floating tip: this is one control among
  /// several stacked graphs, and a popup that follows the pointer covers the neighbour a reader is
  /// comparing against.
  /// </remarks>
  private void DrawCursor(IGraphics g) {
    if (this._hoverX < 0 || this.HoverText.Length == 0)
      return;

    var theme = this.Theme;
    g.DrawLine(RowPalette.PlotInk(theme, PlotInkKind.Cursor), this._hoverX, 0, this._hoverX, this.Height);
    var wide = this._hoverX > this.Width / 2;
    var box = new Rectangle(wide ? 4 : this.Width / 2, 18, (this.Width / 2) - 8, 14);
    g.DrawText(this.HoverText, theme.DefaultFont, RowPalette.PlotInk(theme, PlotInkKind.Caption), box, wide ? ContentAlignment.TopLeft : ContentAlignment.TopRight);
  }

  /// <summary>
  /// Axis text with a dark letter behind it.
  /// </summary>
  /// <remarks>
  /// The corners are exactly where a filled series ends up when a resource is busy, and a pale green
  /// "Now" on a pale purple fill is a label nobody can read at the moment they most want to — which
  /// is when the graph is full (PRD §45.9).
  /// </remarks>
  private static void Shadowed(IGraphics g, string text, ITheme theme, Rectangle bounds, ContentAlignment alignment) {
    // No shadow under a high-contrast scheme: a second colour one pixel from the first is the one
    // thing that scheme exists to stop, and a white label needs no help (PRD §45.9).
    if (RowPalette.PlotInkShadow(theme) is { } shadow)
      g.DrawText(text, theme.DefaultFont, shadow, bounds with { X = bounds.X + 1, Y = bounds.Y + 1 }, alignment);

    g.DrawText(text, theme.DefaultFont, RowPalette.PlotInk(theme, PlotInkKind.Axis), bounds, alignment);
  }

}
