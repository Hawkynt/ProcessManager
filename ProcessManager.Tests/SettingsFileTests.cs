using Hawkynt.ProcessManager.App;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Where the settings file lives, and the three things somebody can do to it from outside the
/// program (PRD §67).
/// </summary>
/// <remarks>
/// The round trip is the assertion that matters throughout: a program that rewrites its settings
/// every second is the worst possible one to be careless about what it drops, so every path through
/// here is checked with an unrecognised key in the file — the one thing no schema can describe and
/// the one thing an older build must not eat.
/// </remarks>
[TestFixture]
public sealed class SettingsFileTests {

  private string _directory = string.Empty;

  [SetUp]
  public void MakeDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), $"procman-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void RemoveDirectory() {
    // Best-effort, like the store itself: a leftover temporary directory is not worth failing a
    // test run over.
    try {
      if (Directory.Exists(this._directory))
        Directory.Delete(this._directory, recursive: true);
    } catch (IOException) {
      // nothing to do
    }
  }

  private string In(string name) => Path.Combine(this._directory, name);

  #region where it lives (PRD §67)

  [Test]
  public void APathGivenOutrightWinsOverEverythingElse() {
    var location = SettingsStore.Locate("/tmp/somewhere/settings.conf");

    Assert.That(location.Path, Is.EqualTo("/tmp/somewhere/settings.conf"));
    Assert.That(location.Placement, Is.EqualTo(SettingsPlacement.Chosen));
    Assert.That(location.Explain(), Does.Contain("--settings"));
  }

  /// <summary>
  /// The variable exists for the runs where adding an argument means editing somebody else's
  /// command line — a service unit, a wrapper script, a container entry point.
  /// </summary>
  [Test]
  public void TheEnvironmentVariableNamesTheFile() {
    var previous = Environment.GetEnvironmentVariable(SettingsStore.PathVariable);
    try {
      Environment.SetEnvironmentVariable(SettingsStore.PathVariable, this.In("named.conf"));
      var location = SettingsStore.Locate();

      Assert.That(location.Path, Is.EqualTo(this.In("named.conf")));
      Assert.That(location.Placement, Is.EqualTo(SettingsPlacement.Environment));
      Assert.That(location.Explain(), Does.Contain(SettingsStore.PathVariable));
    } finally {
      Environment.SetEnvironmentVariable(SettingsStore.PathVariable, previous);
    }
  }

  /// <summary>
  /// …but not over a path given outright. The order is the order of how deliberate each answer is,
  /// so a variable in a shell profile never overrules a flag somebody typed just now.
  /// </summary>
  [Test]
  public void TheEnvironmentVariableDoesNotOverruleAFlag() {
    var previous = Environment.GetEnvironmentVariable(SettingsStore.PathVariable);
    try {
      Environment.SetEnvironmentVariable(SettingsStore.PathVariable, this.In("named.conf"));

      Assert.That(SettingsStore.Locate(this.In("chosen.conf")).Placement, Is.EqualTo(SettingsPlacement.Chosen));
    } finally {
      Environment.SetEnvironmentVariable(SettingsStore.PathVariable, previous);
    }
  }

  /// <summary>
  /// With nothing said, the platform's own convention — which is the case every existing settings
  /// file on every machine is already in, and the one that must not have moved.
  /// </summary>
  [Test]
  public void WithNothingSaidItIsTheProfile() {
    var previous = Environment.GetEnvironmentVariable(SettingsStore.PathVariable);
    try {
      Environment.SetEnvironmentVariable(SettingsStore.PathVariable, null);
      var location = SettingsStore.Locate();

      // A test host is not a portable install, so this is the profile — and the path is the one
      // that shipped before any of this existed.
      Assert.That(location.Placement, Is.EqualTo(SettingsPlacement.Profile));
      Assert.That(location.Path, Does.Contain("procman"));
      Assert.That(location.Path, Does.EndWith(SettingsStore.FileName));
      Assert.That(Path.IsPathRooted(location.Path), Is.True);
      Assert.That(location.Explain(), Is.EqualTo(location.Path), "the default needs no explanation");
    } finally {
      Environment.SetEnvironmentVariable(SettingsStore.PathVariable, previous);
    }
  }

  [Test]
  public void EveryPlacementExplainsItselfInOneLine() {
    foreach (var placement in Enum.GetValues<SettingsPlacement>()) {
      var explanation = new SettingsLocation("/tmp/x.conf", placement).Explain();

      Assert.That(explanation, Does.StartWith("/tmp/x.conf"), placement.ToString());
      Assert.That(explanation, Does.Not.Contain("\n"), placement.ToString());
    }
  }

  #endregion

  #region the new settings

  [Test]
  public void TheDefaultsAreTheSafeAnswers() {
    var settings = new UserSettings();

    Assert.That(settings.ConfirmDestructiveActions, Is.True, "ending the wrong process is not undoable");
    Assert.That(settings.CompactPerformancePage, Is.False);
    Assert.That(settings.TerminalMouse, Is.True);
  }

  [Test]
  public void TheNewSettingsSurviveARoundTrip() {
    var original = new UserSettings {
      ConfirmDestructiveActions = false,
      CompactPerformancePage = true,
      TerminalMouse = false,
    };

    var round = UserSettings.Parse(original.Write());

    Assert.That(round.ConfirmDestructiveActions, Is.False);
    Assert.That(round.CompactPerformancePage, Is.True);
    Assert.That(round.TerminalMouse, Is.False);
  }

  /// <summary>
  /// Not written when they are what they always were: a line in everybody's file saying the program
  /// asks before it kills something is a line nobody reads.
  /// </summary>
  [Test]
  public void TheDefaultsAreNotWrittenOut() {
    var text = new UserSettings().Write();

    Assert.That(text, Does.Not.Contain("confirm.destructive"));
    Assert.That(text, Does.Not.Contain("performance.density"));
    Assert.That(text, Does.Not.Contain("tui.mouse"));
  }

  /// <summary>
  /// The rule of §67 that applies to every key here, and to this one especially: a line that will
  /// not parse leaves the setting where it was. A typo must not switch a confirmation off.
  /// </summary>
  [Test]
  public void ALineThatWillNotParseLeavesTheSettingAlone() {
    Assert.That(UserSettings.Parse("confirm.destructive=perhaps").ConfirmDestructiveActions, Is.True);
    Assert.That(UserSettings.Parse("performance.density=roomy").CompactPerformancePage, Is.False);
    Assert.That(UserSettings.Parse("tui.mouse=sometimes").TerminalMouse, Is.True);
  }

  [Test]
  public void DensityIsWrittenAsAWordRatherThanABoolean() {
    var text = new UserSettings { CompactPerformancePage = true }.Write();

    Assert.That(text, Does.Contain("performance.density=compact"));
    Assert.That(UserSettings.Parse("performance.density=comfortable").CompactPerformancePage, Is.False);
  }

  #endregion

  #region export, import and reset (PRD §67)

  /// <summary>
  /// The honest test of a settings file: write one, read it back, and confirm every key survived —
  /// including one this build does not recognise.
  /// </summary>
  [Test]
  public void AnExportAndAnImportCarryAKeyThisBuildDoesNotKnow() {
    var live = this.In("settings.conf");
    var copy = this.In("backup.conf");
    File.WriteAllText(live, "interval=3\nsomething.from.the.future=42\nconfirm.destructive=false\n");

    Assert.That(SettingsCommand.Run(SettingsAction.Export, copy, live), Is.Zero);
    Assert.That(File.ReadAllText(copy), Does.Contain("something.from.the.future=42"));

    // And back, onto a file that says something else entirely.
    var other = this.In("other.conf");
    File.WriteAllText(other, "interval=10\n");
    Assert.That(SettingsCommand.Run(SettingsAction.Import, copy, other), Is.Zero);

    var imported = SettingsStore.Load(other);
    Assert.That(imported.IntervalSeconds, Is.EqualTo(3));
    Assert.That(imported.ConfirmDestructiveActions, Is.False);
    Assert.That(imported.Unknown, Does.Contain("something.from.the.future=42"));
  }

  [Test]
  public void ExportingTwiceProducesTheSameFile() {
    var live = this.In("settings.conf");
    File.WriteAllText(live, "interval=3\nsomething.from.the.future=42\n");

    Assert.That(SettingsCommand.Run(SettingsAction.Export, this.In("a.conf"), live), Is.Zero);
    Assert.That(SettingsCommand.Run(SettingsAction.Export, this.In("b.conf"), this.In("a.conf")), Is.Zero);

    Assert.That(File.ReadAllText(this.In("b.conf")), Is.EqualTo(File.ReadAllText(this.In("a.conf"))));
  }

  [Test]
  public void ResettingRemovesTheFile() {
    var live = this.In("settings.conf");
    File.WriteAllText(live, "interval=3\n");

    Assert.That(SettingsCommand.Run(SettingsAction.Reset, null, live), Is.Zero);
    Assert.That(File.Exists(live), Is.False);
    Assert.That(SettingsStore.Load(live).IntervalSeconds, Is.EqualTo(1), "and the defaults come back");
  }

  /// <summary>
  /// Removing a file that is not there is what somebody asked for, not a failure — the same
  /// best-effort rule the rest of the store follows (PRD §81).
  /// </summary>
  [Test]
  public void ResettingWhenThereIsNoFileIsNotAFailure()
    => Assert.That(SettingsCommand.Run(SettingsAction.Reset, null, this.In("never-written.conf")), Is.Zero);

  [Test]
  public void ImportingAFileThatIsNotThereFailsRatherThanEmptyingTheSettings() {
    var live = this.In("settings.conf");
    File.WriteAllText(live, "interval=3\n");

    Assert.That(SettingsCommand.Run(SettingsAction.Import, this.In("missing.conf"), live), Is.EqualTo(1));
    Assert.That(SettingsStore.Load(live).IntervalSeconds, Is.EqualTo(3), "and the settings are untouched");
  }

  [Test]
  public void TheTransferVerbsNeedAPath() {
    Assert.That(SettingsCommand.Run(SettingsAction.Export, null, this.In("settings.conf")), Is.EqualTo(1));
    Assert.That(SettingsCommand.Run(SettingsAction.Import, null, this.In("settings.conf")), Is.EqualTo(1));
  }

  [Test]
  public void ShowingThePathWorksWhetherOrNotThereIsAFile() {
    var live = this.In("settings.conf");

    Assert.That(SettingsCommand.Run(SettingsAction.Show, null, live), Is.Zero);
    File.WriteAllText(live, "interval=3\n");
    Assert.That(SettingsCommand.Run(SettingsAction.Show, null, live), Is.Zero);
  }

  #endregion

  #region the command line

  [Test]
  public void TheFourSwitchesReachTheSettingsMode() {
    Assert.That(CommandLineOptions.Parse(["--settings-path"]).SettingsAction, Is.EqualTo(SettingsAction.Show));
    Assert.That(CommandLineOptions.Parse(["--reset-settings"]).SettingsAction, Is.EqualTo(SettingsAction.Reset));

    var exported = CommandLineOptions.Parse(["--export-settings", "/tmp/out.conf"]);
    Assert.That(exported.Mode, Is.EqualTo(RunMode.Settings));
    Assert.That(exported.SettingsAction, Is.EqualTo(SettingsAction.Export));
    Assert.That(exported.SettingsTransferPath, Is.EqualTo("/tmp/out.conf"));

    var imported = CommandLineOptions.Parse(["--import-settings=/tmp/in.conf"]);
    Assert.That(imported.SettingsAction, Is.EqualTo(SettingsAction.Import));
    Assert.That(imported.SettingsTransferPath, Is.EqualTo("/tmp/in.conf"));
  }

  [Test]
  public void ATransferSwitchWithNoPathIsRefusedRatherThanGuessed() {
    Assert.That(CommandLineOptions.Parse(["--export-settings"]).Error, Is.Not.Null);
    Assert.That(CommandLineOptions.Parse(["--import-settings"]).Error, Is.Not.Null);
  }

  /// <summary>
  /// The persistent form of <c>--no-mouse</c>, layered the way every other setting is: the file
  /// seeds it and the flag beats it.
  /// </summary>
  [Test]
  public void TheTerminalMouseSettingSeedsTheRunAndTheFlagStillWins() {
    Assert.That(CommandLineOptions.Parse([], new UserSettings()).UseMouse, Is.True);
    Assert.That(CommandLineOptions.Parse([], new UserSettings { TerminalMouse = false }).UseMouse, Is.False);
    Assert.That(CommandLineOptions.Parse(["--no-mouse"], new UserSettings()).UseMouse, Is.False);
  }

  [Test]
  public void EveryNewSwitchIsInTheHelpText() {
    foreach (var switchName in new[] {
      "--settings-path", "--export-settings", "--import-settings", "--reset-settings",
    })
      Assert.That(CommandLineOptions.HelpText, Does.Contain(switchName), switchName);
  }

  #endregion

}
