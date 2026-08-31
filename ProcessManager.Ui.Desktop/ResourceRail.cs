using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One row of the performance page's resource rail (PRD §45.1).
/// </summary>
/// <param name="Title">What the resource is: <c>Processor</c>, <c>Disk — sda</c>.</param>
/// <param name="Primary">Its headline reading, already formatted.</param>
/// <param name="Secondary">A second reading where one is worth having — a clock beside a
/// utilisation, a total beside a usage — or empty.</param>
/// <param name="History">The same history the main graph draws, for the sparkline.</param>
/// <param name="Maximum">The top of the sparkline's scale; 0 to fit whatever it holds.</param>
/// <param name="Key">
/// The section's full title, which is what the rest of the window identifies it by. The rail shows
/// the short name and the header shows the model, so the two must not be the same string.
/// </param>
public sealed record ResourceRow(
  string Title,
  string Primary,
  string Secondary,
  HistoryRing<Rate>? History,
  double Maximum,
  Color Accent,
  string Key
) {

  /// <summary>What the rail's text falls back to, and what a test reads.</summary>
  public override string ToString()
    => this.Secondary.Length > 0 ? $"{this.Title}   {this.Primary}   {this.Secondary}" : $"{this.Title}   {this.Primary}";


  /// <summary>
  /// An accent mixed most of the way back into the page, so it reads as a highlight rather than as
  /// a block of colour. A fifth of the accent is enough to see and little enough to read over.
  /// </summary>
  private static Color Tint(Color accent, Color ground) => Color.FromArgb(
    0xFF,
    ((accent.R * 1) + (ground.R * 4)) / 5,
    ((accent.G * 1) + (ground.G * 4)) / 5,
    ((accent.B * 1) + (ground.B * 4)) / 5
  );

}

/// <summary>
/// The rail down the left of the performance page (PRD §45.1, §45.5).
/// </summary>
/// <remarks>
/// <para>
/// A plain list of "name, value" text answered the first of the page's three questions — which
/// resource is busy — but not the second, which is how long it has been. A row that carries its own
/// sparkline answers both without anything being clicked, which is the whole argument for a rail
/// rather than a drop-down.
/// </para>
/// <para>
/// Owner-drawn on top of <see cref="ListBox"/> rather than a control of its own: the list already
/// has the selection, the keyboard, the scrolling and now a scrollbar, and none of that is worth
/// writing again to put a graph in a row.
/// </para>
/// </remarks>
public sealed class ResourceRail : ListBox {

  private const int _Pad = 8;

  public ResourceRail() =>
    // Tall enough for three bands — name, sparkline, readings — at the sizes §45.1 asks for.
    this.ItemHeight = 76;

  /// <summary>
  /// How many samples wide each row's sparkline is, which the page keeps equal to its graph's span.
  /// </summary>
  /// <remarks>
  /// §45.1 asks for a sparkline "over the same history the main graph uses". Changing the graph to
  /// five minutes and leaving the rail on one would put two different time axes on one page, and
  /// nothing on either would say which is which.
  /// </remarks>
  public int Samples { get; set; } = 60;

  /// <summary>
  /// How much older history is progressively fitted behind <see cref="Samples"/>.
  /// </summary>
  /// <remarks>
  /// The main graph and every rail row must use one time axis. Keeping this explicit rather than
  /// relying on the painter's default means a runtime history-mode change cannot leave the rail on
  /// a different horizon from the selected resource.
  /// </remarks>
  public double HistoryMultiplier { get; set; } = HistoryAxis.DefaultMultiplier;

  /// <summary>Frozen with the graphs, so pausing the page pauses all of it (PRD §45.4).</summary>
  public int SkipNewest { get; set; }

  /// <summary>
  /// Tightens the rows for §45.7's compact density: less spacing, and a shorter sparkline, so more
  /// resources are on screen at once.
  /// </summary>
  public bool Compact {
    set {
      this.ItemHeight = value ? 64 : 76;
      this.Invalidate();
    }
  }

  /// <summary>Whatever is left of the row once the name and the readings have had theirs.</summary>
  private int SparkHeight => Math.Max(8, this.ItemHeight - 42);

  protected override void OnDrawRow(IGraphics g, int index, Rectangle bounds, bool selected) {
    if (this.Items[index] is not ResourceRow row) {
      base.OnDrawRow(g, index, bounds, selected);
      return;
    }

    var theme = this.Theme;

    // The base class has already filled a selected row with the theme's selection colour, and at this
    // row height that swallows the sparkline — the one thing on the row that has to stay readable.
    // Painting over it with a pale tint of the resource's own accent, plus a stripe, is what §45.1
    // asks for and what the first version of this looked wrong doing.
    if (selected) {
      g.FillRectangle(Tint(row.Accent, theme.FieldBackground), bounds);
      g.FillRectangle(row.Accent, new Rectangle(bounds.X, bounds.Y, 3, bounds.Height));
    }

    var text = theme.ControlText;
    var left = bounds.X + _Pad;
    var width = bounds.Width - (2 * _Pad);

    g.DrawText(row.Title, theme.DefaultFont, text, new(left, bounds.Y + 4, width, 16), ContentAlignment.TopLeft);

    // A resource with no history of its own — the System summary — gets no empty black box
    // pretending to be a graph.
    var spark = new Rectangle(left, bounds.Y + 22, width, this.SparkHeight);
    if (row.History is not null)
      Sparkline.Draw(g, spark, row.History, row.Maximum, row.Accent, theme, this.Samples, this.SkipNewest, this.HistoryMultiplier);

    var readings = new Rectangle(left, bounds.Bottom - 18, width, 16);
    g.DrawText(row.Primary, theme.DefaultFont, text, readings, ContentAlignment.TopLeft);
    if (row.Secondary.Length > 0)
      g.DrawText(row.Secondary, theme.DefaultFont, text, readings, ContentAlignment.TopRight);
  }

  /// <summary>
  /// An accent mixed most of the way back into the page, so it reads as a highlight rather than as a
  /// block of colour. A fifth of the accent is enough to see and little enough to read over.
  /// </summary>
  private static Color Tint(Color accent, Color ground) => Color.FromArgb(
    0xFF,
    (accent.R + (ground.R * 4)) / 5,
    (accent.G + (ground.G * 4)) / 5,
    (accent.B + (ground.B * 4)) / 5
  );

}
