using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The settings box: what it shows, what it gives back, and the parts of the file it must not
/// disturb (PRD §67).
/// </summary>
/// <remarks>
/// Testable without a display for the reason the rest of the window is: the controls hold real state
/// before anything is realised. What cannot be checked here is whether a row landed under the
/// buttons, which is what the capture leg is for.
/// </remarks>
[TestFixture]
public sealed class SettingsDialogTests {

  private static SettingsLocation Somewhere => new("/tmp/procman/settings.conf", SettingsPlacement.Profile);

  private static SettingsDialog Dialog(UserSettings settings) => new(settings, Somewhere);

  [Test]
  public void ItOpensShowingWhatTheFileSays() {
    var dialog = Dialog(new() {
      ConfirmDestructiveActions = false,
      CompactPerformancePage = true,
      TerminalMouse = false,
      BlockCharacters = false,
      IntervalSeconds = 5,
      Grouping = ProcessGrouping.User,
      CpuMode = CpuPercentMode.PerCore,
    });

    var shown = dialog.Description;
    Assert.That(shown, Does.Contain("[ ] Ask before ending"));
    Assert.That(shown, Does.Contain("[x] Open the performance page tightened up"));
    Assert.That(shown, Does.Contain("[ ] Terminal: read the mouse"));
    Assert.That(shown, Does.Contain("5 s"));
    Assert.That(shown, Does.Contain("the account that owns them"));
    Assert.That(shown, Does.Contain("a share of one core"));
  }

  /// <summary>
  /// A box that shows nothing is a box that photographs as a grey rectangle, and the picture and the
  /// assertion look identical. The count is what tells them apart.
  /// </summary>
  [Test]
  public void EverySettingItClaimsToOfferIsThere() {
    var dialog = Dialog(new());

    Assert.That(dialog.RowCount, Is.GreaterThanOrEqualTo(14));
    Assert.That(dialog.Description.Split('\n'), Has.Length.EqualTo(dialog.RowCount + 1), "and the file line");
  }

  [Test]
  public void TheBoxNamesTheFileItIsEditing() {
    Assert.That(Dialog(new()).Description, Does.Contain("/tmp/procman/settings.conf"));

    var portable = new SettingsDialog(new(), new("/media/stick/settings.conf", SettingsPlacement.Portable));
    Assert.That(portable.Description, Does.Contain("portable"));
  }

  #region what comes back out

  [Test]
  public void WhatIsOnScreenIsWhatComesBack() {
    var dialog = Dialog(new());
    var settings = dialog.Settings;

    // Unchanged, because nothing was touched — the round trip through a box nobody used must be the
    // identity, or opening the settings and pressing OK would rewrite somebody's file.
    Assert.That(settings.ConfirmDestructiveActions, Is.True);
    Assert.That(settings.CompactPerformancePage, Is.False);
    Assert.That(settings.TerminalMouse, Is.True);
    Assert.That(settings.BlockCharacters, Is.True);
    Assert.That(settings.IntervalSeconds, Is.EqualTo(1));
    Assert.That(settings.Grouping, Is.EqualTo(ProcessGrouping.ParentTree));
    Assert.That(settings.CpuMode, Is.EqualTo(CpuPercentMode.Normalized));
  }

  /// <summary>
  /// The rule that makes this box safe to open on a file it does not fully understand: everything it
  /// has no control for comes out exactly as it went in.
  /// </summary>
  [Test]
  public void EverythingTheBoxCannotShowIsCarriedThroughUntouched() {
    var original = new UserSettings {
      Unknown = ["something.from.the.future=42"],
      ColumnSets = new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase) {
        ["mine"] = [ProcessField.Name, ProcessField.Elevated],
      },
      Colours = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { ["new"] = 0xFF112233 },
      DesktopColumns = [ProcessField.Pid, ProcessField.Name],
      DesktopColumnWidths = [new(ProcessField.Name, 240)],
      PinnedDesktopColumns = 2,
      WindowWidth = 1400,
      WindowHeight = 900,
      SplitPercent = 62,
      Thresholds = UsageThresholds.Default with { WarmCpuPercent = 12 },
    };

    var settings = Dialog(original).Settings;

