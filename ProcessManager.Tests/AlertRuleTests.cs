using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The rules somebody writes themselves (PRD §84).
/// </summary>
/// <remarks>
/// Three properties carry the weight here. A rule with a dwell fires when the condition has held for
/// that long and not before, and once rather than every sample it goes on holding. A condition over a
/// reading nobody could take does not match, so an unpermitted counter leaves a rule silent instead of
/// firing it. And nothing a rule can say ends a process: §84 forbids it in a baseline release, and
/// what makes that true is that the vocabulary of what firing does has two words in it and both of
/// them produce a sentence.
/// </remarks>
[TestFixture]
public sealed class AlertRuleTests {

  private const ulong _OneSecond = 1_000_000_000;

  private sealed record Row(
    int Pid,
    string Name,
    ulong CpuNs = 0,
    ulong Resident = 0,
    ProcessState State = ProcessState.Running,
    bool UnreadableCpu = false
  );

  /// <summary>One sample, a second on from whatever came before it.</summary>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Sample(SystemSnapshot? before, params Row[] rows) {
    var after = new SystemSnapshot {
      TimestampTicks = (before?.TimestampTicks ?? 0) + (before is null ? 0 : System.Diagnostics.Stopwatch.Frequency),
    };

    var buffer = after.PrepareProcesses(rows.Length);
    for (var i = 0; i < rows.Length; ++i) {
      buffer[i] = default;
      buffer[i].Key = new(rows[i].Pid, 1000);
      buffer[i].Name = rows[i].Name;
      buffer[i].State = rows[i].State;
      buffer[i].CpuTimeNs = rows[i].UnreadableCpu ? Counter.NotPermitted : Counter.Of(rows[i].CpuNs);
      buffer[i].WorkingSetBytes = Counter.Of(rows[i].Resident);
    }

