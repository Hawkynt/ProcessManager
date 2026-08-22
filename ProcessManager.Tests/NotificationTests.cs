using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What somebody asked to be told about, and what they are told (PRD §64).
/// </summary>
/// <remarks>
/// Two properties carry most of the weight here and neither is obvious from reading the rules. A
/// process sitting above a threshold for a minute is one thing that happened and not sixty, so the
/// crossing fires and the sitting does not; and a reading with no value is not a reading below the
/// threshold, so an unpermitted counter neither fires the rule nor clears it. Getting the second
/// wrong is the confident-zero defect arriving as an interruption instead of as a cell.
/// </remarks>
[TestFixture]
public sealed class NotificationTests {

  private const ulong _OneSecond = 1_000_000_000;

  /// <summary>One process, sampled twice, with whatever it spent in between.</summary>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Sample(
    SystemSnapshot? before,
    (int Pid, string Name, ulong CpuNs, ulong Resident, ulong ReadBytes)[] processes
  ) {
    // One second on from whatever came before, so that a third and a fourth sample are intervals and
    // not two readings taken at the same instant.
    var after = new SystemSnapshot {
      TimestampTicks = (before?.TimestampTicks ?? 0) + (before is null ? 0 : System.Diagnostics.Stopwatch.Frequency),
    };
    var buffer = after.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      buffer[i] = default;
      buffer[i].Key = new(processes[i].Pid, 1000);
      buffer[i].Name = processes[i].Name;
      buffer[i].CpuTimeNs = Counter.Of(processes[i].CpuNs);
      buffer[i].WorkingSetBytes = Counter.Of(processes[i].Resident);
      buffer[i].ReadBytes = Counter.Of(processes[i].ReadBytes);
    }

    after.System.CoreCount = 8;
    after.System.TotalMemoryBytes = Counter.Of(1000);

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);
    return (after, delta);
  }

  private static SystemSnapshot First(params (int, string, ulong, ulong, ulong)[] processes)
    => Sample(null, processes).Snapshot;

  #region nothing was asked for

  [Test]
  public void AnUnconfiguredProgramSaysNothingAtAll() {
    var watch = new NotificationWatch(new());
    var first = First((1, "init", 0, 0, 0));
    var (snapshot, delta) = Sample(first, [(1, "init", _OneSecond * 8, 900, 0), (2, "new", 0, 0, 0)]);

    Assert.That(watch.Examine(snapshot, delta), Is.Empty);
    Assert.That(new NotificationRules().Any, Is.False);
    Assert.That(new NotificationRules().NeedsServices, Is.False);
  }

  /// <summary>
  /// Against no previous sample every process on the machine is new in the only sense available. A
  /// program that announced three hundred starts the moment it opened would have taught its reader
  /// to ignore it before they finished reading the first one.
  /// </summary>
  [Test]
  public void TheFirstSampleOfARunAnnouncesNothing() {
    var watch = new NotificationWatch(new() { ProcessStarted = true, ProcessEnded = true });
    var first = new SystemSnapshot { TimestampTicks = 0 };
    var buffer = first.PrepareProcesses(2);
    buffer[0] = default;
    buffer[0].Key = new(1, 1000);
    buffer[0].Name = "init";
    buffer[1] = default;
    buffer[1].Key = new(2, 1000);
    buffer[1].Name = "shell";
    var delta = new SnapshotDelta();
    delta.Update(null, first, CpuPercentMode.Normalized);

    Assert.That(watch.Examine(first, delta), Is.Empty);
  }

  #endregion

  #region what started and what went (PRD §64)

  [Test]
  public void AProcessThatAppearedIsAnnouncedByNameAndPid() {
    var watch = new NotificationWatch(new() { ProcessStarted = true });
    var first = First((1, "init", 0, 0, 0));
    var (snapshot, delta) = Sample(first, [(1, "init", 0, 0, 0), (77, "backup", 0, 0, 0)]);

    var found = watch.Examine(snapshot, delta);

    Assert.That(found, Has.Count.EqualTo(1));
    Assert.That(found[0].Kind, Is.EqualTo(NotificationKind.ProcessStarted));
    Assert.That(found[0].Text, Does.Contain("backup"));
    Assert.That(found[0].Text, Does.Contain("PID 77"));
  }

  /// <summary>
  /// A process that has ended is the one case where the name cannot be looked up afterwards — it is
  /// gone, and the delta carries only its identity. "PID 77 ended" is not a sentence anybody can act
  /// on (PRD §90).
  /// </summary>
  [Test]
  public void AProcessThatWentIsStillNamedRatherThanOnlyNumbered() {
    var watch = new NotificationWatch(new() { ProcessEnded = true });
    var first = First((1, "init", 0, 0, 0), (77, "backup", 0, 0, 0));
    var (started, startedDelta) = Sample(first, [(1, "init", 0, 0, 0), (77, "backup", 0, 0, 0)]);
    watch.Examine(started, startedDelta);

    var (snapshot, delta) = Sample(started, [(1, "init", 0, 0, 0)]);
    var found = watch.Examine(snapshot, delta);

    Assert.That(found, Has.Count.EqualTo(1));
    Assert.That(found[0].Kind, Is.EqualTo(NotificationKind.ProcessEnded));
    Assert.That(found[0].Text, Does.Contain("backup"));
  }

  [Test]
  public void ANamedProcessFiresItsOwnRuleWithoutTheCatchAllOne() {
    var watch = new NotificationWatch(new() { Names = ["BACKUP"] });
    var first = First((1, "init", 0, 0, 0));
    var (snapshot, delta) = Sample(first, [(1, "init", 0, 0, 0), (77, "backup", 0, 0, 0), (78, "other", 0, 0, 0)]);

    var found = watch.Examine(snapshot, delta);

    Assert.That(found, Has.Count.EqualTo(1), "the one that was named, and not the one that was not");
    Assert.That(found[0].Kind, Is.EqualTo(NotificationKind.NamedProcessStarted));
    Assert.That(found[0].Text, Does.Contain("backup"), "matched without regard to case");
  }

  #endregion

  #region thresholds (PRD §23, §64)

  [Test]
  public void CrossingTheCpuThresholdFiresOnceAndNotEverySample() {
    var watch = new NotificationWatch(new() { CpuPercent = 90 });
    var first = First((1, "grinder", 0, 0, 0));

    // A whole core of an eight-core machine: a hundred per cent per core.
    var (over, overDelta) = Sample(first, [(1, "grinder", _OneSecond, 0, 0)]);
    var found = watch.Examine(over, overDelta);

    Assert.That(found, Has.Count.EqualTo(1));
    Assert.That(found[0].Kind, Is.EqualTo(NotificationKind.CpuAboveThreshold));

    var (again, againDelta) = Sample(over, [(1, "grinder", _OneSecond * 2, 0, 0)]);
    Assert.That(watch.Examine(again, againDelta), Is.Empty, "still over is not a new event");
  }

  [Test]
  public void DroppingBackArmsTheRuleAgain() {
    var watch = new NotificationWatch(new() { CpuPercent = 90 });
    var first = First((1, "grinder", 0, 0, 0));
    var (over, overDelta) = Sample(first, [(1, "grinder", _OneSecond, 0, 0)]);
    watch.Examine(over, overDelta);

    var (idle, idleDelta) = Sample(over, [(1, "grinder", _OneSecond, 0, 0)]);
    Assert.That(watch.Examine(idle, idleDelta), Is.Empty);

    var (busy, busyDelta) = Sample(idle, [(1, "grinder", _OneSecond * 2, 0, 0)]);
    Assert.That(watch.Examine(busy, busyDelta), Has.Count.EqualTo(1), "it went quiet and came back");
  }

  [Test]
  public void TheMemoryRuleIsAShareOfTheMachine() {
    var watch = new NotificationWatch(new() { MemoryPercent = 25 });
    var first = First((1, "hog", 0, 0, 0));
    var (snapshot, delta) = Sample(first, [(1, "hog", 0, 400, 0)]);

    var found = watch.Examine(snapshot, delta);

    Assert.That(found, Has.Count.EqualTo(1));
    Assert.That(found[0].Kind, Is.EqualTo(NotificationKind.MemoryAboveThreshold));
    Assert.That(found[0].Text, Does.Contain("40"));
  }

  [Test]
  public void TheDiskRuleIsReadPlusWritePerSecond() {
    var watch = new NotificationWatch(new() { DiskBytesPerSecond = 1024 * 1024 });
    var first = First((1, "copier", 0, 0, 0));
    var (snapshot, delta) = Sample(first, [(1, "copier", 0, 0, 8L * 1024 * 1024)]);

    var found = watch.Examine(snapshot, delta);

    Assert.That(found, Has.Count.EqualTo(1));
    Assert.That(found[0].Kind, Is.EqualTo(NotificationKind.DiskAboveThreshold));
  }

  /// <summary>
  /// The defect this project keeps meeting, arriving as an interruption. An unread counter is not a
  /// counter reading nought, and treating it as one would fire "back below the threshold" for every
  /// process the sampler could not read — and then fire the crossing again the moment it could.
  /// </summary>
  [Test]
  public void AReadingWithNoValueNeitherFiresTheRuleNorClearsIt() {
    var watch = new NotificationWatch(new() { CpuPercent = 50 });
    var first = First((1, "grinder", 0, 0, 0));
    var (over, overDelta) = Sample(first, [(1, "grinder", _OneSecond, 0, 0)]);
    Assert.That(watch.Examine(over, overDelta), Has.Count.EqualTo(1));

    // The same machine with the CPU counter unreadable: not permitted, which is not nought.
    var blind = new SystemSnapshot { TimestampTicks = System.Diagnostics.Stopwatch.Frequency * 2 };
    var buffer = blind.PrepareProcesses(1);
    buffer[0] = default;
    buffer[0].Key = new(1, 1000);
    buffer[0].Name = "grinder";
    buffer[0].CpuTimeNs = Counter.NotPermitted;
    blind.System.CoreCount = 8;
    blind.System.TotalMemoryBytes = Counter.Of(1000);
    var blindDelta = new SnapshotDelta();
    blindDelta.Update(over, blind, CpuPercentMode.Normalized);

    Assert.That(watch.Examine(blind, blindDelta), Is.Empty, "an unknown is not a crossing");

    // And the rule was not disarmed by the blind sample: coming back over is not a new event,
    // because as far as anybody knows it never left.
    var (again, againDelta) = Sample(blind, [(1, "grinder", _OneSecond * 3, 0, 0)]);
    Assert.That(watch.Examine(again, againDelta), Is.Empty, "an unknown is not a return either");
  }

  #endregion

  #region services (PRD §41, §64)

  private static ServiceRecord Unit(string name, ServiceState state)
    => new(name, null, state, null, false, 0, null, $"/usr/lib/systemd/system/{name}", null);

  [Test]
  public void AUnitThatStoppedIsAnnouncedAndOnlyOnce() {
    var watch = new NotificationWatch(new() { Services = ["nginx.service"] });

    Assert.That(watch.ExamineServices([Unit("nginx.service", ServiceState.Running)]), Is.Empty);

    var found = watch.ExamineServices([Unit("nginx.service", ServiceState.Inactive)]);
    Assert.That(found, Has.Count.EqualTo(1));
    Assert.That(found[0].Kind, Is.EqualTo(NotificationKind.ServiceStopped));
    Assert.That(found[0].Text, Does.Contain("nginx.service"));

    Assert.That(watch.ExamineServices([Unit("nginx.service", ServiceState.Inactive)]), Is.Empty, "it is already stopped");
  }

  /// <summary>
  /// A unit whose state could not be determined is neither running nor stopped. Passing through
  /// <see cref="ServiceState.Unknown"/> must not fire, in either direction (PRD §72.3).
  /// </summary>
  [Test]
  public void AUnitWhoseStateIsUnknownFiresNothing() {
    var watch = new NotificationWatch(new() { Services = ["nginx.service"] });
    watch.ExamineServices([Unit("nginx.service", ServiceState.Running)]);

    Assert.That(watch.ExamineServices([Unit("nginx.service", ServiceState.Unknown)]), Is.Empty);
    Assert.That(watch.ExamineServices([Unit("nginx.service", ServiceState.Inactive)]), Is.Empty, "and it did not become a stop afterwards");
  }

  [Test]
  public void AUnitNobodyNamedIsNotWatched() {
    var watch = new NotificationWatch(new() { Services = ["nginx.service"] });
    watch.ExamineServices([Unit("cups.service", ServiceState.Running)]);

    Assert.That(watch.ExamineServices([Unit("cups.service", ServiceState.Inactive)]), Is.Empty);
  }

  #endregion

  #region stored locally, in the settings file (PRD §64, §67)

  [Test]
  public void EveryRuleSurvivesTheFile() {
    var written = new UserSettings {
      Notifications = new() {
        ProcessStarted = true,
        ProcessEnded = true,
        Names = ["firefox", "sshd"],
        CpuPercent = 90,
        MemoryPercent = 25,
        DiskBytesPerSecond = 104857600,
        Services = ["nginx.service"],
      },
    };

    var read = UserSettings.Parse(written.Write()).Notifications;

    Assert.Multiple(() => {
      Assert.That(read.ProcessStarted, Is.True);
      Assert.That(read.ProcessEnded, Is.True);
      Assert.That(read.Names, Is.EqualTo(new[] { "firefox", "sshd" }));
      Assert.That(read.CpuPercent, Is.EqualTo(90));
      Assert.That(read.MemoryPercent, Is.EqualTo(25));
      Assert.That(read.DiskBytesPerSecond, Is.EqualTo(104857600));
      Assert.That(read.Services, Is.EqualTo(new[] { "nginx.service" }));
    });
  }

  /// <summary>
  /// A file that says nothing about notifications writes nothing about them. Seven lines of
  /// <c>notify.cpu=</c> in everybody's file would be seven lines nobody reads, and the absence of a
  /// line is what "no rule" already means (PRD §67).
  /// </summary>
  [Test]
  public void AFileWithNoRulesInItGrowsNoNotifyLines()
    => Assert.That(new UserSettings().Write(), Does.Not.Contain("notify."));

  /// <summary>
  /// Nought is a threshold somebody could mean — "tell me about anything using any CPU at all" is a
  /// sentence — so a mistyped number must leave the rule unset rather than arm it at nought and fire
  /// on every process on the machine.
  /// </summary>
  [Test]
  public void AThresholdThatCannotBeParsedLeavesTheRuleUnset() {
    Assert.That(UserSettings.Parse("notify.cpu=lots").Notifications.CpuPercent, Is.Null);
    Assert.That(UserSettings.Parse("notify.cpu=-5").Notifications.CpuPercent, Is.Null);
    Assert.That(UserSettings.Parse("notify.cpu=0").Notifications.CpuPercent, Is.EqualTo(0), "but nought itself is a rule");
  }

  /// <summary>
  /// A line this build understands with a value it does not leaves the setting alone rather than
  /// failing the file — which is the same thing every other key here does, and is why a settings
  /// file with one bad line still starts the program (PRD §67).
  /// </summary>
  [Test]
  public void ABadValueDoesNotStopTheRestOfTheFileBeingRead() {
    var settings = UserSettings.Parse("notify.cpu=lots\nnotify.memory=25");

    Assert.That(settings.Notifications.CpuPercent, Is.Null);
    Assert.That(settings.Notifications.MemoryPercent, Is.EqualTo(25));
  }

  [Test]
  public void ATrailingCommaInANameListIsNotAnEmptyName()
    => Assert.That(UserSettings.Parse("notify.name=firefox,").Notifications.Names, Is.EqualTo(new[] { "firefox" }));

  #endregion

  [Test]
  public void SeveralThingsAtOnceBecomeOneLineWithACount() {
    Assert.That(NotificationWatch.Summarise([]), Is.Empty);
    Assert.That(NotificationWatch.Summarise([new(NotificationKind.ProcessStarted, "a started")]), Is.EqualTo("a started"));
    Assert.That(
      NotificationWatch.Summarise([
        new(NotificationKind.ProcessStarted, "a started"),
        new(NotificationKind.ProcessStarted, "b started"),
        new(NotificationKind.ProcessStarted, "c started"),
      ]),
      Is.EqualTo("a started (and 2 more)")
    );
  }

  [Test]
  public void ANotificationNobodyFilledInIsNotOneOfTheRealKinds()
    => Assert.That(default(Notification).Kind, Is.EqualTo(NotificationKind.Unclassified));

}
