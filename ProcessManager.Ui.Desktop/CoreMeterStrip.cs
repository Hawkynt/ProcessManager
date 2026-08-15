using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One bar per logical core — the htop meter as a widget (PRD §7.2).
/// </summary>
/// <remarks>
/// Reads the delta directly on every paint rather than caching a copy: the delta is recomputed once
/// per sample and this control is repainted once per sample, so a cache would be a copy of something
/// that is never read twice.
/// </remarks>
public sealed class CoreMeterStrip : OwnerDrawnControl {

  private SnapshotDelta? _delta;

  /// <summary>Point at the sampler's delta once; repaints follow <see cref="Control.Invalidate()"/>.</summary>
  public void Bind(SnapshotDelta delta) {
    this._delta = delta;
    this.Invalidate();
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var theme = this.Theme;
    g.FillRectangle(theme.ControlBackground, new(0, 0, this.Width, this.Height));

    var delta = this._delta;
    var cores = delta?.PerCoreCount ?? 0;
    if (cores == 0) {
      g.DrawText("no per-core data yet", theme.DefaultFont, theme.DisabledText,
        new(0, 0, this.Width, this.Height), ContentAlignment.MiddleCenter);
      return;
    }

    // Bars share the width evenly with a one-pixel gap. Below four pixels a bar stops being
    // readable, so on a machine with more cores than pixels the strip scrolls off rather than
    // drawing lines nobody can distinguish.
    var barWidth = Math.Max(4, (this.Width - cores) / cores);
    var x = 0;
    for (var core = 0; core < cores && x < this.Width; ++core) {
      var value = delta!.PerCoreBusyPercent(core);
      var height = this.Height - 2;
      var slot = new Rectangle(x, 1, barWidth, height);
      g.FillRectangle(theme.FieldBackground, slot);

      if (value.HasValue) {
        var percent = Math.Clamp(value.Value, 0, 100);
        var filled = (int)Math.Round(percent * height / 100);
        var color = percent >= 90 ? Color.FromArgb(0xD0, 0x40, 0x40)
          : percent >= 60 ? Color.FromArgb(0xD0, 0xA0, 0x30)
          : theme.Accent;

        g.FillRectangle(color, new(x, 1 + height - filled, barWidth, filled));
      }

      g.DrawRectangle(theme.Border, slot);
      x += barWidth + 1;
    }
  }

}
