using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// How physical memory divides up, as one bar (PRD §14, §47).
/// </summary>
/// <remarks>
/// <para>
/// The picture that explains why a machine with almost no free memory is healthy. 3.8 GB free out of
/// 125 GB reads as an emergency in a list of numbers; seen as a bar, next to 68 GB of cache the
/// kernel hands back the moment anything asks, it reads as a machine doing its job.
/// </para>
/// <para>
/// Every band is labelled in place rather than only on hover. A bar whose meaning is behind a mouse
/// gesture is a bar most people never read, and the labels are what stop the colours being the only
/// thing carrying the information (PRD §45.9). Bands too narrow for their text are still there, and
/// the tooltip names them.
/// </para>
/// </remarks>
public sealed class CompositionBar : OwnerDrawnControl {

  private MemoryComposition _composition;

  /// <summary>Which band the pointer is over, or -1.</summary>
  private int _hot = -1;

  public MemoryComposition Composition {
    get => this._composition;
    set {
      this._composition = value;
      this.Invalidate();
    }
  }

  /// <summary>What the pointer is over, for the window to show. Empty when it is over nothing.</summary>
  public string HoverText { get; private set; } = string.Empty;

  protected override void OnMouseMove(MouseEventArgs e) {
    var was = this._hot;
    this._hot = this.BandAt(e.X);
    this.HoverText = this._hot < 0
      ? string.Empty
      : $"{this._composition.Bands[this._hot].Label}: {Humanize.Bytes(Model.Counter.Of(this._composition.Bands[this._hot].Bytes))}"
        + $" — {this._composition.Bands[this._hot].Explanation}";

    if (was != this._hot)
      this.Invalidate();
  }

  protected override void OnMouseLeave(EventArgs e) {
    if (this._hot < 0)
      return;

    this._hot = -1;
    this.HoverText = string.Empty;
    this.Invalidate();
  }

  private int BandAt(int x) {
    if (!this._composition.HasValue)
      return -1;

    var left = 0d;
    for (var i = 0; i < this._composition.Bands.Count; ++i) {
      var width = this.Width * (double)this._composition.Bands[i].Bytes / this._composition.TotalBytes;
      if (x >= left && x < left + width)
        return i;

      left += width;
    }

    return -1;
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var theme = this.Theme;
    var bounds = new Rectangle(0, 0, this.Width, this.Height);
    g.FillRectangle(theme.FieldBackground, bounds);

    if (!this._composition.HasValue) {
      g.DrawRectangle(theme.Border, new(0, 0, this.Width - 1, this.Height - 1));
      return;
    }

    // Laid out in fractional pixels and rounded at each boundary rather than per band, so the bands
    // meet exactly and the last one ends on the right edge. Rounding each width independently leaves
    // a gap or an overlap of a pixel or two, which on a bar whose whole point is that it is a
    // partition looks like an arithmetic error.
    var left = 0d;
    for (var i = 0; i < this._composition.Bands.Count; ++i) {
      var band = this._composition.Bands[i];
      var right = left + (this.Width * (double)band.Bytes / this._composition.TotalBytes);
      var x = (int)Math.Round(left);
      var width = (i == this._composition.Bands.Count - 1 ? this.Width : (int)Math.Round(right)) - x;
      left = right;
      if (width <= 0)
        continue;

      var cell = new Rectangle(x, 0, width, this.Height);
      g.FillRectangle(Shade(i, this._hot == i), cell);
      g.DrawRectangle(theme.FieldBackground, cell);

      // Only where it fits, and the name is what goes first: a band too narrow for "Cached 10.8G"
      // is still wide enough for "Cached", and a band with nothing on it at all is a block of
      // colour whose meaning is behind a mouse gesture (PRD §45.9). The tooltip covers the ones
      // that cannot say even their own name.
      var value = Humanize.Bytes(Model.Counter.Of(band.Bytes));
      if (width > 100)
        g.DrawText($"{band.Label}  {value}", theme.DefaultFont, _Ink, cell, ContentAlignment.MiddleCenter);
      else if (width > 52)
        g.DrawText(band.Label, theme.DefaultFont, _Ink, cell, ContentAlignment.MiddleCenter);
    }

    g.DrawRectangle(theme.Border, new(0, 0, this.Width - 1, this.Height - 1));
  }

  private static readonly Color _Ink = Color.FromArgb(0xFF, 0xF4, 0xF0, 0xFA);

  /// <summary>
  /// Four shades of the memory accent, dark to light in the order the bands run.
  /// </summary>
  /// <remarks>
  /// One family rather than four colours, because these are four parts of one quantity and four
  /// unrelated hues would say they are four different things. Ordered so the eye reads the bar as a
  /// gradient from "spoken for" to "available", which is what it is.
  /// </remarks>
  private static Color Shade(int band, bool hot) {
    var colour = band switch {
      0 => Color.FromArgb(0xFF, 0x5B, 0x3F, 0x8C),
      1 => Color.FromArgb(0xFF, 0x7A, 0x5A, 0xB0),
      2 => Color.FromArgb(0xFF, 0x9E, 0x86, 0xC8),
      _ => Color.FromArgb(0xFF, 0xC5, 0xB8, 0xDF),
    };

    return hot ? Lighten(colour) : colour;
  }

  private static Color Lighten(Color colour) => Color.FromArgb(
    0xFF,
    Math.Min(255, colour.R + 40),
    Math.Min(255, colour.G + 40),
    Math.Min(255, colour.B + 40)
  );

}
