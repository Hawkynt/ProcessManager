using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The little graph that goes in the panel (PRD §65).
/// </summary>
/// <remarks>
/// Pixels rather than a panel, because the arithmetic is where the mistakes are and a test should
/// not need a desktop to catch them. A tray icon that is wrong is wrong in a way nobody reports: it
/// is a smudge in the corner of a screen that somebody quietly stops trusting without ever working
/// out why.
/// </remarks>
[TestFixture]
public sealed class IndicatorIconTests {

  private const int _Size = 8;

  private static HistoryRing<Rate> Ring(params double?[] values) {
    var ring = new HistoryRing<Rate>(_Size);
    foreach (var value in values)
      ring.Add(value is { } number ? Rate.Of(number) : Rate.NotSampledYet);

    return ring;
  }

  private static int Filled(ReadOnlySpan<int> pixels, int column) {
    var height = 0;
    for (var row = 0; row < _Size; ++row)
      if (pixels[row * _Size + column] != 0)
        ++height;

    return height;
  }

  [Test]
  public void AFullReadingFillsItsColumn() {
    var pixels = IndicatorIcon.Render(_Size, _Size, Ring(100), 100, unchecked((int)0xFFFFFFFF));

    Assert.That(Filled(pixels, _Size - 1), Is.EqualTo(_Size));
  }

  /// <summary>
  /// Newest at the right, the way every other plot in this program runs — a tray icon that ran the
  /// other way would be the one thing on the screen that did.
  /// </summary>
  [Test]
  public void TheNewestSampleIsAtTheRight() {
    var pixels = IndicatorIcon.Render(_Size, _Size, Ring(0, 100), 100, unchecked((int)0xFFFFFFFF));

    Assert.That(Filled(pixels, _Size - 1), Is.EqualTo(_Size), "the newest");
    Assert.That(Filled(pixels, _Size - 2), Is.Zero, "the one before it");
  }

  /// <summary>
  /// Anything above nought gets at least one row, or a machine using half a per cent draws as one
  /// using none — which is the difference the icon exists to show at a glance.
  /// </summary>
  [Test]
  public void ASmallReadingStillShows()
    => Assert.That(Filled(IndicatorIcon.Render(_Size, _Size, Ring(0.4), 100, -1), _Size - 1), Is.EqualTo(1));

  /// <summary>
  /// A sample nobody could read leaves its column clear rather than drawing a bar of no height. An
  /// unreadable counter and an idle machine are different states, and the icon must not merge them
  /// any more than a column may (PRD §72.3).
  /// </summary>
  [Test]
  public void AnUnreadableSampleLeavesItsColumnClear() {
    var pixels = IndicatorIcon.Render(_Size, _Size, Ring(100, null, 100), 100, unchecked((int)0xFFFFFFFF));

    Assert.That(Filled(pixels, _Size - 2), Is.Zero, "the gap");
    Assert.That(Filled(pixels, _Size - 1), Is.EqualTo(_Size), "and the reading after it");
  }

  /// <summary>
  /// Nothing is painted where there is no history, so the panel's own background shows through. An
  /// icon that paints its own square is the one that looks wrong on somebody's light panel, and
  /// every panel is somebody's.
  /// </summary>
  [Test]
  public void TheUnfilledPartIsTransparent() {
    var pixels = IndicatorIcon.Render(_Size, _Size, Ring(50), 100, unchecked((int)0xFFFFFFFF));

    Assert.That(pixels[0], Is.Zero, "the top-left corner");
  }

  [Test]
  public void NoHistoryDrawsNothing()
    => Assert.That(IndicatorIcon.Render(_Size, _Size, null, 100, -1), Is.All.Zero);

  /// <summary>Every indicator has a name, a word and a colour, and no two share any of them.</summary>
  [Test]
  public void EachIndicatorIsTellableFromTheOthers() {
    var names = new List<string>();
    var words = new List<string>();
    var inks = new List<int>();
    foreach (var kind in Enum.GetValues<IndicatorKind>()) {
      names.Add(IndicatorIcon.Name(kind));
      words.Add(IndicatorIcon.Describe(kind));
      inks.Add(IndicatorIcon.Ink(kind));
    }

    Assert.That(names, Is.Unique);
    Assert.That(words, Is.Unique);
    Assert.That(inks, Is.Unique);
  }

  #region and the setting that decides whether there is a tray at all

  /// <summary>
  /// Nothing appears unless it was asked for. A program that puts icons in somebody's panel without
  /// being asked has taken a decision about their screen that is theirs to take.
  /// </summary>
  [Test]
  public void ThereIsNoTrayUntilSomebodyAsks() {
    Assert.That(new UserSettings().TrayIndicators, Is.Empty);
    Assert.That(UserSettings.Parse("tray=none\n").TrayIndicators, Is.Empty);
    Assert.That(UserSettings.Parse(string.Empty).Write(), Does.Not.Contain("tray="));
  }

  /// <summary>
  /// Named one at a time, so turning one off does not mean turning the tray off — which is §65's
  /// fourth box, and is what a boolean could not have expressed.
  /// </summary>
  [Test]
  public void TheyAreNamedOneAtATime() {
    var settings = UserSettings.Parse("tray=memory,cpu\n");

    Assert.That(settings.TrayIndicators, Is.EqualTo(new[] { IndicatorKind.Memory, IndicatorKind.Cpu }));
  }

  /// <summary>
  /// And in the order somebody wrote them, because that is the order they appear in the panel and a
  /// panel is a place where the order is the whole of the arrangement.
  /// </summary>
  [Test]
  public void TheOrderIsTheOrderTheyWereNamedIn() {
    var one = UserSettings.Parse("tray=cpu,memory\n").TrayIndicators;
    var other = UserSettings.Parse("tray=memory,cpu\n").TrayIndicators;

    Assert.That(one, Is.Not.EqualTo(other));
  }

  /// <summary>A name this build does not know is skipped, not fatal — every setting's rule.</summary>
  [Test]
  public void AnUnknownNameIsSkippedAndTheRestSurvive()
    => Assert.That(UserSettings.Parse("tray=cpu,teapot,memory\n").TrayIndicators, Has.Count.EqualTo(2));

  /// <summary>Two of the same is a mistake rather than a preference, so the second is dropped.</summary>
  [Test]
  public void TheSameIndicatorTwiceIsOneIcon()
    => Assert.That(UserSettings.Parse("tray=cpu,cpu\n").TrayIndicators, Has.Count.EqualTo(1));

  [Test]
  public void ItSurvivesBeingWrittenOutAndReadBack() {
    var settings = UserSettings.Parse("tray=gpu,disk,network\n");

    Assert.That(UserSettings.Parse(settings.Write()).TrayIndicators, Is.EqualTo(settings.TrayIndicators));
  }

  #endregion

}
