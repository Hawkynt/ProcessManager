using System.Drawing;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// The colour each <see cref="ProcessCategory"/> paints a row.
/// </summary>
/// <remarks>
/// <para>
/// Two palettes, picked from the theme's background rather than from a preference: the pale one
/// reads on a light desktop and is unreadable on a dark one, and the reverse. That the colours have
/// to be hard-coded at all is a limitation of <c>ITheme</c>, which offers one accent and a selection
/// colour — enough to match a desktop, not enough to distinguish seven kinds of process.
/// </para>
/// <para>
/// The hues are the ones Process Explorer and Process Hacker taught people: blue for the system,
/// green for new, red for gone. Nothing here is load-bearing on its own — the same information is in
/// the State column, and the legend window spells every colour out, because a colour no dialog
/// explains is decoration (PRD §7.1).
/// </para>
/// </remarks>
public static class RowPalette {

  /// <summary>The row background for a category, or null to leave the theme's alone.</summary>
  public static Color? BackColorOf(ProcessCategory category, ITheme theme) {
    var dark = IsDark(theme.FieldBackground);
    return category switch {
      ProcessCategory.New => dark ? Color.FromArgb(0xFF, 0x1E, 0x3E, 0x24) : Color.FromArgb(0xFF, 0xD6, 0xF5, 0xD6),
      ProcessCategory.Exited => dark ? Color.FromArgb(0xFF, 0x4A, 0x1E, 0x1E) : Color.FromArgb(0xFF, 0xFF, 0xD6, 0xD6),
      ProcessCategory.Zombie => dark ? Color.FromArgb(0xFF, 0x4A, 0x2A, 0x12) : Color.FromArgb(0xFF, 0xFF, 0xE0, 0xC0),
      ProcessCategory.Suspended => dark ? Color.FromArgb(0xFF, 0x33, 0x33, 0x38) : Color.FromArgb(0xFF, 0xDC, 0xDC, 0xE0),
      ProcessCategory.System => dark ? Color.FromArgb(0xFF, 0x1B, 0x2E, 0x4A) : Color.FromArgb(0xFF, 0xCF, 0xE2, 0xF7),
      ProcessCategory.Service => dark ? Color.FromArgb(0xFF, 0x14, 0x38, 0x3A) : Color.FromArgb(0xFF, 0xCF, 0xF0, 0xF2),
      ProcessCategory.Own => dark ? Color.FromArgb(0xFF, 0x3A, 0x36, 0x18) : Color.FromArgb(0xFF, 0xFB, 0xF5, 0xCE),
      _ => null,
    };
  }

  /// <summary>The colour of the plot series and meters, so the whole window agrees with itself.</summary>
  public static Color Cpu => Color.FromArgb(0xFF, 0x28, 0xC8, 0x28);

  public static Color CpuKernel => Color.FromArgb(0xFF, 0xD0, 0x30, 0x30);

  public static Color Memory => Color.FromArgb(0xFF, 0x28, 0xB4, 0xB4);

  public static Color Io => Color.FromArgb(0xFF, 0xE0, 0xC0, 0x30);

  /// <summary>The plot background and grid: black with a green graticule, as the reference tools use.</summary>
  public static Color PlotBackground => Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A);

  public static Color PlotGrid => Color.FromArgb(0xFF, 0x14, 0x3C, 0x14);

  /// <summary>
  /// Whether a background is dark enough to need the dark palette. Perceptual weights, because a
  /// mid-green and a mid-blue of the same arithmetic mean look nothing alike.
  /// </summary>
  private static bool IsDark(Color color)
    => color.R * 0.299 + color.G * 0.587 + color.B * 0.114 < 128;

}
