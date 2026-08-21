using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What the window says about itself to something that cannot see it (PRD §74, §45.9).
/// </summary>
/// <remarks>
/// <para>
/// Accessibility is the one part of a user interface that a screenshot cannot check: a name a screen
/// reader would say is invisible in a photograph, and a window whose every control announces as
/// "panel" photographs exactly like one that names them all. So it is asserted here instead.
/// </para>
/// <para>
/// The high-contrast half needs a theme this machine cannot be put into, so it uses a stub. That is
/// the whole reason <see cref="ITheme"/> is an interface: the palette decisions are pure functions of
/// it, and a test can hand them the desktop nobody here is running.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AccessibilityTests {

  /// <summary>A theme with one knob, which is the one thing the palette branches on.</summary>
  private sealed class StubTheme : ITheme {

    public bool IsHighContrast { get; init; }

    public Color WindowBackground => Color.White;
    public Color ControlBackground => Color.White;
    public Color ControlText => Color.Black;
    public Color DisabledText => Color.Gray;
    public Color FieldBackground { get; init; } = Color.White;
    public Color Accent => Color.Blue;
    public Color SelectionBackground => Color.FromArgb(0xFF, 0x00, 0x00, 0x80);
    public Color SelectionText => Color.White;
    public Color Border => Color.Black;
    public Color GridLine => Color.Black;
    public Color HeaderBackground => Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0);
    public Color HeaderText => Color.Black;
    public Font DefaultFont => DefaultTheme.Instance.DefaultFont;
    public int RowHeight => 17;
    public int ScrollBarSize => 14;
    public int DoubleClickTime => 500;

  }

  private static readonly StubTheme _Plain = new();
  private static readonly StubTheme _HighContrast = new() { IsHighContrast = true };

  private sealed class StubProbe : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) => snapshot.PrepareProcesses(0);
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }
  }

  private static MainWindow Window() {
    var probe = new StubProbe();
    return new(new Sampler(probe), probe, null);
  }

  #region naming (PRD §74)

  /// <summary>
  /// Half the window is owner-drawn, and an owner-drawn control has no text to fall back on: unnamed,
  /// a plot, a meter strip and a status line all announce as nothing at all.
  /// </summary>
  [Test]
  public void EveryPartOfTheWindowSaysWhatItIs() {
    var window = Window();
    var named = new List<string>();
    Walk(window, named);

    foreach (var expected in new[] {
      "Processes", "Filter", "Status", "Views", "Commands", "System totals",
      "Processor history", "Memory history", "Logical processors",
      "Details for the selected process",
    })
      Assert.That(named, Does.Contain(expected), $"nothing in the window announces as \"{expected}\"");
  }

  /// <summary>
  /// A name without a role is announced as "Processor history, panel". The roles exist so that a
  /// reader is told a graph is a graph and a list is a list.
  /// </summary>
  [Test]
  public void TheThingsThatAreNotPanelsSaySoToo() {
    var window = Window();
    var roles = new Dictionary<string, AccessibleRole>(StringComparer.Ordinal);
    Walk(window, null, roles);

    Assert.That(roles["Processes"], Is.EqualTo(AccessibleRole.Tree));
    Assert.That(roles["Filter"], Is.EqualTo(AccessibleRole.Text));
    Assert.That(roles["Processor history"], Is.EqualTo(AccessibleRole.Graphic));
    Assert.That(roles["Memory history"], Is.EqualTo(AccessibleRole.Graphic));
    Assert.That(roles["Logical processors"], Is.EqualTo(AccessibleRole.Graphic));
    Assert.That(roles["Commands"], Is.EqualTo(AccessibleRole.ToolBar));
    Assert.That(roles["Status"], Is.EqualTo(AccessibleRole.StaticText));
  }

  /// <summary>
  /// The toolkit docks by walking its children backwards, so the order they were added in is very
  /// nearly the reverse of the order they are read in. Tab must follow the reading order (PRD §74).
  /// </summary>
  [Test]
  public void TabFollowsTheReadingOrderRatherThanTheOrderThingsWereAdded() {
    var window = Window();
    var indices = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var child in window.Controls)
      if (child.AccessibleName is { Length: > 0 } name)
        indices[name] = child.TabIndex;

    Assert.That(indices["Commands"], Is.LessThan(indices["System totals"]));
    Assert.That(indices["System totals"], Is.LessThan(indices["Views"]));
    Assert.That(indices["Views"], Is.LessThan(indices["Filter bar"]));
    Assert.That(indices["Filter bar"], Is.LessThan(indices["Status"]));
  }

  #endregion

  #region graphs in words (PRD §74, §45.9)

  /// <summary>
  /// §74's "graphs expose textual summaries". A graph whose only content is a picture says nothing at
  /// all to somebody who cannot see it, and the numbers behind it already exist.
  /// </summary>
  [Test]
  public void EveryGraphInTheWindowHasADescriptionMadeOfItsOwnNumbers() {
    var window = Window();
    var descriptions = new Dictionary<string, string?>(StringComparer.Ordinal);
    Walk(window, null, null, descriptions);

    foreach (var graph in new[] { "Processor history", "Memory history", "Logical processors" })
      Assert.That(descriptions[graph], Is.Not.Null.And.Not.Empty, $"{graph} has nothing to say");
  }

  /// <summary>
  /// The core map is the one control in the window whose reading exists only as a colour — sixty-four
  /// cells with not a digit on them. Its summary is therefore not a nicety.
  /// </summary>
  [Test]
  public void TheCoreMapSaysWhatItWouldHaveShown() {
    var map = new CoreHeatmap();

    // Nothing sampled yet: a reason rather than a row of noughts (PRD §72.3).
    Assert.That(map.Statistics(), Is.Not.Empty);
    Assert.That(map.Statistics(), Does.Not.Contain("0 %"), "an unsampled map must not read as idle");
  }

  #endregion

  #region high contrast (PRD §45.9, §74)

  /// <summary>
  /// A high-contrast scheme is a promise that the foreground and background are a readable pair.
  /// Every wash this program paints is a third colour laid between them.
  /// </summary>
  [Test]
  public void NoWashIsPaintedUnderAHighContrastScheme() {
    RowPalette.Apply(new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));

    foreach (var category in Enum.GetValues<ProcessCategory>()) {
      Assert.That(RowPalette.BackColorOf(category, _HighContrast), Is.Null, category.ToString());
    }

    foreach (var heat in Enum.GetValues<UsageHeat>())
      Assert.That(RowPalette.HeatColour(heat, _HighContrast), Is.Null, heat.ToString());
  }

  /// <summary>…and every one of them is still painted on an ordinary desktop.</summary>
  [Test]
  public void TheWashesAreUnchangedEverywhereElse() {
    RowPalette.Apply(new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));

    Assert.That(RowPalette.BackColorOf(ProcessCategory.New, _Plain), Is.Not.Null);
    Assert.That(RowPalette.HeatColour(UsageHeat.Hot, _Plain), Is.Not.Null);
    Assert.That(RowPalette.HeatColour(UsageHeat.None, _Plain), Is.Null, "no heat is still no colour");
  }

  /// <summary>
  /// A colour the file names outright is still painted: somebody who wrote it down while running a
  /// high-contrast theme has said what they want, and second-guessing them makes the setting a
  /// suggestion.
  /// </summary>
  [Test]
  public void AColourTheFileNamesIsPaintedEvenThere() {
    try {
      RowPalette.Apply(new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) {
        ["new"] = 0xFF102030,
      });

      Assert.That(RowPalette.BackColorOf(ProcessCategory.New, _HighContrast)?.ToArgb(), Is.EqualTo(unchecked((int)0xFF102030)));
      Assert.That(RowPalette.BackColorOf(ProcessCategory.Exited, _HighContrast), Is.Null, "and only that one");
    } finally {
      RowPalette.Apply(new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));
    }
  }

  /// <summary>
  /// The match highlight is the exception, and for the opposite reason: a matched run has no other
  /// carrier in the cell, so dropping it would leave a search whose result is invisible.
  /// </summary>
  [Test]
  public void TheMatchHighlightSurvivesAndComesFromTheThemeItself() {
    RowPalette.Apply(new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));

    Assert.That(RowPalette.MatchHighlight(_HighContrast), Is.EqualTo(_HighContrast.SelectionBackground));
    Assert.That(RowPalette.GroupHeading(_HighContrast), Is.EqualTo(_HighContrast.HeaderBackground));
  }

  /// <summary>
  /// A graticule is meant to be faint, which is precisely what a high-contrast scheme is asking not
  /// to happen; and a drop shadow is two colours a pixel apart, which is the same request again.
  /// </summary>
  [Test]
  public void ThePlotInksComeUpAndTheShadowGoesAway() {
    RowPalette.Apply(new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));

    Assert.That(RowPalette.PlotGrid(_HighContrast), Is.Not.EqualTo(RowPalette.PlotGrid(_Plain)));
    Assert.That(Luminance(RowPalette.PlotGrid(_HighContrast)), Is.GreaterThan(Luminance(RowPalette.PlotGrid(_Plain))));
    Assert.That(RowPalette.PlotInkShadow(_HighContrast), Is.Null);
    Assert.That(RowPalette.PlotInkShadow(_Plain), Is.Not.Null);

    foreach (var kind in Enum.GetValues<PlotInkKind>())
      Assert.That(RowPalette.PlotInk(_HighContrast, kind), Is.EqualTo(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)), kind.ToString());
  }

  /// <summary>
  /// A legend explaining twelve colours that are not being painted is a legend that has quietly
  /// become wrong, which is worse than no legend at all.
  /// </summary>
  [Test]
  public void TheLegendAdmitsWhenItsColoursAreOff() {
    var normal = new LegendWindow(UsageThresholds.Default, highContrast: false);
    var contrast = new LegendWindow(UsageThresholds.Default, highContrast: true);

    Assert.That(contrast.Description, Does.Contain("This desktop is high-contrast"));
    Assert.That(normal.Description, Does.Not.Contain("This desktop is high-contrast"));
    Assert.That(normal.Description, Does.Contain("high-contrast"), "and says what would happen");

    // Both are the same height: a window whose layout depends on the desktop's scheme is one whose
    // last row lands under the buttons on exactly one kind of machine.
    Assert.That(contrast.Height, Is.EqualTo(normal.Height));
  }

  #endregion

  #region nothing left unnamed (PRD §74, §99)

  /// <summary>
  /// The tests above name the controls they expect and check each one. That list is a thing that
  /// goes stale the next time somebody adds a control — it cannot fail for a control nobody thought
  /// to put in it. This sweeps the real tree instead: every control that has no text of its own has
  /// to answer for itself, and a new textless one fails here until it is named.
  /// </summary>
  private static void SweepForUnnamed(Control root) {
    var unnamed = new List<string>();
    foreach (var control in Descendants(root)) {
      // Scaffolding holds other controls and is announced through what it holds. Naming a layout
      // panel adds a stop on the way to everything inside it and says nothing.
      if (control is Panel or SplitContainer or Label or GroupBox or TabPage || control.Text.Length > 0)
        continue;

      if (string.IsNullOrEmpty(control.AccessibleName))
        unnamed.Add(control.GetType().Name);
    }

    Assert.That(unnamed, Is.Empty, "these are announced as their role and nothing else");
  }

  private static IEnumerable<Control> Descendants(Control root) {
    foreach (Control child in root.Controls) {
      yield return child;
      foreach (var deeper in Descendants(child))
        yield return deeper;
    }
  }

  [Test]
  public void NothingInTheProcessWindowIsLeftUnnamed() => SweepForUnnamed(Window());

  /// <summary>
  /// The lower pane is built by a different type and its six lists are reached by a reader who moves
  /// off the tab strip into the page — where, unnamed, they announce only that they are tables.
  /// </summary>
  [Test]
  public void NothingInTheDetailPaneIsLeftUnnamed() {
    using var pane = new DetailPane(new StubProbe());
    SweepForUnnamed(pane.Control);
  }

  [Test]
  public void NothingInThePropertiesWindowIsLeftUnnamed()
    => SweepForUnnamed(new ProcessPropertiesWindow(new StubProbe(), new(4242, 100), "editor"));

  [Test]
  public void NothingInThePerformanceWindowIsLeftUnnamed() {
    var probe = new StubProbe();
    var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();

    SweepForUnnamed(new PerformanceWindow(probe, sampler, openOnBusiest: false));
  }

  /// <summary>
  /// A name is not a duplicate of the role. "Tree", "Table", "Text box" as a name announces the
  /// control twice and identifies it not at all — and it is the failure a sweep invites, because a
  /// name is easy to add and easy to add badly.
  /// </summary>
  [Test]
  public void NoNameIsJustTheKindOfControlAgain() {
    foreach (var control in Descendants(Window())) {
      if (control.AccessibleName is not { Length: > 0 } name)
        continue;

      foreach (var role in (string[])["tree", "table", "list", "text box", "textbox", "panel", "button", "control"])
        Assert.That(name, Is.Not.EqualTo(role).IgnoreCase, $"{control.GetType().Name} is named after its own kind");
    }
  }

  #endregion

  private static double Luminance(Color color)
    => (color.R * 0.299) + (color.G * 0.587) + (color.B * 0.114);

  /// <summary>Every control under <paramref name="parent"/>, by whatever it announces itself as.</summary>
  private static void Walk(
    Control parent,
    List<string>? names,
    Dictionary<string, AccessibleRole>? roles = null,
    Dictionary<string, string?>? descriptions = null
  ) {
    foreach (var child in parent.Controls) {
      if (child.AccessibleName is { Length: > 0 } name) {
        names?.Add(name);
        if (roles is not null)
          roles[name] = child.AccessibleRole;

        if (descriptions is not null)
          descriptions[name] = child.AccessibleDescription;
      }

      Walk(child, names, roles, descriptions);
    }
  }

}
