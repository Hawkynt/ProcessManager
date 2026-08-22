using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What each program has cost the machine, across every time it has been run (PRD §44).
/// </summary>
/// <remarks>
/// The accumulator is pure: it is handed a snapshot, an interval and a clock, so every one of these
/// is arithmetic rather than a measurement of the machine running them. The wall clock is passed in
/// for the same reason — a test that reads the real one is a test that behaves differently at
/// midnight.
/// </remarks>
[TestFixture]
public sealed class UsageHistoryTests {

  private static SystemSnapshot Machine(params (int Pid, string Path, ulong Cpu, ulong Read, ulong Working)[] processes) {
    var snapshot = new SystemSnapshot { TimestampTicks = 0 };
    var span = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      span[i] = default;
      span[i].Key = new(processes[i].Pid, 1);
      span[i].Name = Path.GetFileName(processes[i].Path);
      span[i].ImagePath = processes[i].Path;
      span[i].CpuTimeNs = Counter.Of(processes[i].Cpu);
      span[i].ReadBytes = Counter.Of(processes[i].Read);
      span[i].WriteBytes = Counter.Of(0);
      span[i].WorkingSetBytes = Counter.Of(processes[i].Working);
    }

    return snapshot;
  }

  /// <summary>
  /// The first sight of a process contributes nothing but a launch. What it did before anybody was
  /// watching is not this machine's record of it, and adding its accumulated counters would credit
  /// an hour of work to the second somebody opened the window.
  /// </summary>
  [Test]
  public void TheFirstSightOfAProcessAddsALaunchAndNothingElse() {
    var history = new UsageHistory();

    history.Add(Machine((1, "/usr/bin/thing", 3_600_000_000_000, 999_999, 100)), 1, 1000);

    var record = history.Find("/usr/bin/thing");
    Assert.That(record, Is.Not.Null);
    Assert.That(record!.Value.Launches, Is.EqualTo(1));
    Assert.That(record.Value.CpuTimeNs, Is.Zero, "an hour it spent before we looked is not ours to record");
    Assert.That(record.Value.ReadBytes, Is.Zero);
  }

  /// <summary>
  /// The second and later sights add the difference, not the total. A cumulative counter added every
  /// sample would count the same second once per sample.
  /// </summary>
  [Test]
  public void LaterSamplesAddTheDifferenceAndNotTheTotal() {
    var history = new UsageHistory();
    history.Add(Machine((1, "/usr/bin/thing", 1000, 100, 50)), 1, 1000);
    history.Add(Machine((1, "/usr/bin/thing", 1500, 300, 50)), 1, 2000);
    history.Add(Machine((1, "/usr/bin/thing", 1800, 300, 50)), 1, 3000);

    var record = history.Find("/usr/bin/thing")!.Value;
    Assert.That(record.CpuTimeNs, Is.EqualTo(800ul), "500 then 300");
    Assert.That(record.ReadBytes, Is.EqualTo(200ul));
    Assert.That(record.Launches, Is.EqualTo(1), "one process seen three times is one launch");
  }

  /// <summary>
  /// A counter that went backwards adds nothing. That is a kernel that wrapped or a reading that
  /// failed, and treating it as a huge positive would put a century of processor time on a row.
  /// </summary>
  [Test]
  public void ACounterThatWentBackwardsAddsNothing() {
    var history = new UsageHistory();
    history.Add(Machine((1, "/usr/bin/thing", 5000, 0, 0)), 1, 1000);
    history.Add(Machine((1, "/usr/bin/thing", 10, 0, 0)), 1, 2000);

    Assert.That(history.Find("/usr/bin/thing")!.Value.CpuTimeNs, Is.Zero);
  }

  /// <summary>
  /// Two runs of the same program are two launches against one total, which is the whole point of
  /// keying by the image rather than by the process.
  /// </summary>
  [Test]
  public void TwoRunsOfOneProgramShareItsTotal() {
    var history = new UsageHistory();
    history.Add(Machine((1, "/usr/bin/thing", 0, 0, 0)), 1, 1000);
    history.Add(Machine((1, "/usr/bin/thing", 100, 0, 0), (2, "/usr/bin/thing", 0, 0, 0)), 1, 2000);
    history.Add(Machine((1, "/usr/bin/thing", 100, 0, 0), (2, "/usr/bin/thing", 700, 0, 0)), 1, 3000);

    var record = history.Find("/usr/bin/thing")!.Value;
    Assert.That(record.Launches, Is.EqualTo(2));
    Assert.That(record.CpuTimeNs, Is.EqualTo(800ul));
  }

  /// <summary>
  /// The average is the integral over the time, not a mean of means. A run lasting a second and one
  /// lasting a day would otherwise count equally, and the day is what the machine actually spent.
  /// </summary>
  [Test]
  public void TheAverageIsWeightedByHowLongItRan() {
    var history = new UsageHistory();
    history.Add(Machine((1, "/usr/bin/thing", 0, 0, 100)), 1, 1000);
    // Ten seconds at a thousand, then one second at a hundred: the mean is much nearer a thousand.
    history.Add(Machine((1, "/usr/bin/thing", 0, 0, 1000)), 10, 2000);
    history.Add(Machine((1, "/usr/bin/thing", 0, 0, 100)), 1, 3000);

    var record = history.Find("/usr/bin/thing")!.Value;
    Assert.That(record.AverageWorkingSetBytes, Is.EqualTo((10_000d + 100d) / 11).Within(0.001));
    Assert.That(record.PeakWorkingSetBytes, Is.EqualTo(1000ul));
  }

  /// <summary>
  /// An interval of nothing attributes nothing. The first sample of a session has no interval behind
  /// it, and crediting a whole process's lifetime to it would be the largest possible error.
  /// </summary>
  [Test]
  public void AnIntervalOfNothingAttributesNothing() {
    var history = new UsageHistory();
    history.Add(Machine((1, "/usr/bin/thing", 5000, 0, 0)), 0, 1000);

    Assert.That(history.Count, Is.Zero);
  }

  /// <summary>
  /// A process with no image path is not counted at all. A kernel thread has none, and a row of
  /// totals under an empty name would be every kernel thread's work added together.
  /// </summary>
  [Test]
  public void AProcessWithNoImageIsNotAnApplication() {
    var history = new UsageHistory();
    history.Add(Machine((1, string.Empty, 100, 0, 0)), 1, 1000);

    Assert.That(history.Count, Is.Zero);
  }

  #region the file

  [Test]
  public void ItSurvivesBeingWrittenOutAndReadBack() {
    var history = new UsageHistory();
    history.Add(Machine((1, "/usr/bin/a program with spaces", 0, 0, 0)), 1, 1000);
    history.Add(Machine((1, "/usr/bin/a program with spaces", 12_345, 678, 900)), 2, 2000);

    var back = new UsageHistory();
    back.Restore(UsageHistory.Parse(history.Write()));

    var before = history.Find("/usr/bin/a program with spaces")!.Value;
    var after = back.Find("/usr/bin/a program with spaces")!.Value;
    Assert.That(after, Is.EqualTo(before));
  }

  /// <summary>
  /// A line that cannot be understood is skipped rather than failing the file — the same rule the
  /// settings file follows. A history that refuses to load because one line was corrupted has lost
  /// everything to save nothing.
  /// </summary>
  [Test]
  public void ACorruptedLineDoesNotCostTheRestOfTheFile() {
    var text = """
      # a comment
      not a record at all
      1	2	3	4	5	6	7	8	9	/usr/bin/kept
      12	nonsense	3	4	5	6	7	8	9	/usr/bin/dropped
      """;

    var records = UsageHistory.Parse(text);
    Assert.That(records, Has.Count.EqualTo(1));
    Assert.That(records[0].Application, Is.EqualTo("/usr/bin/kept"));
  }

  /// <summary>Reset means starting from nothing, which is what somebody asking for it means.</summary>
  [Test]
  public void ResetForgetsEverything() {
    var history = new UsageHistory();
    history.Add(Machine((1, "/usr/bin/thing", 0, 0, 0)), 1, 1000);
    history.Clear();

    Assert.That(history.Count, Is.Zero);
  }

  /// <summary>
  /// Retention drops what has not been seen lately, by last sighting rather than by first. A program
  /// run every day since January is not old, and dropping it because its record began long ago would
  /// delete exactly the rows worth keeping.
  /// </summary>
  [Test]
  public void RetentionGoesByTheLastSightingAndNotTheFirst() {
    var history = new UsageHistory();
    history.Restore([
      new("/usr/bin/old-and-idle", 1, 0, 0, 0, 0, 0, 1, 100, 100),
      new("/usr/bin/old-and-busy", 1, 0, 0, 0, 0, 0, 1, 100, 9000),
    ]);

    Assert.That(history.Forget(5000), Is.EqualTo(1));
    Assert.That(history.Find("/usr/bin/old-and-busy"), Is.Not.Null, "started long ago, still running");
    Assert.That(history.Find("/usr/bin/old-and-idle"), Is.Null);
  }

  #endregion

  /// <summary>
  /// A process that ended stops being remembered, or the map grows for the life of a session on a
  /// machine that starts and stops a lot of short-lived programs — which is most of them.
  /// </summary>
  [Test]
  public void AProcessThatEndedIsNotRememberedForEver() {
    var history = new UsageHistory();
    for (var i = 0; i < 200; ++i)
      history.Add(Machine((i + 1, "/usr/bin/short", 0, 0, 0)), 1, 1000 + i);

    // Two hundred short-lived processes, one at a time. The launch count knows about all of them;
    // nothing else should still be holding them.
    Assert.That(history.Find("/usr/bin/short")!.Value.Launches, Is.EqualTo(200));
  }


  #region where the file goes

  /// <summary>
  /// The record lives beside whichever settings file is in use, not beside the default one.
  /// </summary>
  /// <remarks>
  /// `--settings`, `PROCMAN_SETTINGS` and a portable marker each move the settings file, and this has
  /// to follow: a portable install on a stick that wrote its record into the profile directory would
  /// leave behind exactly the file it exists to keep off the machine. This was wrong when it was
  /// first written and only a run against a real path showed it.
  /// </remarks>
  [Test]
  public void TheRecordLivesBesideTheSettingsFileInUse() {
    var elsewhere = Path.Combine("somewhere", "portable", "settings.conf");

    var usage = SettingsStore.UsagePathFor(elsewhere);

    Assert.That(Path.GetDirectoryName(usage), Is.EqualTo(Path.GetDirectoryName(elsewhere)));
    Assert.That(Path.GetFileName(usage), Is.EqualTo(SettingsStore.UsageFileName));
  }

  /// <summary>
  /// It is a file of its own rather than a section of the settings: it is data rather than
  /// preference, it grows, and a settings file somebody edits by hand should not fill with rows they
  /// never wrote.
  /// </summary>
  [Test]
  public void ItIsNotTheSettingsFile()
    => Assert.That(SettingsStore.UsageFileName, Is.Not.EqualTo(SettingsStore.FileName));

  /// <summary>
  /// The setting is off unless a file says otherwise, which is the whole design of the feature
  /// rather than a preference about it.
  /// </summary>
  [Test]
  public void NobodyIsRecordedUntilSomebodyAsks() {
    Assert.That(new UserSettings().UsageHistory, Is.False);
    Assert.That(UserSettings.Parse("history.usage=true\n").UsageHistory, Is.True);
    Assert.That(UserSettings.Parse("history.usage=true\n").Write(), Does.Contain("history.usage=true"));
  }

  /// <summary>
  /// And a settings file that never mentions it writes nothing about it, so the absence stays an
  /// absence rather than becoming an explicit "off" the next time the file is saved.
  /// </summary>
  [Test]
  public void SayingNothingWritesNothing()
    => Assert.That(new UserSettings().Write(), Does.Not.Contain("history.usage"));

  /// <summary>
  /// A retention of days round-trips, and nought means keep everything — which is what a person who
  /// has not set one means.
  /// </summary>
  [Test]
  public void RetentionRoundTrips() {
    var settings = UserSettings.Parse("history.usage=true\nhistory.usage.days=30\n");

    Assert.That(settings.UsageHistoryDays, Is.EqualTo(30));
    Assert.That(UserSettings.Parse(settings.Write()).UsageHistoryDays, Is.EqualTo(30));
    Assert.That(new UserSettings().UsageHistoryDays, Is.Zero);
  }

  #endregion

}
