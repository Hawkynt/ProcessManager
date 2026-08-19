using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Settings that save themselves (PRD §11, §67).
/// </summary>
/// <remarks>
/// The file used to be written only by <c>--save-settings</c>, so every preference set through the
/// window was gone by the next start unless somebody knew to run the program again from a terminal
/// with a flag.
/// </remarks>
[TestFixture]
public sealed class SettingsAutoSaveTests {

  #region the new keys

  [Test]
  public void AColourSurvivesTheRoundTrip() {
    var settings = new UserSettings {
      Colours = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { ["new"] = 0xFF123456 },
    };

    var reread = UserSettings.Parse(settings.Write());

    Assert.That(reread.Colours["new"], Is.EqualTo(0xFF123456));
  }

  [Test]
  public void ColoursAreWrittenInTheFormPeopleTypeThemIn() {
    var text = new UserSettings {
      Colours = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { ["cpu"] = 0xFF28C828 },
    }.Write();

    Assert.That(text, Does.Contain("color.cpu=#28c828"));
  }

  [Test]
  public void ThreeDigitColoursAreExpandedTheWayCssDoes() {
    var settings = UserSettings.Parse("color.memory=#08f");

    Assert.That(settings.Colours["memory"], Is.EqualTo(0xFF0088FFu));
  }

  [Test]
  public void AColourWithoutItsHashIsStillAColour() =>
    Assert.That(UserSettings.Parse("color.io=e0c030").Colours["io"], Is.EqualTo(0xFFE0C030u));

  /// <summary>
  /// A colour that will not parse is kept as an unknown line rather than dropped, so somebody who
  /// mistypes one finds it still there to correct instead of silently deleted.
  /// </summary>
  [Test]
  public void AColourThatMakesNoSenseIsKeptRatherThanEaten() {
    var settings = UserSettings.Parse("color.new=not-a-colour");

    Assert.That(settings.Colours, Is.Empty);
    Assert.That(settings.Unknown, Does.Contain("color.new=not-a-colour"));
    Assert.That(settings.Write(), Does.Contain("not-a-colour"));
  }

  /// <summary>
  /// The alpha is never taken from the file. A row painted half-transparent is a bug report, not a
  /// preference somebody meant to express.
  /// </summary>
  [Test]
  public void EveryColourComesBackFullyOpaque() =>
    Assert.That(UserSettings.Parse("color.own=000000").Colours["own"] >> 24, Is.EqualTo(0xFF));

  [Test]
  public void TheWindowGeometrySurvivesTheRoundTrip() {
    var settings = new UserSettings { WindowWidth = 1400, WindowHeight = 900, SplitPercent = 62 };
    var reread = UserSettings.Parse(settings.Write());

    Assert.That(reread.WindowWidth, Is.EqualTo(1400));
    Assert.That(reread.WindowHeight, Is.EqualTo(900));
    Assert.That(reread.SplitPercent, Is.EqualTo(62));
  }

  /// <summary>
  /// A window remembered as four pixels wide cannot be closed. Nothing outside the sane range is
  /// accepted, and rejecting it leaves the default rather than failing the file.
  /// </summary>
  [Test]
  public void AnAbsurdGeometryIsIgnoredRatherThanObeyed() {
    var settings = UserSettings.Parse("window.width=4\nwindow.height=999999\nwindow.split=140");

    Assert.That(settings.WindowWidth, Is.Zero);
    Assert.That(settings.WindowHeight, Is.Zero);
    Assert.That(settings.SplitPercent, Is.Zero);
  }

  /// <summary>Nothing to remember is written as nothing, not as a row of zeroes.</summary>
  [Test]
  public void ADefaultSettingsFileMentionsNoGeometryAtAll() {
    var text = new UserSettings().Write();

    Assert.That(text, Does.Not.Contain("window."));
    Assert.That(text, Does.Not.Contain("color."));
  }

  #endregion

  #region the saver

  private static SettingsAutoSaver Saver(Func<UserSettings> current, List<UserSettings> written)
    => new(current, settings => {
      written.Add(settings);
      return true;
    });

  [Test]
  public void NothingChangingWritesNothing() {
    var settings = new UserSettings();
    var written = new List<UserSettings>();
    var saver = Saver(() => settings, written);
    saver.Prime(settings);

    for (var i = 0; i < 10; ++i)
      Assert.That(saver.Flush(), Is.False);

    Assert.That(written, Is.Empty);
    Assert.That(saver.Writes, Is.Zero);
  }

  [Test]
  public void AChangeIsWrittenOnce() {
    var settings = new UserSettings();
    var written = new List<UserSettings>();
    var saver = Saver(() => settings, written);
    saver.Prime(settings);

    settings = settings with { SortField = ProcessField.PrivateBytes };
    Assert.That(saver.Flush(), Is.True);
    Assert.That(saver.Flush(), Is.False, "and not again while it stays that way");
    Assert.That(written, Has.Count.EqualTo(1));
  }

  /// <summary>
  /// Not priming would rewrite the file on the first tick of every run, which is how a settings file
  /// acquires a modification time that says nothing.
  /// </summary>
  [Test]
  public void PrimingWithWhatWasLoadedStopsAPointlessFirstWrite() {
    var settings = new UserSettings { IntervalSeconds = 2.5 };
    var written = new List<UserSettings>();
    var saver = Saver(() => settings, written);
    saver.Prime(settings);

    Assert.That(saver.Flush(), Is.False);
  }

  /// <summary>
  /// A write that fails is retried on the next tick rather than being marked as done — a disk that is
  /// full at this second may not be at the next.
  /// </summary>
  [Test]
  public void AFailedWriteIsNotRememberedAsASuccess() {
    var settings = new UserSettings { TreeMode = false };
    var attempts = 0;
    var saver = new SettingsAutoSaver(() => settings, _ => {
      ++attempts;
      return false;
    });

    Assert.That(saver.Flush(), Is.False);
    Assert.That(saver.Flush(), Is.False);
    Assert.That(attempts, Is.EqualTo(2));
    Assert.That(saver.Writes, Is.Zero);
  }

  /// <summary>A window mid-teardown cannot describe itself, and that is not worth an exception.</summary>
  [Test]
  public void AWindowThatCannotDescribeItselfIsNotACrash() {
    var saver = new SettingsAutoSaver(() => throw new InvalidOperationException("gone"), _ => true);

    Assert.That(() => saver.Flush(), Throws.Nothing);
  }

  #endregion

  /// <summary>
  /// The reason the settings are compared as text: the file is what has to differ. A record that
  /// changed in a way the file cannot express is not a change worth a write.
  /// </summary>
  [Test]
  public void AChangeThatNeverReachesTheFileIsNotAChange() {
    var settings = new UserSettings();
    var written = new List<UserSettings>();
    var saver = Saver(() => settings, written);
    saver.Prime(settings);

    // Column sets are written; an empty one is not, so adding one changes nothing on disk.
    settings = settings with { ColumnSets = new Dictionary<string, ProcessField[]>() };
    Assert.That(saver.Flush(), Is.False);
    Assert.That(written, Is.Empty);
  }

}
