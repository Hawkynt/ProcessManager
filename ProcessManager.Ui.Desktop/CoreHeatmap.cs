using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Every logical processor as one cell of a heat map, grouped by socket and by kind (PRD §46, §7.2).
/// </summary>
/// <remarks>
/// <para>
/// A bar per core was readable at eight and useless at sixty-four: the strip gave each bar four
/// pixels, and sixty-four four-pixel bars are a texture rather than a reading. A cell can be small
/// and still say its value, because colour survives being small in a way a height does not — which
/// is the whole argument for a heat map over meters.
/// </para>
/// <para>
/// <b>Grouped, not merely listed.</b> A hybrid part has cores of two very different speeds, and a
/// grid in kernel enumeration order interleaves them on some machines and separates them on others.
/// Performance cores come first, efficiency cores after, sockets in their own blocks — so the same
/// silicon always looks the same, and "the fast half is idle while the slow half is saturated" is
/// visible as a shape rather than being something a reader has to work out from sixteen numbers.
/// </para>
/// <para>
/// Reads the delta directly on every paint rather than caching a copy: the delta is recomputed once
/// per sample and this control is repainted once per sample.
/// </para>
/// </remarks>
public sealed class CoreHeatmap : OwnerDrawnControl {

  private const int _Gap = 2;
  private const int _LabelWidth = 22;

  private SnapshotDelta? _delta;
  private CpuTopology _topology = CpuTopology.Empty;

  /// <summary>Point at the sampler's delta once; repaints follow <see cref="Control.Invalidate()"/>.</summary>
  public void Bind(SnapshotDelta delta) {
    this._delta = delta;
    this.Invalidate();
  }

  /// <summary>How the cores are arranged. Empty falls back to a flat grid in kernel order.</summary>
  public CpuTopology Topology {
    get => this._topology;
    set {
      this._topology = value ?? CpuTopology.Empty;
      this.Invalidate();
    }
  }

  /// <summary>The rows the map is laid out in, each a socket-and-kind group with a label.</summary>
  private List<(string Label, IReadOnlyList<int> Cores)> Groups(int cores) {
    var groups = new List<(string, IReadOnlyList<int>)>();
    var packages = this._topology.Packages;
    if (packages.Count == 0) {
      // Nothing known about the arrangement: one row of every core the delta has, which is what the
      // strip always did and the right fallback for a container or an unusual architecture.
      var all = new int[cores];
      for (var i = 0; i < cores; ++i)
        all[i] = i;

      groups.Add((string.Empty, all));
      return groups;
    }

    var hybrid = this._topology.IsHybrid;
    var manySockets = packages.Count > 1;
    foreach (var package in packages) {
      var members = this._topology.Of(package);

      // Split by kind only where there is more than one kind — a row labelled "P" on a machine
      // whose cores are all the same says nothing and costs a label column.
      foreach (var kind in hybrid ? _Kinds : _AnyKind) {
        var logical = new List<int>();
        foreach (var core in members)
          if ((!hybrid || core.Kind == kind) && core.Logical < cores)
            logical.Add(core.Logical);

        if (logical.Count == 0)
          continue;

        var label = (manySockets, hybrid) switch {
          (true, true) => $"{package}{Letter(kind)}",
          (true, false) => package.ToString(System.Globalization.CultureInfo.InvariantCulture),
          (false, true) => Letter(kind).ToString(),
          _ => string.Empty,
        };

        groups.Add((label, logical));
      }
    }

    return groups;
  }

  private static readonly CoreKind[] _Kinds = [CoreKind.Performance, CoreKind.Efficiency, CoreKind.Unknown];

  private static readonly CoreKind[] _AnyKind = [CoreKind.Unknown];

  private static char Letter(CoreKind kind) => kind switch {
    CoreKind.Performance => 'P',
    CoreKind.Efficiency => 'E',
    _ => '?',
  };

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var theme = this.Theme;
    g.FillRectangle(RowPalette.PlotBackground, new(0, 0, this.Width, this.Height));

    var delta = this._delta;
    var cores = delta?.PerCoreCount ?? 0;
    if (cores == 0) {
      g.DrawText("no per-core data yet", theme.DefaultFont, theme.DisabledText,
        new(0, 0, this.Width, this.Height), ContentAlignment.MiddleCenter);
      return;
    }

    var groups = this.Groups(cores);
    var labelled = groups.Count > 1 || groups[0].Label.Length > 0;
    var left = labelled ? _LabelWidth : 0;

    var widest = 1;
    foreach (var group in groups)
      widest = Math.Max(widest, group.Cores.Count);

