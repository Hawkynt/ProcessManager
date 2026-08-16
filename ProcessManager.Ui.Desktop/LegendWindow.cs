using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// What the row colours mean.
/// </summary>
/// <remarks>
/// PRD §7.1: a colour no dialog explains is decoration. Every category the list can paint is in here
/// with its swatch and a sentence, and the categories the program deliberately does not distinguish
/// are named at the bottom so their absence is a decision rather than an oversight.
/// </remarks>
public sealed class LegendWindow : Form {

  public LegendWindow() {
    this.Text = "Colour legend";
    this.Bounds = new(0, 0, 460, 330);

    var y = 12;
    foreach (var category in (ReadOnlySpan<ProcessCategory>)[
      ProcessCategory.New,
      ProcessCategory.Exited,
      ProcessCategory.Own,
      ProcessCategory.System,
      ProcessCategory.Service,
      ProcessCategory.Suspended,
      ProcessCategory.Zombie,
      ProcessCategory.Other,
    ]) {
      var swatch = new SwatchPanel(category) { Bounds = new(14, y, 28, 18) };
      var label = new Label {
        Text = ProcessCategories.Describe(category),
        Bounds = new(52, y, 380, 18),
      };

      this.Controls.Add(swatch);
      this.Controls.Add(label);
      y += 26;
    }

    this.Controls.Add(new Label {
      Bounds = new(14, y + 8, 430, 60),
      Text = "Not distinguished: packed, .NET, elevated and store processes. Telling those apart\n"
           + "needs information neither probe collects, and a colour that is sometimes right is\n"
           + "worse than none.",
    });
  }

  /// <summary>A colour chip. Its own control because a Label has no background of its own.</summary>
  private sealed class SwatchPanel(ProcessCategory category) : OwnerDrawnControl {

    protected override void OnPaint(PaintEventArgs e) {
      var theme = this.Theme;
      var fill = RowPalette.BackColorOf(category, theme) ?? theme.FieldBackground;
      e.Graphics.FillRectangle(fill, new(0, 0, this.Width, this.Height));
      e.Graphics.DrawRectangle(theme.Border, new(0, 0, this.Width - 1, this.Height - 1));
    }

  }

}
