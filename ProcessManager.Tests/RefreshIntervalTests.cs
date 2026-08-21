using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Terminal;
using static Hawkynt.ProcessManager.Tests.TerminalFixture;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// How often the machine is sampled, and whether it is sampled at all (PRD §12).
/// </summary>
/// <remarks>
/// Both front-ends and the file between them, in one fixture, because the requirement is that they
/// agree: the row §12 asks for is one picker offering one set of rates, and two lists that started
/// out identical is exactly the drift the field catalogue was split up to stop (PRD §5.1).
/// </remarks>
[TestFixture]
public sealed class RefreshIntervalTests {

  [Test]
  public void BothFrontEndsOfferTheSameRates() {
    Assert.That(UserSettings.OfferedIntervalSeconds, Is.EqualTo(new[] { 0.25, 0.5, 1d, 2d, 5d, 10d }));
    Assert.That(UserSettings.NameOfInterval(0.25), Is.EqualTo("250 ms"));
    Assert.That(UserSettings.NameOfInterval(1), Is.EqualTo("1 s"));
    Assert.That(UserSettings.NameOfInterval(10), Is.EqualTo("10 s"));
  }

  /// <summary>
  /// The rate underneath survives the tick being switched off, so switching it back on returns to
  /// what somebody chose rather than to whatever the default happens to be.
  /// </summary>
  [Test]
  public void RefreshingByHandSurvivesTheSettingsFile() {
    var written = new UserSettings { ManualRefresh = true, IntervalSeconds = 5 }.Write();

    var parsed = UserSettings.Parse(written);
    Assert.That(parsed.ManualRefresh, Is.True);
    Assert.That(parsed.IntervalSeconds, Is.EqualTo(5));
  }

  [Test]
  public void ARateInTheFileLeavesTheTickOn() {
    var parsed = UserSettings.Parse("interval=2.5");

    Assert.That(parsed.ManualRefresh, Is.False);
    Assert.That(parsed.IntervalSeconds, Is.EqualTo(2.5));
  }

  #region the terminal (PRD §57.3)

  [Test]
  public void ThePickerOpensOnItsOwnKeyAndNamesTheRateItIsOn() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('d'));
      var frame = Frame(ui);

      Assert.That(frame, Does.Contain("Sample the machine"));
      Assert.That(frame, Does.Contain("Every 250 ms"));
      Assert.That(frame, Does.Contain("[x] Every 1 s"), "the rate it is on is the ticked one");
      Assert.That(frame, Does.Contain("By hand only"));
    }
  }

  /// <summary>
  /// Choosing a rate moves it. The host reads the property each time round its loop, so a rate
  /// chosen mid-session takes effect at the next sample rather than at the next start-up.
  /// </summary>
  [Test]
  public void ChoosingARateMovesIt() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('d'));
      ui.HandleKey(Key(ConsoleKey.DownArrow));
      ui.HandleKey(Key(ConsoleKey.DownArrow));
      ui.HandleKey(Key(ConsoleKey.DownArrow));
      ui.HandleKey(Key(ConsoleKey.Enter));

      Assert.That(ui.IntervalMilliseconds, Is.EqualTo(2000));
      Assert.That(ui.Sampling, Is.True);
      Assert.That(Frame(ui), Does.Contain("sampling every 2 s"));
    }
  }

  /// <summary>
  /// A pause and a by-hand refresh both stop the tick and are not the same request, so the tab row
  /// has to be able to say which of the two it is.
  /// </summary>
  [Test]
  public void TheTabRowSaysWhyTheTableIsNotMoving() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('p'));
      Assert.That(ui.Sampling, Is.False);
      Assert.That(Frame(ui), Does.Contain("PAUSED"));

      ui.HandleKey(Key('p'));
      ui.SetManualRefresh();
      Assert.That(ui.Sampling, Is.False);
      Assert.That(ui.Paused, Is.False);
      Assert.That(Frame(ui), Does.Contain("BY HAND"));
    }
  }

  /// <summary>The rate is remembered while the tick is off, so turning it back on returns to it.</summary>
  [Test]
  public void StoppingTheTickLeavesTheRateWhereItWas() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.IntervalMilliseconds = 5000;
      ui.SetManualRefresh();

      Assert.That(ui.IntervalMilliseconds, Is.EqualTo(5000));
    }
  }

  [Test]
  public void ARateOutsideWhatATerminalCanUseIsClamped() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.IntervalMilliseconds = 1;
      Assert.That(ui.IntervalMilliseconds, Is.EqualTo(250));

      ui.IntervalMilliseconds = 9_999_999;
      Assert.That(ui.IntervalMilliseconds, Is.EqualTo(60_000));
    }
  }

  #endregion

}