    var bands = this.BandsPerGroup(groups.Count, widest, left);
    var columns = (widest + bands - 1) / bands;
    var rows = groups.Count * bands;
    var cellHeight = (this.Height - ((rows - 1) * _Gap)) / rows;
    var cellWidth = ((this.Width - left) - ((columns - 1) * _Gap)) / columns;
    if (cellHeight < 3 || cellWidth < 3)
      return;

    var y = 0;
    foreach (var group in groups) {
      var groupHeight = (bands * cellHeight) + ((bands - 1) * _Gap);
      if (labelled && group.Label.Length > 0)
        g.DrawText(group.Label, theme.DefaultFont, _LabelInk,
          new(0, y, _LabelWidth - 2, groupHeight), ContentAlignment.MiddleCenter);

      for (var i = 0; i < group.Cores.Count; ++i) {
        var cell = new Rectangle(
          left + ((i % columns) * (cellWidth + _Gap)),
          y + ((i / columns) * (cellHeight + _Gap)),
          cellWidth,
          cellHeight
        );

        if (cell.Bottom > this.Height)
          break;

        var value = delta!.PerCoreBusyPercent(group.Cores[i]);
        g.FillRectangle(value.HasValue ? Heat(value.Value) : RowPalette.PlotBackground, cell);
        g.DrawRectangle(RowPalette.PlotGrid, cell);
      }

      y += groupHeight + _Gap;
    }
  }

  /// <summary>
  /// How many rows of cells each group wraps into.
  /// </summary>
  /// <remarks>
  /// Chosen to make the cells as near square as the space allows. Sixteen cores in one row of a strip
  /// ninety pixels tall are twenty-six by ninety, which reads as the bar meters this replaced; in two
  /// rows they are fifty-three by forty-five, which reads as a map. Squareness is not decoration —
  /// a colour is judged by area, and cells of wildly different aspect are judged against each other
  /// wrongly.
  /// <para>
  /// Every group wraps by the same amount so their cells line up. A machine with eight performance
  /// cores above sixteen efficiency ones must not draw two rows of different-sized squares, which
  /// invites a comparison of areas that means nothing.
  /// </para>
  /// </remarks>
  private int BandsPerGroup(int groups, int widest, int left) {
    var best = 1;
    var bestScore = double.MaxValue;
    for (var bands = 1; bands <= 4 && bands <= widest; ++bands) {
      var columns = (widest + bands - 1) / bands;
      var rows = groups * bands;
      var height = (this.Height - ((rows - 1) * _Gap)) / (double)rows;
      var width = ((this.Width - left) - ((columns - 1) * _Gap)) / (double)columns;
      if (height < 8 || width < 8)
        continue;

      var score = Math.Abs(Math.Log(width / height));
      if (score >= bestScore)
        continue;

      bestScore = score;
      best = bands;
    }

    return best;
  }

  private static readonly Color _LabelInk = Color.FromArgb(0xFF, 0x9C, 0xE8, 0x9C);

  /// <summary>
  /// Idle to saturated, as one ramp.
  /// </summary>
  /// <remarks>
  /// Green through amber to red, which is the convention every load display in the world uses, and
  /// interpolated rather than stepped: a stepped ramp turns a core drifting around sixty percent
  /// into a cell that flickers between two colours, and the flicker reads as activity that is not
  /// there. The dark end is the plot's own ground, so an idle core is indistinguishable from the
  /// background — which is correct, because an idle core is nothing to look at.
  /// </remarks>
  private static Color Heat(double percent) {
    var load = Math.Clamp(percent, 0, 100) / 100d;
    return load < 0.5
      ? Blend(RowPalette.PlotBackground, Color.FromArgb(0xFF, 0x28, 0xC8, 0x28), load * 2)
      : load < 0.8
        ? Blend(Color.FromArgb(0xFF, 0x28, 0xC8, 0x28), Color.FromArgb(0xFF, 0xD8, 0xB0, 0x28), (load - 0.5) / 0.3)
        : Blend(Color.FromArgb(0xFF, 0xD8, 0xB0, 0x28), Color.FromArgb(0xFF, 0xD8, 0x30, 0x30), (load - 0.8) / 0.2);
  }

  private static Color Blend(Color from, Color to, double fraction) {
    var f = Math.Clamp(fraction, 0, 1);
    return Color.FromArgb(
      0xFF,
      (int)Math.Round(from.R + ((to.R - from.R) * f)),
      (int)Math.Round(from.G + ((to.G - from.G) * f)),
      (int)Math.Round(from.B + ((to.B - from.B) * f))
    );
  }

}
