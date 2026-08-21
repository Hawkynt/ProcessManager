using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// How many decimals a percentage is written with (PRD §15, §67).
/// </summary>
/// <remarks>
/// A global on <see cref="Humanize"/> because every percentage in the program renders through it, so
/// each test puts it back — a fixture that left it at two would change what every other fixture in
/// this assembly reads.
/// </remarks>
[TestFixture]
public sealed class PercentPrecisionTests {

  [TearDown]
  public void Restore() => Humanize.PercentDecimals = Humanize.DefaultPercentDecimals;

  [TestCase(0, "12")]
  [TestCase(1, "12.3")]
  [TestCase(2, "12.35")]
  [TestCase(3, "12.346")]
  public void APercentageIsWrittenAtTheChosenPrecision(int decimals, string expected) {
    Humanize.PercentDecimals = decimals;

    Assert.That(Humanize.Percent(Rate.Of(12.3456)), Is.EqualTo(expected));
  }

  /// <summary>
  /// One digit fewer once the value reaches a hundred, so the column keeps its width where it is
  /// widest. At the default this is what the program has always done — "100" beside "99.9" — and the
  /// rule follows the setting rather than staying fixed at the tenth it used to be.
  /// </summary>
  [TestCase(1, "100")]
  [TestCase(2, "100.0")]
  public void AHundredPerCentDropsOneDigitSoTheColumnKeepsItsWidth(int decimals, string expected) {
    Humanize.PercentDecimals = decimals;

    Assert.That(Humanize.Percent(Rate.Of(100)), Is.EqualTo(expected));
  }

  /// <summary>
  /// The default is what it always was, which is the point of checking it: this setting must change
  /// nothing for anybody who does not set it.
  /// </summary>
  [Test]
  public void TheDefaultIsUnchanged() {
    Assert.Multiple(() => {
      Assert.That(Humanize.Percent(Rate.Of(12.3456)), Is.EqualTo("12.3"));
      Assert.That(Humanize.Percent(Rate.Of(100)), Is.EqualTo("100"));
      Assert.That(Humanize.SignedPercent(Rate.Of(-3.44)), Is.EqualTo("−3.4"));
      Assert.That(Humanize.SignedPercent(Rate.Of(0.04)), Is.EqualTo("0"));
    });
  }

  /// <summary>
  /// The threshold under which a change is written as a plain nought follows the precision. At one
  /// decimal a twentieth of a point rounds to nothing and says so; at two it does not, and writing
  /// "0" over a change the column has room to show would be losing the reading the column is for.
  /// </summary>
  [Test]
  public void TheDeadBandFollowsThePrecision() {
    Humanize.PercentDecimals = 2;

    Assert.That(Humanize.SignedPercent(Rate.Of(0.04)), Is.EqualTo("+0.04"));
    Assert.That(Humanize.SignedPercent(Rate.Of(0.004)), Is.EqualTo("0"));
  }

  /// <summary>Nonsense is clamped rather than formatted: "F9" of a number sampled once a second is nine digits of noise.</summary>
  [Test]
  public void OutOfRangeIsClamped() {
    Humanize.PercentDecimals = 99;
    Assert.That(Humanize.PercentDecimals, Is.EqualTo(Humanize.MaximumPercentDecimals));

    Humanize.PercentDecimals = -4;
    Assert.That(Humanize.PercentDecimals, Is.EqualTo(0));
  }

  /// <summary>A placeholder is a placeholder at every precision — there is no value to write digits of.</summary>
  [Test]
  public void AMissingReadingStillSaysWhyRatherThanNought() {
    Humanize.PercentDecimals = 3;

    Assert.That(Humanize.Percent(Rate.Unknown(UnknownReason.NotPermitted)), Is.EqualTo("—"));
    Assert.That(Humanize.SignedPercent(Rate.Unknown(UnknownReason.NotSampledYet)), Is.EqualTo("…"));
  }

  #region the settings file (PRD §67)

  [Test]
  public void TheSettingRoundTripsThroughTheFile() {
    var settings = new UserSettings { PercentDecimals = 2 };
    var written = settings.Write();

    Assert.That(written, Does.Contain("percent.decimals=2"));
    Assert.That(UserSettings.Parse(written).PercentDecimals, Is.EqualTo(2));
  }

  [Test]
  public void TheDefaultSettingIsOneDecimal()
    => Assert.That(new UserSettings().PercentDecimals, Is.EqualTo(1));

  /// <summary>
  /// A value out of range is a typo rather than a preference and leaves the setting where it was —
  /// the same rule every other number in this file follows.
  /// </summary>
  [TestCase("9")]
  [TestCase("-1")]
  [TestCase("two")]
  public void ANonsenseValueLeavesTheSettingAlone(string value)
    => Assert.That(UserSettings.Parse($"percent.decimals={value}").PercentDecimals, Is.EqualTo(1));

  #endregion

}
