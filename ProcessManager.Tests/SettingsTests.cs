using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What survives a restart (PRD §11, §67), and the two rules that make a settings file safe to
/// share between versions and safe to edit by hand.
/// </summary>
[TestFixture]
public sealed class SettingsTests {

  #region reading

  [Test]
  public void TheDefaultsAreWhatTheProgramAlreadyDid() {
    var settings = new UserSettings();

    Assert.That(settings.IntervalSeconds, Is.EqualTo(1));
    Assert.That(settings.SortField, Is.EqualTo(ProcessField.CpuPercent));
    Assert.That(settings.SortDescending, Is.True);
    Assert.That(settings.TreeMode, Is.True);
    Assert.That(settings.CpuMode, Is.EqualTo(CpuPercentMode.Normalized));
    Assert.That(settings.BlockCharacters, Is.True);
  }

  [Test]
  public void EveryScalarIsRead() {
    var settings = UserSettings.Parse("""
      interval=2.5
      sort=private
      sort.descending=false
      tree=false
      cpu.mode=percore
      blocks=false
      """);

    Assert.That(settings.IntervalSeconds, Is.EqualTo(2.5));
    Assert.That(settings.SortField, Is.EqualTo(ProcessField.PrivateBytes));
    Assert.That(settings.SortDescending, Is.False);
    Assert.That(settings.TreeMode, Is.False);
    Assert.That(settings.CpuMode, Is.EqualTo(CpuPercentMode.PerCore));
    Assert.That(settings.BlockCharacters, Is.False);
  }

  [TestCase("true")]
  [TestCase("yes")]
  [TestCase("on")]
  [TestCase("1")]
  public void TheUsualSpellingsOfTrueAreAccepted(string spelling)
    => Assert.That(UserSettings.Parse($"tree={spelling}").TreeMode, Is.True);

  /// <summary>
  /// The performance page opening on whatever is busiest is what it does; the setting exists to
  /// turn that off, so only the "off" survives a round trip through the file (PRD §45.3, §67).
  /// </summary>
  [Test]
  public void OpeningThePerformancePageOnTheBusiestResourceCanBeTurnedOff() {
    Assert.That(new UserSettings().PerformanceOpensOnBusiest, Is.True);
    Assert.That(UserSettings.Parse("performance.busiest=false").PerformanceOpensOnBusiest, Is.False);
    Assert.That(UserSettings.Parse("performance.busiest=true").PerformanceOpensOnBusiest, Is.True);

    var written = (new UserSettings { PerformanceOpensOnBusiest = false }).Write();
    Assert.That(written, Does.Contain("performance.busiest=false"));
    Assert.That(UserSettings.Parse(written).PerformanceOpensOnBusiest, Is.False);
    Assert.That(new UserSettings().Write(), Does.Not.Contain("performance.busiest"));
  }

  [Test]
  public void CommentsAndBlankLinesAreIgnored() {
    var settings = UserSettings.Parse("""
      # a comment

      interval=4
      # another
      """);

    Assert.That(settings.IntervalSeconds, Is.EqualTo(4));
    Assert.That(settings.Unknown, Is.Empty);
  }

  [Test]
  public void ColumnsAreReadByKeyOrAlias() {
    var settings = UserSettings.Parse("columns.desktop=pid, memory ,name");

    Assert.That(settings.DesktopColumns, Is.EqualTo(new[] {
      ProcessField.Pid, ProcessField.PrivateBytes, ProcessField.Name,
    }));
  }

  #endregion

  #region surviving a bad or newer file

  /// <summary>
  /// Somebody diagnosing an unhealthy machine cannot afford a task manager that refuses to open
  /// because one line of its config is wrong (PRD §81).
  /// </summary>
  [Test]
  public void OneBadLineDoesNotTakeTheRestOfTheFileWithIt() {
    var settings = UserSettings.Parse("""
      interval=not-a-number
      sort=no-such-field
      tree=perhaps
      blocks=false
      """);

    Assert.That(settings.IntervalSeconds, Is.EqualTo(1), "left at the default");
    Assert.That(settings.SortField, Is.EqualTo(ProcessField.CpuPercent), "left at the default");
    Assert.That(settings.TreeMode, Is.True, "left at the default");
    Assert.That(settings.BlockCharacters, Is.False, "and the good line still applied");
  }