    after.System.CoreCount = 1;
    after.System.TotalMemoryBytes = Counter.Of(1000);

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);
    return (after, delta);
  }

  private static AlertRule Rule(string text) {
    Assert.That(AlertRule.TryParse(text, out var rule, out var error), Is.True, error);
    return rule!;
  }

  /// <summary>Runs a watch over a series of samples and returns everything it said.</summary>
  private static List<string> Run(AlertWatch watch, params Row[][] samples) {
    var said = new List<string>();
    SystemSnapshot? previous = null;
    foreach (var rows in samples) {
      var (snapshot, delta) = Sample(previous, rows);
      foreach (var notification in watch.Examine(snapshot, delta))
        said.Add(notification.Text);

      previous = snapshot;
    }

    return said;
  }

  #region the rule form of §84

  /// <summary>
  /// The sentence §84 is written in, parsed exactly as it appears in the document. It is quoted there
  /// as the shape of a rule, so if it does not parse then the requirement is not met however many
  /// near-equivalents do.
  /// </summary>
  [Test]
  public void TheRuleFormInTheDocumentParses() {
    var rule = Rule("process.name == \"myservice\" AND process.cpu.usage > 80% for 30s");

    Assert.Multiple(() => {
      Assert.That(rule.Trigger, Is.EqualTo(AlertTrigger.While));
      Assert.That(rule.Dwell, Is.EqualTo(TimeSpan.FromSeconds(30)));
      Assert.That(rule.ConditionText, Is.EqualTo("process.name == \"myservice\" AND process.cpu.usage > 80%"));
      Assert.That(rule.Actions, Is.EqualTo(AlertAction.Both), "a rule that names no action does both");
    });
  }

  /// <summary>
  /// And it means what it says. A process called something else, or one under the threshold, is not
  /// what the rule is about — a test that only checked the parse would pass on a rule that matched
  /// everything.
  /// </summary>
  [Test]
  public void TheRuleFormInTheDocumentMatchesTheProcessItNames() {
    var watch = new AlertWatch([Rule("process.name == \"myservice\" AND process.cpu.usage > 80%")]);

    var said = Run(
      watch,
      [new(1, "myservice"), new(2, "other")],
      // myservice spends nine tenths of the second; other spends a tenth.
      [new(1, "myservice", _OneSecond * 9 / 10), new(2, "other", _OneSecond / 10)]
    );

    Assert.That(said, Has.Count.EqualTo(1));
    Assert.That(said[0], Does.Contain("myservice (PID 1)"));
  }

  [TestCase("process.name", ProcessField.Name)]
  [TestCase("process.cpu.usage", ProcessField.CpuPercent)]
  [TestCase("cpu.usage", ProcessField.CpuPercent)]
  [TestCase("name", ProcessField.Name)]
  [TestCase("process.memory", ProcessField.PrivateBytes)]
  public void TheDocumentsSpellingOfAFieldIsTheRegistrysSpelling(string text, ProcessField expected) {
    Assert.That(FieldRegistry.TryParse(text, out var field), Is.True);
    Assert.That(field, Is.EqualTo(expected));
  }

  /// <summary>The prefix is stripped, not ignored: "process.nonsense" is still nonsense.</summary>
  [Test]
  public void APrefixedNameThatIsNotAFieldIsStillNotAField() {
    Assert.That(FieldRegistry.TryParse("process.nonsense", out _), Is.False);
    Assert.That(AlertRule.TryParse("process.nonsense > 1", out _, out var error), Is.False);
    Assert.That(error, Does.Contain("process.nonsense"));
  }

  #endregion

  #region for 30s

  /// <summary>
  /// The dwell is what a rule adds to a filter. A filter answers "is it true now"; a rule has to be
  /// able to say "and has been for half a minute", which is the difference between a process that is
  /// busy and a process that is stuck.
  /// </summary>
  [Test]
  public void ADwellFiresOnlyOnceTheConditionHasHeldThatLong() {
    var watch = new AlertWatch([Rule("name == \"hog\" AND cpu > 50% for 3s")]);
    var busy = new Row(1, "hog", 0);

    // Four samples a second apart, each spending the whole second on the processor. The first has no
    // previous sample to be a rate against; the three after it are each a second of holding.
    var said = Run(
      watch,
      [busy],
      [busy with { CpuNs = _OneSecond }],
      [busy with { CpuNs = _OneSecond * 2 }],
      [busy with { CpuNs = _OneSecond * 3 }],
      [busy with { CpuNs = _OneSecond * 4 }]
    );

    Assert.That(said, Has.Count.EqualTo(1), string.Join(" / ", said));
    Assert.That(said[0], Does.Contain("for 3s"));
  }

  /// <summary>Sitting above the line is one thing that happened, not one per sample (PRD §64).</summary>
  [Test]
  public void ARuleThatKeepsHoldingDoesNotKeepFiring() {
    var watch = new AlertWatch([Rule("name == \"hog\" AND cpu > 50%")]);
    var said = Run(
      watch,
      [new(1, "hog")],
      [new(1, "hog", _OneSecond)],
      [new(1, "hog", _OneSecond * 2)],
      [new(1, "hog", _OneSecond * 3)]
    );

    Assert.That(said, Has.Count.EqualTo(1), string.Join(" / ", said));
  }

  /// <summary>Dropping back arms it again, and the clock starts over rather than resuming.</summary>
  [Test]
  public void DroppingBelowResetsTheClock() {
    var watch = new AlertWatch([Rule("name == \"hog\" AND cpu > 50% for 2s")]);
    var said = Run(
      watch,
      [new(1, "hog")],
      // One second above.
      [new(1, "hog", _OneSecond)],
      // One second idle: the clock goes back to nought rather than needing one more second.
      [new(1, "hog", _OneSecond)],
      [new(1, "hog", _OneSecond * 2)],
      [new(1, "hog", _OneSecond * 3)],
      [new(1, "hog", _OneSecond * 4)]
    );

    Assert.That(said, Has.Count.EqualTo(1), string.Join(" / ", said));
  }

  #endregion

  #region appears, disappears, changes

  [Test]
  public void AppearsFiresForANewProcessAndNotForOneThatWasAlreadyThere() {
    var watch = new AlertWatch([Rule("name == \"worker\" appears")]);
    var said = Run(
      watch,
      [new(1, "worker")],
      [new(1, "worker"), new(2, "worker")],
      [new(1, "worker"), new(2, "worker")]
    );

    Assert.That(said, Has.Count.EqualTo(1), string.Join(" / ", said));
    Assert.That(said[0], Does.Contain("worker (PID 2)"));
    Assert.That(said[0], Does.Contain("appeared"));
  }

  /// <summary>
  /// A process that has ended carries no record: the only thing left of it is its identity in the
  /// delta, so what the rule matched against has to have been remembered from the sample before.
  /// </summary>
  [Test]
  public void DisappearsFiresForAProcessThatMatchedWhenItWasLastSeen() {
    var watch = new AlertWatch([Rule("name == \"sshd\" disappears")]);
    var said = Run(
      watch,
      [new(1, "sshd"), new(2, "other")],
      [new(1, "sshd"), new(2, "other")],
      [new(2, "other")]
    );

    Assert.That(said, Has.Count.EqualTo(1), string.Join(" / ", said));
    Assert.That(said[0], Does.Contain("sshd (PID 1)"));
    Assert.That(said[0], Does.Contain("gone"));
  }

  /// <summary>A process the rule was never about going is not that rule firing.</summary>
  [Test]
  public void DisappearsSaysNothingAboutAProcessThatNeverMatched() {
    var watch = new AlertWatch([Rule("name == \"sshd\" disappears")]);
    var said = Run(watch, [new(1, "sshd"), new(2, "other")], [new(1, "sshd")]);

    Assert.That(said, Is.Empty);
  }

  [Test]
  public void ChangesFiresOnTheNewValueAndNotOnFirstSight() {
    var watch = new AlertWatch([Rule("name == \"daemon\" changes state")]);
    var said = Run(
      watch,
      [new(1, "daemon", State: ProcessState.Running)],
      // Seen a second time in the same state: nothing changed.
      [new(1, "daemon", State: ProcessState.Running)],
      [new(1, "daemon", State: ProcessState.Sleeping)],
      [new(1, "daemon", State: ProcessState.Sleeping)]
    );

    Assert.That(said, Has.Count.EqualTo(1), string.Join(" / ", said));
    Assert.That(said[0], Does.Contain("daemon (PID 1)"));
    Assert.That(said[0], Does.Contain("changed from"));
  }

  /// <summary>"changes" on its own is the state, which is the one §84 names.</summary>
  [Test]
  public void ABareChangesWatchesTheState() {
    var rule = Rule("name == \"daemon\" changes");

    Assert.That(rule.Trigger, Is.EqualTo(AlertTrigger.Changes));
    Assert.That(rule.Watched, Is.EqualTo(ProcessField.State));
  }

  #endregion

  #region every comparison §84 lists

  [TestCase("name == \"target\"", true)]
  [TestCase("name == \"elsewhere\"", false)]
  [TestCase("name != \"elsewhere\"", true)]
  [TestCase("name:targ", true)]
  [TestCase("name:nothing", false)]
  [TestCase("name:/^tar/", true)]
  [TestCase("name:/^nope/", false)]
  [TestCase("rss > 400", true)]
  [TestCase("rss > 600", false)]
  [TestCase("rss < 600", true)]
  [TestCase("rss >= 500", true)]
  [TestCase("rss <= 400", false)]
  public void EveryComparisonInTheListIsAvailableToARule(string condition, bool expected) {
    var watch = new AlertWatch([Rule(condition)]);
    var said = Run(watch, [new(1, "target", Resident: 500)], [new(1, "target", Resident: 500)]);

    Assert.That(said.Count, Is.EqualTo(expected ? 1 : 0), string.Join(" / ", said));
  }

  #endregion

  #region what does not fire

  /// <summary>
  /// The confident zero, arriving as an interruption instead of as a cell. A CPU time the sampler
  /// could not read is not a CPU time of nought, so a rule about being busy neither fires nor clears.
  /// </summary>
  [Test]
  public void AReadingWithNoValueDoesNotFireARule() {
    var watch = new AlertWatch([Rule("name == \"secret\" AND cpu > 0")]);
    var said = Run(
      watch,
      [new(1, "secret", UnreadableCpu: true)],
      [new(1, "secret", UnreadableCpu: true)],
      [new(1, "secret", UnreadableCpu: true)]
    );

    Assert.That(said, Is.Empty);
  }

  /// <summary>
  /// The first sample of a run says nothing whatever, for §64's reason: every process on the machine
  /// is new to a program that has just started looking.
  /// </summary>
  [Test]
  public void TheFirstSampleOfARunFiresNothing() {
    var watch = new AlertWatch([Rule("name:a appears"), Rule("rss > 0")]);
    var (snapshot, delta) = Sample(null, new Row(1, "a", Resident: 5));

    Assert.That(watch.Examine(snapshot, delta), Is.Empty);
  }

  [Test]
  public void AWatchWithNoRulesInItDoesNothingAtAll() {
    var watch = new AlertWatch([]);
    var said = Run(watch, [new(1, "a")], [new(1, "a"), new(2, "b")]);

    Assert.That(said, Is.Empty);
    Assert.That(watch.Actions, Is.EqualTo(AlertAction.None));
  }

  #endregion

  #region what a rule may do

  [TestCase("name:a then notify", AlertAction.Notify)]
  [TestCase("name:a then log", AlertAction.Log)]
  [TestCase("name:a then notify and log", AlertAction.Both)]
  [TestCase("name:a then log, notify", AlertAction.Both)]
  [TestCase("name:a", AlertAction.Both)]
  public void ARuleSaysWhatItWants(string text, AlertAction expected)
    => Assert.That(Rule(text).Actions, Is.EqualTo(expected));

  /// <summary>
  /// §84: automatic process termination is not enabled in a baseline release. The way that is kept is
  /// that there is no word for it — a rule that asks is refused with the reason, rather than quietly
  /// given a notice instead, because a rule somebody wrote and was not told about is a rule they
  /// believe is doing something.
  /// </summary>
  [TestCase("name:a then kill")]
  [TestCase("name:a then terminate")]
  [TestCase("name:a then end")]
  [TestCase("name:a then restart")]
  [TestCase("name:a then suspend")]
  public void ARuleCannotAskForAProcessToBeEnded(string text) {
    Assert.That(AlertRule.TryParse(text, out var rule, out var error), Is.False);
    Assert.That(rule, Is.Null);
    Assert.That(error, Does.Contain("notify"));
  }

  /// <summary>
  /// And there is no member to add one with. This is the audit rather than the argument: whatever a
  /// rule fires, the whole vocabulary of what firing does is here, and every member of it is a
  /// sentence.
  /// </summary>
  [Test]
  public void NothingInTheActionVocabularyTouchesAProcess() {
    var names = new List<string>(Enum.GetNames<AlertAction>());
    names.Sort(StringComparer.Ordinal);

    Assert.That(names, Is.EqualTo(new[] { "Both", "Log", "None", "Notify" }));
  }

  /// <summary>A rule nobody finished parsing is not one that fires (PRD §72.3, applied to a trigger).</summary>
  [Test]
  public void TheDefaultTriggerIsNotARealOne()
    => Assert.That(default(AlertTrigger), Is.EqualTo(AlertTrigger.Unspecified));

  #endregion

  #region what will not parse

  [TestCase("", "nothing")]
  [TestCase("   ", "nothing")]
  [TestCase("for 30s", "condition")]
  [TestCase("name:a for", "no value after it")]
  [TestCase("name:a then", "'then' with nothing after it")]
  public void ARuleThatMakesNoSenseIsRefusedWithAReason(string text, string expected) {
    Assert.That(AlertRule.TryParse(text, out var rule, out var error), Is.False, text);
    Assert.That(rule, Is.Null);
    Assert.That(error, Does.Contain(expected).IgnoreCase);
  }

  /// <summary>
  /// A rule about a process called "for" is a rule about a process called "for". The tail is scanned
  /// outside quotes, or a quoted value would silently become a trigger.
  /// </summary>
  [Test]
  public void AQuotedWordIsNotATrigger() {
    var rule = Rule("name == \"appears\"");

    Assert.That(rule.Trigger, Is.EqualTo(AlertTrigger.While));
    Assert.That(rule.ConditionText, Is.EqualTo("name == \"appears\""));
  }

  #endregion

  #region the settings file

  [Test]
  public void ARuleSurvivesTheFile() {
    const string Text = "alert=process.name == \"myservice\" AND process.cpu.usage > 80% for 30s then log";
    var settings = UserSettings.Parse(Text);

    Assert.That(settings.Notifications.Alerts, Has.Count.EqualTo(1));
    Assert.That(settings.Notifications.Alerts[0].Actions, Is.EqualTo(AlertAction.Log));
    Assert.That(settings.AlertProblems, Is.Empty);

    var round = UserSettings.Parse(settings.Write());
    Assert.That(round.Notifications.Alerts, Has.Count.EqualTo(1));
    Assert.That(round.Notifications.Alerts[0].Text, Is.EqualTo(settings.Notifications.Alerts[0].Text));
  }

  [Test]
  public void SeveralRulesAreSeveralLines() {
    var settings = UserSettings.Parse("alert=name:a appears\nalert=name:b disappears\n");

    Assert.That(settings.Notifications.Alerts, Has.Count.EqualTo(2));
    Assert.That(settings.Notifications.Alerts[0].Trigger, Is.EqualTo(AlertTrigger.Appears));
    Assert.That(settings.Notifications.Alerts[1].Trigger, Is.EqualTo(AlertTrigger.Disappears));
  }

  /// <summary>
  /// A rule that will not parse is reported and kept. Reported because somebody believing they are
  /// watched and not being is worse than any other bad line in this file; kept because saving the
  /// settings must not eat the rule they were halfway through writing.
  /// </summary>
  [Test]
  public void ARuleThatWillNotParseIsReportedAndNotEaten() {
    var settings = UserSettings.Parse("alert=nonsense.field > 3\n");

    Assert.That(settings.Notifications.Alerts, Is.Empty);
    Assert.That(settings.AlertProblems, Has.Count.EqualTo(1));
    Assert.That(settings.AlertProblems[0], Does.Contain("nonsense.field"));
    Assert.That(settings.Write(), Does.Contain("alert=nonsense.field > 3"));
  }

  [Test]
  public void AFileWithNoRulesInItGrowsNoAlertLines()
    => Assert.That(new UserSettings().Write(), Does.Not.Contain("alert="));

  /// <summary>Written rules count as something having been asked for, or nothing would examine them.</summary>
  [Test]
  public void AWrittenRuleMakesTheWatchWorthRunning() {
    var settings = UserSettings.Parse("alert=name:a appears\n");

    Assert.That(settings.Notifications.Any, Is.True);
  }

  #endregion

  #region the record of it

  /// <summary>
  /// A rule firing is its own category in the timeline. Folding it into "over a threshold" would file
  /// "sshd has gone" under something that did not happen (PRD §63).
  /// </summary>
  [Test]
  public void AFiredRuleIsRecordedAsARule() {
    var log = new EventLog();
    log.Add([new(NotificationKind.RuleFired, "sshd (PID 1) has gone, having matched name == \"sshd\"")], 1000);

    Assert.That(log.Entries, Has.Count.EqualTo(1));
    Assert.That(log.Entries[0].Category, Is.EqualTo(EventCategory.Rule));
    Assert.That(EventLog.Describe(EventCategory.Rule), Is.EqualTo("a rule you wrote"));
  }

  /// <summary>
  /// The written rules go through the same watch the six ready-made ones do, so a front-end that
  /// shows one shows the other and there is one path to keep honest rather than two.
  /// </summary>
  [Test]
  public void AWrittenRuleComesOutOfTheOrdinaryWatch() {
    var watch = new NotificationWatch(new NotificationRules { Alerts = [Rule("name == \"worker\" appears")] });

    var first = Sample(null, new Row(1, "init")).Snapshot;
    var (snapshot, delta) = Sample(first, new Row(1, "init"), new Row(2, "worker"));
    var found = watch.Examine(snapshot, delta);

    Assert.That(found, Has.Count.EqualTo(1));
    Assert.That(found[0].Kind, Is.EqualTo(NotificationKind.RuleFired));
    Assert.That(found[0].Text, Does.Contain("worker (PID 2)"));
  }

  #endregion

}
