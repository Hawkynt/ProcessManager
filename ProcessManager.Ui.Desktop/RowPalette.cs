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

  /// <summary>
  /// The colours the settings file overrides, by the names <see cref="Settings.UserSettings.ColourNames"/>
  /// lists. Empty until <see cref="Apply"/> is called, which is what makes the built-ins the
  /// defaults rather than the only option.
  /// </summary>
  private static IReadOnlyDictionary<string, uint> _overrides =
    new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

  /// <summary>Takes the palette from a settings file. Names it does not know are ignored.</summary>
  public static void Apply(IReadOnlyDictionary<string, uint> colours) {
    ArgumentNullException.ThrowIfNull(colours);
    _overrides = colours;
  }

  /// <summary>An override by name, or the built-in when the file is silent about it.</summary>
  private static Color Pick(string name, Color builtIn)
    => _overrides.TryGetValue(name, out var argb) ? Color.FromArgb(unchecked((int)argb)) : builtIn;

  private static Color? Pick(string name, Color? builtIn)
    => _overrides.TryGetValue(name, out var argb) ? Color.FromArgb(unchecked((int)argb)) : builtIn;

  /// <summary>Whether the file has an opinion about this colour, which beats everything below.</summary>
  private static bool Chosen(string name) => _overrides.ContainsKey(name);

  /// <summary>
  /// Whether a wash behind text should be painted at all (PRD §45.9, §74).
  /// </summary>
  /// <remarks>
  /// <para>
  /// A high-contrast desktop is a promise: the theme's foreground and background are a pair chosen to
  /// be readable, and every colour here is a third thing painted between them. A pale green behind
  /// text the theme coloured for a white ground breaks exactly the guarantee the user switched the
  /// scheme on to get — which is why <c>ITheme</c>'s own documentation says owner-drawn chrome that
  /// blends colours should fall back to the plain palette while this is set.
  /// </para>
  /// <para>
  /// Nothing is lost that was only here. Every category the row wash names is also a value in the
  /// State, User or Elevated column, the cell mark sits under a number that stays legible either way,
  /// and the legend window says in words that the washes are off. That is the §74 rule working as
  /// intended rather than an exception to it: a colour was never the only carrier, so removing the
  /// colour removes nothing.
  /// </para>
  /// <para>
  /// A colour the settings file names outright is still painted. Somebody who wrote
  /// <c>color.new=#…</c> while running a high-contrast theme has said what they want, and second-
  /// guessing them would make the setting a suggestion.
  /// </para>
  /// </remarks>
  private static bool PaintsWashes(ITheme theme, string name) => !theme.IsHighContrast || Chosen(name);

  /// <summary>The name a category's colour goes by in the settings file.</summary>
  public static string NameOf(ProcessCategory category) => category switch {
    ProcessCategory.New => "new",
    ProcessCategory.Exited => "exited",
    ProcessCategory.Zombie => "zombie",
    ProcessCategory.Suspended => "suspended",
    ProcessCategory.System => "system",
    ProcessCategory.Elevated => "elevated",
    ProcessCategory.Service => "service",
    ProcessCategory.Own => "own",
    ProcessCategory.ImageReplaced => "image.replaced",
    ProcessCategory.Packaged => "packaged",
    ProcessCategory.ManagedRuntime => "managed",
    _ => string.Empty,
  };

  /// <summary>The row background for a category, or null to leave the theme's alone.</summary>
  public static Color? BackColorOf(ProcessCategory category, ITheme theme) {
    ArgumentNullException.ThrowIfNull(theme);
    var name = NameOf(category);
    return PaintsWashes(theme, name) ? Pick(name, BuiltInBackColorOf(category, theme)) : null;
  }

  private static Color? BuiltInBackColorOf(ProcessCategory category, ITheme theme) {
    var dark = IsDark(theme.FieldBackground);
    return category switch {
      ProcessCategory.New => dark ? Color.FromArgb(0xFF, 0x1E, 0x3E, 0x24) : Color.FromArgb(0xFF, 0xD6, 0xF5, 0xD6),
      ProcessCategory.Exited => dark ? Color.FromArgb(0xFF, 0x4A, 0x1E, 0x1E) : Color.FromArgb(0xFF, 0xFF, 0xD6, 0xD6),
      ProcessCategory.Zombie => dark ? Color.FromArgb(0xFF, 0x4A, 0x2A, 0x12) : Color.FromArgb(0xFF, 0xFF, 0xE0, 0xC0),
      ProcessCategory.Suspended => dark ? Color.FromArgb(0xFF, 0x33, 0x33, 0x38) : Color.FromArgb(0xFF, 0xDC, 0xDC, 0xE0),
      ProcessCategory.System => dark ? Color.FromArgb(0xFF, 0x1B, 0x2E, 0x4A) : Color.FromArgb(0xFF, 0xCF, 0xE2, 0xF7),
      // Purple, and deliberately not near the blue of System: the two mean different things and a
      // reader must not have to compare shades to tell "root started it" from "it became root".
      ProcessCategory.Elevated => dark ? Color.FromArgb(0xFF, 0x3A, 0x1E, 0x4A) : Color.FromArgb(0xFF, 0xEB, 0xD6, 0xF7),
      ProcessCategory.Service => dark ? Color.FromArgb(0xFF, 0x14, 0x38, 0x3A) : Color.FromArgb(0xFF, 0xCF, 0xF0, 0xF2),
      ProcessCategory.Own => dark ? Color.FromArgb(0xFF, 0x3A, 0x36, 0x18) : Color.FromArgb(0xFF, 0xFB, 0xF5, 0xCE),
      // Magenta, and the one row colour deliberately louder than the rest. Every other category here
      // describes what a process is; this one says something is wrong with it and wants finding in a
      // table of nine hundred rows.
      ProcessCategory.ImageReplaced => dark ? Color.FromArgb(0xFF, 0x5C, 0x1B, 0x3A) : Color.FromArgb(0xFF, 0xFF, 0xC4, 0xDE),
      // Periwinkle and olive: the two gaps left in a circle the eight above have most of. Both are a
      // step deeper than the pale family, because their neighbours are the two closest hues here —
      // periwinkle sits between System's sky and Elevated's lilac, and olive between Own's cream and
      // New's green — and at the same lightness the legend's own swatches read as shades of one
      // colour rather than as two. Neither can crowd the default table: neither is painted unless
      // somebody switched on the package or runtime field it reads, and somebody who did is looking
      // for exactly this.
      ProcessCategory.Packaged => dark ? Color.FromArgb(0xFF, 0x25, 0x25, 0x5E) : Color.FromArgb(0xFF, 0xC2, 0xC4, 0xF0),
      ProcessCategory.ManagedRuntime => dark ? Color.FromArgb(0xFF, 0x33, 0x42, 0x14) : Color.FromArgb(0xFF, 0xD8, 0xE9, 0xA8),
      _ => null,
    };
  }

  /// <summary>
  /// The wash behind a cell whose reading is high (PRD §23).
  /// </summary>
  /// <remarks>
  /// Deliberately pale, and deliberately amber-then-red rather than any of the category hues: it has
  /// to be legible under the row colour it sits on, and it must not be mistakable for one of the
  /// category colours that mean something else entirely. It is also never the only signal — the
  /// number is right there in the cell, which is what keeps this readable in high contrast and for
  /// anyone who cannot separate the two hues (PRD §45.9).
  /// </remarks>
  public static Color? HeatColour(UsageHeat heat, ITheme theme) {
    ArgumentNullException.ThrowIfNull(theme);

    var name = heat switch {
      UsageHeat.Warm => "heat.warm",
      UsageHeat.Hot => "heat.hot",
      _ => string.Empty,
    };

    if (name.Length == 0 || !PaintsWashes(theme, name))
      return null;

    var dark = IsDark(theme.FieldBackground);
    return heat == UsageHeat.Warm
      ? Pick(name, dark ? Color.FromArgb(0xFF, 0x5A, 0x45, 0x14) : Color.FromArgb(0xFF, 0xFF, 0xEA, 0xB8))
      : Pick(name, dark ? Color.FromArgb(0xFF, 0x6E, 0x24, 0x1C) : Color.FromArgb(0xFF, 0xFF, 0xC9, 0xBC));
  }

  /// <summary>
  /// The band behind a grouping heading (PRD §83).
  /// </summary>
  /// <remarks>
  /// Grey rather than any hue, because every colour in this list already means something about a
  /// <em>process</em> and a heading is not one: a heading tinted green would read as a group that
  /// had just started. A step away from the field background in whichever direction the theme has
  /// room for, so the band is visible on a light desktop and on a dark one without a second palette.
  /// </remarks>
  public static Color GroupHeading(ITheme theme) {
    ArgumentNullException.ThrowIfNull(theme);
    // A heading band is a real thing the theme has a colour for, and under a high-contrast scheme
    // that named colour is the answer rather than a step away from the field background: the step is
    // a blend, and a blend is what a high-contrast theme exists to stop.
    if (theme.IsHighContrast && !Chosen("group"))
      return theme.HeaderBackground;

    var ground = theme.FieldBackground;
    var shift = IsDark(ground) ? 26 : -26;
    return Pick(
      "group",
      Color.FromArgb(
        0xFF,
        Math.Clamp(ground.R + shift, 0, 255),
        Math.Clamp(ground.G + shift, 0, 255),
        Math.Clamp(ground.B + shift, 0, 255)
      )
    );
  }

  /// <summary>
  /// The wash behind the run of characters a filter matched (PRD §11).
  /// </summary>
  /// <remarks>
  /// Amber for the same reason the terminal's match attribute is: it has to sit under text that
  /// keeps its own colour, and it must not be one of the category colours, which say something else.
  /// </remarks>
  public static Color MatchHighlight(ITheme theme) {
    ArgumentNullException.ThrowIfNull(theme);
    // Kept under high contrast, unlike the washes above, and for the opposite reason: a matched run
    // has no other carrier in the cell — the filter note counts the rows, not the letters — so
    // dropping it would leave a search whose result is invisible. The theme's own selection colour
    // rather than an amber of ours, because it is the one background the scheme guarantees is both
    // distinct from the field and readable.
    if (theme.IsHighContrast && !Chosen("match"))
      return theme.SelectionBackground;

    return Pick(
      "match",
      IsDark(theme.FieldBackground)
        ? Color.FromArgb(0xFF, 0x6B, 0x5A, 0x10)
        : Color.FromArgb(0xFF, 0xFF, 0xF0, 0x8A)
    );
  }

  /// <summary>The colour of the plot series and meters, so the whole window agrees with itself.</summary>
  public static Color Cpu => Pick("cpu", Color.FromArgb(0xFF, 0x28, 0xC8, 0x28));

  public static Color CpuKernel => Pick("cpu.kernel", Color.FromArgb(0xFF, 0xD0, 0x30, 0x30));

  /// <summary>
  /// Purple, per §45.5's table — and the same family the composition bar's four bands are shaded
  /// from, so a memory page agrees with itself. It was teal, which put a memory sparkline in one
  /// colour and the bar below it in another.
  /// </summary>
  public static Color Memory => Pick("memory", Color.FromArgb(0xFF, 0x9E, 0x86, 0xC8));

  public static Color Io => Pick("io", Color.FromArgb(0xFF, 0xE0, 0xC0, 0x30));

  /// <summary>
  /// The plot background: black, as the reference tools use.
  /// </summary>
  /// <remarks>
  /// Black under a high-contrast scheme too, and that is a decision rather than an oversight. A plot
  /// here is an instrument with a ground of its own — like a meter face, not like a panel — and black
  /// under bright inks is the highest contrast there is. What a high-contrast desktop is promising is
  /// that nothing is a blend of two colours, and a flat black is not one. What did need changing is
  /// what is drawn <em>on</em> it: see <see cref="PlotGrid"/> and the axis inks.
  /// </remarks>
  public static Color PlotBackground => Pick("plot.background", Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A));

  /// <summary>
  /// The graticule over the plot ground (PRD §45.4, §45.9).
  /// </summary>
  /// <remarks>
  /// A very dark green normally, which is what a graticule should be: present when looked for and
  /// invisible when not. Under a high-contrast scheme that is precisely the wrong answer — the whole
  /// point of the scheme is that nothing is faint — so the lines come up to something readable.
  /// </remarks>
  public static Color PlotGrid(ITheme theme) {
    ArgumentNullException.ThrowIfNull(theme);
    return theme.IsHighContrast && !Chosen("plot.grid")
      ? Color.FromArgb(0xFF, 0x3C, 0x9C, 0x3C)
      : Pick("plot.grid", Color.FromArgb(0xFF, 0x14, 0x3C, 0x14));
  }

  /// <summary>
  /// The ink for a plot's caption, its axis labels and its cursor (PRD §45.9).
  /// </summary>
  /// <remarks>
  /// Three greens of decreasing strength normally, so that the caption reads first and the axis
  /// second. Under a high-contrast scheme they all come up to white: a hierarchy made of three
  /// shades of one hue is a hierarchy nobody running that scheme can see, and legibility beats
  /// ranking when they conflict.
  /// </remarks>
  public static Color PlotInk(ITheme theme, PlotInkKind kind) {
    ArgumentNullException.ThrowIfNull(theme);
    if (theme.IsHighContrast)
      return Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    return kind switch {
      PlotInkKind.Caption => Color.FromArgb(0xFF, 0x9C, 0xE8, 0x9C),
      PlotInkKind.Axis => Color.FromArgb(0xFF, 0x6E, 0xA8, 0x6E),
      _ => Color.FromArgb(0xFF, 0xC8, 0xE8, 0xC8),
    };
  }

  /// <summary>
  /// The drop shadow behind an axis label, or null where there must not be one.
  /// </summary>
  /// <remarks>
  /// The shadow exists so a label stays readable where a filled series runs underneath it. It is
  /// also a second colour half a pixel from the first, which is the one thing a high-contrast scheme
  /// is asking not to happen — so under that scheme there is no shadow and the label is simply
  /// white.
  /// </remarks>
  public static Color? PlotInkShadow(ITheme theme) {
    ArgumentNullException.ThrowIfNull(theme);
    return theme.IsHighContrast ? null : Color.FromArgb(0xFF, 0x08, 0x18, 0x08);
  }

  /// <summary>
  /// Whether a background is dark enough to need the dark palette. Perceptual weights, because a
  /// mid-green and a mid-blue of the same arithmetic mean look nothing alike.
  /// </summary>
  private static bool IsDark(Color color)
    => color.R * 0.299 + color.G * 0.587 + color.B * 0.114 < 128;

}

/// <summary>Which of a plot's three inks is wanted (PRD §45.4).</summary>
public enum PlotInkKind {

  /// <summary>The plot's own name, in its corner — the first thing read.</summary>
  Caption,

  /// <summary>The axis labels along the edges.</summary>
  Axis,

  /// <summary>The vertical line under the pointer or the keyboard cursor.</summary>
  Cursor,

}
