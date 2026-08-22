using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Which resource a tray indicator is watching (PRD §65).
/// </summary>
public enum IndicatorKind : byte {
  Cpu,
  Memory,
  Disk,
  Network,
  Gpu,
}

/// <summary>
/// A tray icon, drawn as a column of history rather than a number (PRD §65).
/// </summary>
/// <remarks>
/// <para>
/// The whole point of a tray indicator is that it is read without being looked at: it sits in the
/// corner of a screen at sixteen or twenty-two pixels square, where a number is unreadable and a
/// shape is not. So each one is the same little graph Task Manager has always put there — recent
/// history left to right, newest at the right, the way every other plot in this program runs.
/// </para>
/// <para>
/// In Core and returning pixels rather than touching a tray, because the arithmetic is where the
/// mistakes are and a test should not need a desktop to catch them. A tray icon that is wrong is
/// wrong in a way nobody reports: it is a smudge in the corner that somebody stops trusting without
/// ever working out why.
/// </para>
/// </remarks>
public static class IndicatorIcon {

  /// <summary>Fully transparent, which is what a column with no sample in it must be.</summary>
  private const int _Clear = 0;

  /// <summary>
  /// Renders <paramref name="history"/> into <paramref name="pixels"/>, newest at the right.
  /// </summary>
  /// <param name="scale">The value that fills the icon. A percentage passes 100.</param>
  /// <param name="ink">The bar colour, as 0xAARRGGBB.</param>
  /// <param name="ground">
  /// What the unfilled part of the icon is. Transparent by default, so the tray's own background
  /// shows through — a tray icon that paints its own dark square is the one that looks wrong on a
  /// light panel, and every panel is somebody's.
  /// </param>
  /// <remarks>
  /// A sample nobody could read leaves its column clear rather than drawing a bar of no height. An
  /// unreadable counter and an idle machine are different states, and the icon must not merge them
  /// any more than a column may (PRD §72.3).
  /// </remarks>
  public static void Render(
    Span<int> pixels,
    int width,
    int height,
    HistoryRing<Rate>? history,
    double scale,
    int ink,
    int ground = _Clear
  ) {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
    if (pixels.Length != width * height)
      throw new ArgumentException($"{width}×{height} is {width * height} pixels, not {pixels.Length}.", nameof(pixels));

    pixels.Fill(ground);
    if (history is null || history.Count == 0 || scale <= 0)
      return;

    var visible = Math.Min(history.Count, width);
    var first = history.Count - visible;
    var offset = width - visible;

    for (var i = 0; i < visible; ++i) {
      var value = history[first + i];
      if (!value.HasValue)
        continue;

      var fraction = Math.Clamp(value.Value / scale, 0, 1);
      // At least one row for anything above nought, or a machine using half a percent draws as one
      // using none — which is the difference the icon exists to show at a glance.
      var bar = fraction <= 0 ? 0 : Math.Max(1, (int)Math.Round(fraction * height));
      var column = offset + i;
      for (var row = height - bar; row < height; ++row)
        pixels[row * width + column] = ink;
    }
  }

  /// <summary>The pixels as an array, for a caller that wants one.</summary>
  public static int[] Render(
    int width,
    int height,
    HistoryRing<Rate>? history,
    double scale,
    int ink,
    int ground = _Clear
  ) {
    var pixels = new int[width * height];
    Render(pixels, width, height, history, scale, ink, ground);
    return pixels;
  }

  /// <summary>What each indicator is called, for a tooltip and for the settings file.</summary>
  public static string Name(IndicatorKind kind) => kind switch {
    IndicatorKind.Cpu => "cpu",
    IndicatorKind.Memory => "memory",
    IndicatorKind.Disk => "disk",
    IndicatorKind.Network => "network",
    IndicatorKind.Gpu => "gpu",
    _ => "unknown",
  };

  /// <summary>The word a person reads, which is not the key a file is written with.</summary>
  public static string Describe(IndicatorKind kind) => kind switch {
    IndicatorKind.Cpu => "Processor",
    IndicatorKind.Memory => "Memory",
    IndicatorKind.Disk => "Disk",
    IndicatorKind.Network => "Network",
    IndicatorKind.Gpu => "Graphics",
    _ => "Unknown",
  };

  /// <summary>
  /// The colour each indicator is drawn in, matching the resource pages so the corner of the screen
  /// and the page it opens are recognisably about the same thing.
  /// </summary>
  public static int Ink(IndicatorKind kind) => kind switch {
    IndicatorKind.Cpu => unchecked((int)0xFF3B78C8),
    IndicatorKind.Memory => unchecked((int)0xFF8B5CB8),
    IndicatorKind.Disk => unchecked((int)0xFF3FA34D),
    IndicatorKind.Network => unchecked((int)0xFFC8873B),
    IndicatorKind.Gpu => unchecked((int)0xFFB8425C),
    _ => unchecked((int)0xFF808080),
  };

}
