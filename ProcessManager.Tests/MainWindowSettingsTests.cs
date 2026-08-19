using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The window opening the way it was left, and keeping itself that way (PRD §11).
/// </summary>
/// <remarks>
/// Testable without a display for the reason the binder is: the controls are owner-drawn and their
/// state is real before anything is realised. What is checked is the round trip — that what the file
/// said reaches the window, and that what the window is reaches the file.
/// </remarks>
[TestFixture]
public sealed class MainWindowSettingsTests {

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

  [Test]
  public void EverythingTheFileSaysReachesTheWindow() {
    var window = Window();

    window.ApplySettings(new() {
      IntervalSeconds = 3,
      SortField = ProcessField.PrivateBytes,
      SortDescending = false,
      TreeMode = false,
      WindowWidth = 1400,
      WindowHeight = 900,
      DesktopColumns = [ProcessField.Name, ProcessField.Pid, ProcessField.UserName],
    }, _ => true);

    Assert.That(window.Interval, Is.EqualTo(3000));
    Assert.That(window.FlatMode, Is.True);
    Assert.That(window.Width, Is.EqualTo(1400));
    Assert.That(window.Height, Is.EqualTo(900));

    var described = window.DescribeSettings();
    Assert.That(described.SortField, Is.EqualTo(ProcessField.PrivateBytes));
    Assert.That(described.SortDescending, Is.False);
    Assert.That(described.DesktopColumns, Has.Length.EqualTo(3));
  }

  /// <summary>
  /// A file with nothing in it leaves the window at its own defaults, which is what a fresh install
  /// has to do.
  /// </summary>
  [Test]
  public void AnEmptyFileChangesNothing() {
    var window = Window();
    var before = (window.Width, window.Height, window.Interval);

    window.ApplySettings(new(), _ => true);

    Assert.That((window.Width, window.Height, window.Interval), Is.EqualTo(before));
  }

  /// <summary>
  /// Column sets and lines a newer build wrote are carried through untouched. A program that rewrites
  /// its settings file every second is the worst possible one to be careless about what it drops.
  /// </summary>
  [Test]
  public void WhatTheWindowDoesNotUnderstandIsKept() {
    var window = Window();
    var loaded = UserSettings.Parse(
      "columnset.mine=name,pid\nsomething.from.the.future=42\ncolor.new=#123456"
    );

    window.ApplySettings(loaded, _ => true);
    var described = window.DescribeSettings();

    Assert.That(described.ColumnSets, Does.ContainKey("mine"));
    Assert.That(described.Unknown, Does.Contain("something.from.the.future=42"));
    Assert.That(described.Colours["new"], Is.EqualTo(0xFF123456));
  }

  /// <summary>The colours reach the palette, which is the only thing that makes them visible.</summary>
  [Test]
  public void AColourInTheFileRepaintsTheRows() {
    try {
      Window().ApplySettings(UserSettings.Parse("color.system=#010203\ncolor.cpu=#040506"), _ => true);

      Assert.That(RowPalette.Cpu.ToArgb(), Is.EqualTo(unchecked((int)0xFF040506)));
      Assert.That(
        RowPalette.BackColorOf(ProcessCategory.System, NativeForms.Drawing.DefaultTheme.Instance)?.ToArgb(),
        Is.EqualTo(unchecked((int)0xFF010203))
      );
    } finally {
      // The palette is process-wide, so a test that changes it has to put it back or every test
      // that runs after this one inherits a blue that was never meant for it.
      RowPalette.Apply(new Dictionary<string, uint>());
    }
  }

  [Test]
  public void ACategoryTheFileSaysNothingAboutKeepsItsOwnColour() {
    try {
      Window().ApplySettings(UserSettings.Parse("color.system=#010203"), _ => true);

      var theme = NativeForms.Drawing.DefaultTheme.Instance;
      Assert.That(RowPalette.BackColorOf(ProcessCategory.New, theme), Is.Not.Null);
      Assert.That(
        RowPalette.BackColorOf(ProcessCategory.New, theme)?.ToArgb(),
        Is.Not.EqualTo(unchecked((int)0xFF010203))
      );
    } finally {
      RowPalette.Apply(new Dictionary<string, uint>());
    }
  }

  /// <summary>
  /// The name a category goes by in the file is the same list the file's own comment offers, or the
  /// comment is a lie the moment a category is added.
  /// </summary>
  [Test]
  public void EveryColouredCategoryIsANameTheFileAdvertises() {
    foreach (ProcessCategory category in Enum.GetValues<ProcessCategory>()) {
      var name = RowPalette.NameOf(category);
      if (name.Length == 0)
        continue;

      Assert.That(UserSettings.ColourNames, Does.Contain(name), category.ToString());
    }
  }

}