  [Test]
  public void AnAbsurdIntervalIsRefusedRatherThanObeyed() {
    // A zero interval is a busy loop and a negative one is nonsense; both would be obeyed by a
    // parser that only checked the number parsed.
    Assert.That(UserSettings.Parse("interval=0").IntervalSeconds, Is.EqualTo(1));
    Assert.That(UserSettings.Parse("interval=-5").IntervalSeconds, Is.EqualTo(1));
    Assert.That(UserSettings.Parse("interval=99999").IntervalSeconds, Is.EqualTo(1));
  }

  /// <summary>
  /// The rule that makes it safe to run two versions against one file: an older build must not eat
  /// a key it does not understand.
  /// </summary>
  [Test]
  public void KeysThisBuildDoesNotKnowSurviveARoundTrip() {
    var original = "interval=2\nsomething.from.the.future=42\n";
    var settings = UserSettings.Parse(original);

    Assert.That(settings.Unknown, Does.Contain("something.from.the.future=42"));
    Assert.That(settings.Write(), Does.Contain("something.from.the.future=42"));
  }

  [Test]
  public void AColumnThisBuildDoesNotKnowIsSkippedRatherThanFailingTheLine() {
    var settings = UserSettings.Parse("columns.desktop=pid,field.from.the.future,name");

    Assert.That(settings.DesktopColumns, Is.EqualTo(new[] { ProcessField.Pid, ProcessField.Name }));
  }

  #endregion

  #region writing

  [Test]
  public void WhatIsWrittenParsesBackToWhatItWas() {
    var original = new UserSettings {
      IntervalSeconds = 2.5,
      SortField = ProcessField.WorkingSetBytes,
      SortDescending = false,
      TreeMode = false,
      CpuMode = CpuPercentMode.PerCore,
      BlockCharacters = false,
      DesktopColumns = [ProcessField.Pid, ProcessField.Name, ProcessField.Swap],
      TerminalColumns = [ProcessField.Pid, ProcessField.CpuPercent],
      ColumnSets = new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase) {
        ["mine"] = [ProcessField.Name, ProcessField.Elevated],
      },
    };

    var round = UserSettings.Parse(original.Write());

