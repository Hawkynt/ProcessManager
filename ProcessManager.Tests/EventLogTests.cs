using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What has happened while the program has been watching (PRD §63).
/// </summary>
/// <remarks>
/// A table says what is true now. It cannot say that the process using the processor a minute ago
/// has since exited, which is what somebody who looked away and looked back is actually asking — and
/// the question a monitor is worst at answering.
/// </remarks>
[TestFixture]
public sealed class EventLogTests {

  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Machine(params int[] pids) {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    before.PrepareProcesses(0);

    var after = new SystemSnapshot { TimestampTicks = System.Diagnostics.Stopwatch.Frequency };
    var span = after.PrepareProcesses(pids.Length);
    for (var i = 0; i < pids.Length; ++i) {
      span[i] = default;
      span[i].Key = new(pids[i], 1);
      span[i].Name = "p" + pids[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
      span[i].CpuTimeNs = Counter.Of(0);
    }

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);
    return (after, delta);
  }

  /// <summary>
  /// The first sample records nothing. Every process on the machine is new to a program that has
  /// just started looking, and a timeline whose first line is four hundred processes starting says
  /// nothing about the machine and hides everything after it.
  /// </summary>
  [Test]
  public void TheFirstSampleRecordsNothing() {
    var log = new EventLog();
    var (snapshot, delta) = Machine(1, 2, 3);

    log.Add(snapshot, delta, firstSample: true, 1000);

    Assert.That(log.Count, Is.Zero);
  }

  [Test]
  public void AProcessStartingIsRecorded() {
    var log = new EventLog();
    var (snapshot, delta) = Machine(42);

    log.Add(snapshot, delta, firstSample: false, 1000);

    Assert.That(log.Count, Is.EqualTo(1));
    Assert.That(log.Entries[0].Pid, Is.EqualTo(42));
    Assert.That(log.Entries[0].Category, Is.EqualTo(EventCategory.Lifecycle));
    Assert.That(log.Entries[0].Text, Does.Contain("started"));
  }

  /// <summary>
  /// The oldest goes when the ring is full, and the count of everything that ever happened is kept
  /// — "showing 3 of 40,000" is a different thing to tell somebody than "showing 3", and the second
  /// reads as though that is all there was.
  /// </summary>
  [Test]
  public void TheOldestGoesAndTheTotalRemembers() {
    var log = new EventLog(capacity: 3);
    for (var i = 1; i <= 10; ++i)
      log.Record(i, EventCategory.Lifecycle, "event " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));

    Assert.That(log.Count, Is.EqualTo(3));
    Assert.That(log.Total, Is.EqualTo(10));
    Assert.That(log.Entries[0].Text, Is.EqualTo("event 8"), "oldest kept");
    Assert.That(log.Entries[2].Text, Is.EqualTo("event 10"), "newest");
  }

  /// <summary>Oldest first, which is the order a timeline is read in.</summary>
  [Test]
  public void TheEntriesComeOutOldestFirst() {
    var log = new EventLog(capacity: 8);
    log.Record(1, EventCategory.Lifecycle, "first");
    log.Record(2, EventCategory.Lifecycle, "second");

    Assert.That(log.Entries[0].Text, Is.EqualTo("first"));
    Assert.That(log.Entries[1].Text, Is.EqualTo("second"));
  }

  /// <summary>
  /// What somebody asked to be told about is recorded in the same words the notification used. Two
  /// wordings for one event is two things for a reader to reconcile: the notification is the
  /// interruption and this is the record of it.
  /// </summary>
  [Test]
  public void AnAlertIsRecordedInTheWordsItWasAnnouncedIn() {
    var log = new EventLog();
    log.Add([new(NotificationKind.CpuAboveThreshold, "firefox is over 80% of a core")], 1000);

    Assert.That(log.Entries[0].Text, Is.EqualTo("firefox is over 80% of a core"));
    Assert.That(log.Entries[0].Category, Is.EqualTo(EventCategory.Threshold));
  }

  /// <summary>
  /// What the person did is its own category. Somebody reading a timeline after an incident has to
  /// be able to tell what the machine did from what they did to it, and a line that does not
  /// distinguish them is one that will be misread under exactly the pressure it exists for.
  /// </summary>
  [Test]
  public void WhatThePersonDidIsKeptApartFromWhatTheMachineDid() {
    var log = new EventLog();
    log.RecordAction(1000, "ended firefox", 42);
    log.Add([new(NotificationKind.ProcessEnded, "firefox ended")], 1001);

    Assert.That(log.Entries[0].Category, Is.EqualTo(EventCategory.UserAction));
    Assert.That(log.Entries[1].Category, Is.EqualTo(EventCategory.Lifecycle));
  }

  /// <summary>
  /// A kind nobody sorted shows as unsorted rather than as the mildest category there is — the same
  /// rule §72.3 gives for readings, applied to a classification (PRD §69).
  /// </summary>
  [Test]
  public void AKindNobodySortedShowsAsUnsorted() {
    var log = new EventLog();
    log.Add([new(NotificationKind.Unclassified, "something happened")], 1000);

    Assert.That(log.Entries[0].Category, Is.EqualTo(EventCategory.Unclassified));
  }

  [Test]
  public void EveryCategoryHasAWord() {
    Assert.Multiple(() => {
      foreach (var category in Enum.GetValues<EventCategory>())
        Assert.That(EventLog.Describe(category), Is.Not.Empty, $"{category}");
    });
  }

  /// <summary>An entry with nothing to say is not an entry.</summary>
  [Test]
  public void AnEmptySentenceIsNotRecorded() {
    var log = new EventLog();
    log.Record(1000, EventCategory.Lifecycle, string.Empty);

    Assert.That(log.Count, Is.Zero);
  }

  [Test]
  public void ClearingForgetsEverythingIncludingTheTotal() {
    var log = new EventLog();
    log.Record(1, EventCategory.Lifecycle, "something");
    log.Clear();

    Assert.That(log.Count, Is.Zero);
    Assert.That(log.Total, Is.Zero);
  }

}
