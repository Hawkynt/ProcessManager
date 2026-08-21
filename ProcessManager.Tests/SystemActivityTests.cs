using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What is using the machine (PRD §51).
/// </summary>
/// <remarks>
/// The selection is hand-written — a single pass keeping the best five rather than sorting four
/// hundred, three times over, every second — so most of these check it against the answer a sort
/// would have given. A top-five list that is subtly wrong is worse than none, because nothing about
/// it looks wrong.
/// </remarks>
[TestFixture]
public sealed class SystemActivityTests {

  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Machine(params ulong[] residents) {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    var first = before.PrepareProcesses(residents.Length);
    for (var i = 0; i < residents.Length; ++i) {
      first[i] = default;
      first[i].Key = new(i + 1, 1000);
      first[i].CpuTimeNs = Counter.Of(0);
    }

    var after = new SystemSnapshot { TimestampTicks = System.Diagnostics.Stopwatch.Frequency };
    var second = after.PrepareProcesses(residents.Length);
    for (var i = 0; i < residents.Length; ++i) {
      second[i] = default;
      second[i].Key = new(i + 1, 1000);
      second[i].Name = $"p{i}";
      second[i].WorkingSetBytes = Counter.Of(residents[i]);
      second[i].CpuTimeNs = Counter.Of(0);
    }

    after.System.TotalMemoryBytes = Counter.Of(1_000_000);
    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);
    return (after, delta);
  }

  private static List<string> Names(IReadOnlyList<ActivityEntry> entries) {
    var names = new List<string>();
    foreach (var entry in entries)
      names.Add(entry.Name);

    return names;
  }

  [Test]
  public void TheBiggestComesFirstAndTheListStopsAtFive() {
    var (snapshot, delta) = Machine(10, 90, 30, 70, 50, 20, 80);

    var top = SystemActivity.Top(snapshot, delta, ProcessField.WorkingSetBytes);

    Assert.That(top, Has.Count.EqualTo(SystemActivity.Depth));
    Assert.That(Names(top), Is.EqualTo(new[] { "p1", "p6", "p3", "p4", "p2" }));
  }

  /// <summary>
  /// The whole point of the hand-written selection is that it agrees with a sort. Every ordering of
  /// the same set has to produce the same five, including the ones where the largest arrives last
  /// and where it arrives first.
  /// </summary>
  [Test]
  public void ItAgreesWithASortWhateverOrderTheProcessesArriveIn() {
    ulong[] values = [3, 17, 5, 11, 2, 19, 7, 13];
    var expected = new List<ulong>(values);
    expected.Sort();
    expected.Reverse();

    foreach (var arrangement in new[] {
      values,
      (ulong[])[.. values.Reverse()],
      [19, 3, 17, 5, 11, 2, 7, 13],
      [3, 5, 2, 7, 11, 13, 17, 19],
    }) {
      var (snapshot, delta) = Machine(arrangement);
      var top = SystemActivity.Top(snapshot, delta, ProcessField.WorkingSetBytes);

      var got = new List<ulong>();
      foreach (var entry in top)
        got.Add(arrangement[entry.Key.Pid - 1]);

      Assert.That(got, Is.EqualTo(expected.GetRange(0, SystemActivity.Depth)), string.Join(",", arrangement));
    }
  }

  [Test]
  public void FewerProcessesThanTheDepthGivesAShorterList() {
    var (snapshot, delta) = Machine(5, 1);

    Assert.That(SystemActivity.Top(snapshot, delta, ProcessField.WorkingSetBytes), Has.Count.EqualTo(2));
  }

  /// <summary>
  /// A process using none of something is not "using the least of it" — it does not belong in a list
  /// of what is using the resource at all, and padding the list with zeros is how a top-five becomes
  /// noise on an idle machine.
  /// </summary>
  [Test]
  public void ProcessesUsingNoneOfItAreLeftOut() {
    var (snapshot, delta) = Machine(7, 0, 0, 0, 0, 0);

    var top = SystemActivity.Top(snapshot, delta, ProcessField.WorkingSetBytes);

    Assert.That(top, Has.Count.EqualTo(1));
    Assert.That(top[0].Name, Is.EqualTo("p0"));
  }

  /// <summary>
  /// A reading that does not exist is not a small one. A process whose counters cannot be read must
  /// not sit at the bottom of the list as though it were idle (PRD §5.3).
  /// </summary>
  [Test]
  public void AProcessWhoseReadingIsUnknownIsNotRankedAsIdle() {
    var (snapshot, delta) = Machine(5, 9);
    var processes = snapshot.PrepareProcesses(2);
    processes[1].WorkingSetBytes = Counter.NotPermitted;

    var top = SystemActivity.Top(snapshot, delta, ProcessField.WorkingSetBytes);

    foreach (var entry in top)
      Assert.That(entry.Key.Pid, Is.Not.EqualTo(2));
  }

  [Test]
  public void WithoutASecondSampleThereIsNothingToRank() {
    var (snapshot, _) = Machine(5, 9);

    Assert.That(SystemActivity.Top(snapshot, null, ProcessField.WorkingSetBytes), Is.Empty);
  }

  /// <summary>The key is carried so a caller can navigate to the process, not merely read about it.</summary>
  [Test]
  public void EachEntryCarriesEnoughToGoToTheProcess() {
    var (snapshot, delta) = Machine(10, 20);

    var top = SystemActivity.Top(snapshot, delta, ProcessField.WorkingSetBytes);

    Assert.That(top[0].Key.Pid, Is.EqualTo(2));
    Assert.That(top[0].Key.StartTicks, Is.EqualTo(1000ul), "the identity pair, not a bare pid");
    Assert.That(top[0].Value, Is.Not.Empty);
  }

  #region the machine's own churn

  /// <summary>
  /// Divided by the real elapsed time rather than assumed to be a second: a page refreshing every
  /// five seconds would otherwise report five seconds of forks as a per-second rate.
  /// </summary>
  [Test]
  public void RatesAreDividedByTheIntervalTheyWereCountedIn() {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    before.PrepareProcesses(0);

    // Two seconds later, with four processes that did not exist before.
    var after = new SystemSnapshot { TimestampTicks = System.Diagnostics.Stopwatch.Frequency * 2 };
    var records = after.PrepareProcesses(4);
    for (var i = 0; i < 4; ++i) {
      records[i] = default;
      records[i].Key = new(i + 1, 1000);
      records[i].Name = $"p{i}";
    }

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);

    var rates = SystemActivity.Rates(after, delta);
    foreach (var row in rates)
      if (row.Label == "Processes started") {
        Assert.That(row.Value, Does.StartWith("2"), "four in two seconds is two a second");
        return;
      }

    Assert.Fail("no started row");
  }

  [Test]
  public void WithoutASecondSampleTheRatesSaySoRatherThanReadingZero() {
    var snapshot = new SystemSnapshot();
    snapshot.PrepareProcesses(0);

    foreach (var row in SystemActivity.Rates(snapshot, null))
      if (row.Label is "Processes started" or "Processes ended")
        Assert.That(row.Value, Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)), row.Label);
  }

  #endregion

}