    Assert.That(round.IntervalSeconds, Is.EqualTo(original.IntervalSeconds));
    Assert.That(round.SortField, Is.EqualTo(original.SortField));
    Assert.That(round.SortDescending, Is.EqualTo(original.SortDescending));
    Assert.That(round.TreeMode, Is.EqualTo(original.TreeMode));
    Assert.That(round.CpuMode, Is.EqualTo(original.CpuMode));
    Assert.That(round.BlockCharacters, Is.EqualTo(original.BlockCharacters));
    Assert.That(round.DesktopColumns, Is.EqualTo(original.DesktopColumns));
    Assert.That(round.TerminalColumns, Is.EqualTo(original.TerminalColumns));
    Assert.That(round.ColumnSets["mine"], Is.EqualTo(new[] { ProcessField.Name, ProcessField.Elevated }));
  }

  [Test]
  public void TheFileIsWrittenWithFieldKeysRatherThanEnumNames() {
    var text = new UserSettings {
      SortField = ProcessField.PrivateBytes,
      DesktopColumns = [ProcessField.WorkingSetBytes, ProcessField.CpuPercentPerCore],
    }.Write();

    // The keys are the documented, stable spelling; an enum name is neither.
    Assert.That(text, Does.Contain("sort=private"));
    Assert.That(text, Does.Contain("columns.desktop=ws,cpu.raw"));
    Assert.That(text, Does.Not.Contain("PrivateBytes"));
  }

  #endregion

  #region column sets

  [Test]
  public void ThePresetsOfSection94AreAvailableWithoutAFile() {
    var settings = new UserSettings();

    foreach (var name in new[] { "basic", "expert", "security", "io", "memory", "cpu", "forensic", "minimal" }) {
      Assert.That(settings.TryGetColumnSet(name, out var fields), Is.True, name);
      Assert.That(fields, Is.Not.Empty, name);
    }
  }

  [Test]
  public void ASavedSetReplacesAPresetOfTheSameName() {
    var settings = UserSettings.Parse("columnset.basic=pid,name");

    Assert.That(settings.TryGetColumnSet("basic", out var fields), Is.True);
    Assert.That(fields, Is.EqualTo(new[] { ProcessField.Pid, ProcessField.Name }));
  }

  [Test]
  public void SetNamesAreCaseInsensitive() {
    Assert.That(new UserSettings().TryGetColumnSet("SECURITY", out _), Is.True);
    Assert.That(UserSettings.Parse("columnset.Mine=pid").TryGetColumnSet("mine", out _), Is.True);
  }

  [Test]
  public void EverySetNamesOnlyRealFields() {
    // A preset naming a field that was renamed would silently shrink, so this is worth asserting.
    foreach (var name in new UserSettings().ColumnSetNames()) {
      Assert.That(new UserSettings().TryGetColumnSet(name, out var fields), Is.True, name);
      foreach (var field in fields)
        Assert.That(FieldRegistry.Get(field).Id, Is.EqualTo(field), $"{name} names an unregistered field");
    }
  }

  /// <summary>
  /// §11's full forensic set is the expert set plus the two halves it was missing — who a process
  /// really is, and what it is doing to the disk. A set that had drifted back to being expert with
  /// extra spelling would close that row without earning it.
  /// </summary>
  [Test]
  public void TheForensicSetHasEverythingTheExpertSetHasAndTheDetailItLacked() {
    var settings = new UserSettings();
    Assert.That(settings.TryGetColumnSet("forensic", out var forensic), Is.True);
    Assert.That(settings.TryGetColumnSet("expert", out var expert), Is.True);

    foreach (var field in expert)
      Assert.That(forensic, Does.Contain(field), $"the forensic set drops {FieldRegistry.Get(field).Key}");

    foreach (var field in new[] {
      ProcessField.EffectiveUserName, ProcessField.PrivilegeChanged, ProcessField.Capabilities,
      ProcessField.SecurityContext, ProcessField.Seccomp, ProcessField.TracerPid,
      ProcessField.ReadBytesPerSecond, ProcessField.WriteBytesPerSecond, ProcessField.HandleCount,
    })
      Assert.That(forensic, Does.Contain(field), $"the forensic set has no {FieldRegistry.Get(field).Key}");
  }

  [Test]
  public void PresetsAreNotWrittenIntoTheFile() {
    // A preset copied into everybody's settings could never be improved again.
    Assert.That(new UserSettings().Write(), Does.Not.Contain("columnset."));
  }

  #endregion

  #region the file

  [Test]
  public void AMissingFileYieldsTheDefaultsRatherThanFailing() {
    var path = Path.Combine(Path.GetTempPath(), $"procman-missing-{Guid.NewGuid():N}.conf");
    var loaded = SettingsStore.Load(path);

    // Compared field by field rather than with record equality: the record holds arrays and a
    // dictionary, which compare by reference, so two identical defaults are never "equal".
    var defaults = new UserSettings();
    Assert.That(loaded.IntervalSeconds, Is.EqualTo(defaults.IntervalSeconds));
    Assert.That(loaded.SortField, Is.EqualTo(defaults.SortField));
    Assert.That(loaded.TreeMode, Is.EqualTo(defaults.TreeMode));
    Assert.That(loaded.CpuMode, Is.EqualTo(defaults.CpuMode));
    Assert.That(loaded.BlockCharacters, Is.EqualTo(defaults.BlockCharacters));
    Assert.That(loaded.DesktopColumns, Is.Empty);
    Assert.That(loaded.ColumnSets, Is.Empty);
  }

  [Test]
  public void SavingThenLoadingReturnsTheSameSettings() {
    var path = Path.Combine(Path.GetTempPath(), $"procman-{Guid.NewGuid():N}", "settings.conf");
    try {
      var settings = new UserSettings { IntervalSeconds = 5, SortField = ProcessField.HandleCount };

      Assert.That(SettingsStore.Save(settings, path), Is.True);
      var loaded = SettingsStore.Load(path);

      Assert.That(loaded.IntervalSeconds, Is.EqualTo(5));
      Assert.That(loaded.SortField, Is.EqualTo(ProcessField.HandleCount));
    } finally {
      var directory = Path.GetDirectoryName(path);
      if (directory is not null && Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  /// <summary>
  /// Written to a neighbour and moved into place, so an interrupted write leaves the previous
  /// settings rather than a truncated file.
  /// </summary>
  [Test]
  public void SavingLeavesNoTemporaryFileBehind() {
    var directory = Path.Combine(Path.GetTempPath(), $"procman-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "settings.conf");
    try {
      SettingsStore.Save(new(), path);
      Assert.That(File.Exists(path), Is.True);
      Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  public void ThePathFollowsThePlatformsOwnConvention() {
    var path = SettingsStore.Path;

    Assert.That(path, Does.Contain("procman"));
    Assert.That(path, Does.EndWith("settings.conf"));
    Assert.That(Path.IsPathRooted(path), Is.True);
  }

  #endregion

}