    Assert.That(settings.Unknown, Does.Contain("something.from.the.future=42"));
    Assert.That(settings.ColumnSets["mine"], Is.EqualTo(new[] { ProcessField.Name, ProcessField.Elevated }));
    Assert.That(settings.Colours["new"], Is.EqualTo(0xFF112233));
    Assert.That(settings.DesktopColumns, Is.EqualTo(new[] { ProcessField.Pid, ProcessField.Name }));
    Assert.That(settings.DesktopColumnWidths, Is.EqualTo(original.DesktopColumnWidths));
    Assert.That(settings.PinnedDesktopColumns, Is.EqualTo(2));
    Assert.That(settings.WindowWidth, Is.EqualTo(1400));
    Assert.That(settings.WindowHeight, Is.EqualTo(900));
    Assert.That(settings.SplitPercent, Is.EqualTo(62));
    Assert.That(settings.Thresholds.WarmCpuPercent, Is.EqualTo(12));
  }

  /// <summary>
  /// The picker offers six rates and the file takes any number, so a file saying three seconds has
  /// to come back as the nearest offered rate rather than as the default (PRD §12).
  /// </summary>
  [TestCase(3.0, "2 s")]
  [TestCase(0.3, "250 ms")]
  [TestCase(9.0, "10 s")]
  public void AnIntervalThePickerDoesNotOfferLandsOnTheNearestOneItDoes(double seconds, string expected)
    => Assert.That(Dialog(new() { IntervalSeconds = seconds }).Description, Does.Contain(expected));

  /// <summary>
  /// A pause is not written down and "by hand" is: they are two different statements, and the rate
  /// underneath survives either (PRD §12).
  /// </summary>
  [Test]
  public void ByHandIsAnEntryOfItsOwnAndKeepsTheRateUnderneath() {
    var dialog = Dialog(new() { ManualRefresh = true, IntervalSeconds = 5 });

    Assert.That(dialog.Description, Does.Contain("by hand"));
    Assert.That(dialog.Settings.ManualRefresh, Is.True);
    Assert.That(dialog.Settings.IntervalSeconds, Is.EqualTo(5), "the rate is kept, not reset");
  }

  /// <summary>
  /// Every grouping §83 defines is offered, or one of them would be reachable from the command line
  /// and from nowhere in the window — the complaint §91 makes about a feature nobody can find.
  /// </summary>
  [Test]
  public void EveryGroupingIsOffered() {
    foreach (var grouping in Enum.GetValues<ProcessGrouping>()) {
      var dialog = Dialog(new() { Grouping = grouping });

      Assert.That(dialog.Settings.Grouping, Is.EqualTo(grouping), UserSettings.NameOfGrouping(grouping));
    }
  }

  #endregion

  /// <summary>
  /// Nothing in the box may be a control that writes a key nothing reads. Every checkbox and picker
  /// here has to move a setting the program acts on, and the cheapest way to hold that line is to
  /// assert the box round-trips through the file it claims to write.
  /// </summary>
  [Test]
  public void EverySettingTheBoxOffersSurvivesTheFile() {
    var chosen = Dialog(new() {
      ConfirmDestructiveActions = false,
      CompactPerformancePage = true,
      TerminalMouse = false,
      BlockCharacters = false,
      HideUnavailableTabs = true,
      LowerPaneVisible = false,
      PerformanceOpensOnBusiest = false,
      IntervalSeconds = 2,
      Grouping = ProcessGrouping.Executable,
      CpuMode = CpuPercentMode.PerCore,
    }).Settings;

    var round = UserSettings.Parse(chosen.Write());

    Assert.That(round.ConfirmDestructiveActions, Is.False);
    Assert.That(round.CompactPerformancePage, Is.True);
    Assert.That(round.TerminalMouse, Is.False);
    Assert.That(round.BlockCharacters, Is.False);
    Assert.That(round.HideUnavailableTabs, Is.True);
    Assert.That(round.LowerPaneVisible, Is.False);
    Assert.That(round.PerformanceOpensOnBusiest, Is.False);
    Assert.That(round.IntervalSeconds, Is.EqualTo(2));
    Assert.That(round.Grouping, Is.EqualTo(ProcessGrouping.Executable));
    Assert.That(round.CpuMode, Is.EqualTo(CpuPercentMode.PerCore));
  }

}
