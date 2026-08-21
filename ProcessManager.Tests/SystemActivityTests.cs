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


  #region what the machine is sending (PRD §51)

  /// <summary>
  /// Two samples of a machine with the given interfaces, each moving the given bytes in the second.
  /// </summary>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Wires(params (string Name, ulong In, ulong Out)[] wires) {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    before.PrepareProcesses(0);
    var first = before.PrepareNetworks(wires.Length);
    for (var i = 0; i < wires.Length; ++i) {
      first[i] = default;
      first[i].Name = wires[i].Name;
      first[i].ReceivedBytes = Counter.Of(0);
      first[i].SentBytes = Counter.Of(0);
    }

    var after = new SystemSnapshot { TimestampTicks = System.Diagnostics.Stopwatch.Frequency };
    after.PrepareProcesses(0);
    var second = after.PrepareNetworks(wires.Length);
    for (var i = 0; i < wires.Length; ++i) {
      second[i] = default;
      second[i].Name = wires[i].Name;
      second[i].ReceivedBytes = Counter.Of(wires[i].In);
      second[i].SentBytes = Counter.Of(wires[i].Out);
    }

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);
    return (after, delta);
  }

  private static string RowValue(IReadOnlyList<PerformanceRow> rows, string label) {
    foreach (var row in rows)
      if (row.Label == label)
        return row.Value;

    Assert.Fail($"no row called '{label}'");
    return string.Empty;
  }

  /// <summary>
  /// The machine's own traffic is the sum of its interfaces. §18 refuses <em>per-process</em> byte
  /// counters because no portable source exists; that says nothing about the machine as a whole,
  /// whose interfaces have counted every byte since boot.
  /// </summary>
  [Test]
  public void TheMachinesTrafficIsTheSumOfItsInterfaces() {
    var (snapshot, delta) = Wires(("eth0", 1000, 100), ("wlan0", 2000, 200));

    var rows = SystemActivity.Rates(snapshot, delta);

    Assert.That(RowValue(rows, "Network in"), Does.Contain("3.0"));
    Assert.That(RowValue(rows, "Network out"), Does.Contain("300"));
  }

  /// <summary>
  /// Loopback is left out. Traffic a machine sends to itself crosses no wire and is counted twice —
  /// once out and once in — so a database and its client on one host would read as heavy network
  /// users while nothing had left the box.
  /// </summary>
  [Test]
  public void WhatTheMachineSendsToItselfIsNotNetworkTraffic() {
    var (snapshot, delta) = Wires(("lo", 900_000, 900_000), ("eth0", 1000, 100));

    var rows = SystemActivity.Rates(snapshot, delta);

    Assert.That(RowValue(rows, "Network in"), Does.Contain("1.0"), "the loopback megabyte is not in it");
    Assert.That(RowValue(rows, "Network in"), Does.Not.Contain("M"));
  }

  /// <summary>
  /// A machine whose interfaces have no rates yet says so rather than reporting nought. Nought is a
  /// measurement — an idle link — and this is the absence of one (PRD §72.3).
  /// </summary>
  [Test]
  public void AMachineNobodyHasSampledTwiceDoesNotReportNought() {
    var lonely = new SystemSnapshot { TimestampTicks = 0 };
    lonely.PrepareProcesses(0);
    lonely.PrepareNetworks(0);

    var rows = SystemActivity.Rates(lonely, null);

    Assert.That(RowValue(rows, "Network in"), Is.Not.EqualTo("0"));
    Assert.That(RowValue(rows, "Network out"), Is.Not.EqualTo("0"));
  }

  /// <summary>
  /// A machine with nothing but loopback reports unknown rather than nought, for the same reason:
  /// there is no measurement of anything leaving it, and nought would claim there was.
  /// </summary>
  [Test]
  public void AMachineWithOnlyLoopbackHasNoMeasurementToReport() {
    var (snapshot, delta) = Wires(("lo", 900_000, 900_000));

    var rows = SystemActivity.Rates(snapshot, delta);

    Assert.That(RowValue(rows, "Network in"), Is.Not.EqualTo("0"));
  }

  #endregion


  /// <summary>
  /// A top-five entry carries the process it names, so clicking it can navigate (PRD §51).
  /// </summary>
  /// <remarks>
  /// The identity pair rather than a pid. The page is modeless and outlives any particular sample,
  /// so a row read a second after it was drawn must not be able to take somebody to whatever has
  /// since been given that number (PRD §8.2).
  /// </remarks>
  [Test]
  public void ATopEntryKnowsWhichProcessItNames() {
    var (snapshot, delta) = Machine(10, 90, 30);

    var top = SystemActivity.Top(snapshot, delta, ProcessField.WorkingSetBytes);

    Assert.That(top, Is.Not.Empty);
    foreach (var entry in top) {
      Assert.That(entry.Key.Pid, Is.GreaterThan(0), entry.Name);
      Assert.That(entry.Key.StartTicks, Is.Not.Zero, "a pid alone is not an identity");
    }
  }

  /// <summary>
  /// And a row that is not about a process says so, so a click on "Context switches" navigates
  /// nowhere rather than to whatever the pooled control last displayed.
  /// </summary>
  [Test]
  public void ARowThatIsNotAboutAProcessCarriesNoProcess() {
    var (snapshot, delta) = Wires(("eth0", 1000, 100));

    foreach (var row in SystemActivity.Rates(snapshot, delta))
      Assert.That(row.IsAboutAProcess, Is.False, row.Label);
  }

}
