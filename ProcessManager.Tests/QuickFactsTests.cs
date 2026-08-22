using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What a row says about itself for a pointer resting on it (PRD §24).
/// </summary>
/// <remarks>
/// The interesting requirement is the second one: a tooltip performs no expensive synchronous
/// collection. That is held here by where the text comes from rather than by a rule somebody has to
/// remember — every line is <see cref="FieldAccessor"/> against the record already in the snapshot,
/// and the accessor reads the record and nothing else.
/// </remarks>
[TestFixture]
public sealed class QuickFactsTests {

  private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  private static ProcessRecord Sample(out int index) {
    var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = 0,
      ClockTicksPerSecond = 100,
      PageSize = 4096,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    probe.Dispose();

    if (snapshot.TryGetProcess(new(1, snapshot.Processes[0].Key.StartTicks), out var record, out index))
      return record;

    for (var i = 0; i < snapshot.Processes.Length; ++i)
      if (snapshot.Processes[i].Pid == 1) {
        index = i;
        return snapshot.Processes[i];
      }

    Assert.Fail("no process 1 in the fixture");
    index = -1;
    return default;
  }

  [Test]
  public void ItSaysTheThingsSomebodyPointingAtARowWants() {
    var process = Sample(out var index);

    var facts = QuickFacts.Of(in process, null, index);
    var labels = new List<string>();
    foreach (var (label, _) in facts)
      labels.Add(label);

    Assert.That(labels, Does.Contain("PID"));
    Assert.That(labels, Does.Contain("User"));
    Assert.That(labels, Does.Contain("Command line"));
    Assert.That(facts, Has.Count.GreaterThan(8));
  }

  /// <summary>
  /// The name is the heading rather than a line, because it is what somebody is pointing at — a
  /// tooltip whose first line repeats the cell under the pointer has wasted its first line.
  /// </summary>
  [Test]
  public void TheNameIsTheHeadingAndNotALine() {
    var process = Sample(out var index);

    var described = QuickFacts.Describe(in process, null, index);
    Assert.That(described, Does.StartWith(process.Name));
    foreach (var (label, _) in QuickFacts.Of(in process, null, index))
      Assert.That(label, Is.Not.EqualTo("Process"));
  }

  /// <summary>
  /// One long command line does not become the whole tooltip. A path can run to hundreds of
  /// characters, and something that wraps to twenty lines covers the table it is describing.
  /// </summary>
  [Test]
  public void NoOneLineRunsAwayWithIt() {
    var process = Sample(out var index);

    Assert.Multiple(() => {
      foreach (var (label, value) in QuickFacts.Of(in process, null, index))
        Assert.That(value.Length, Is.LessThanOrEqualTo(97), label);
    });
  }

  /// <summary>
  /// <b>Nothing here reads a file.</b> Everything comes from the record, so a field this run never
  /// asked for renders as the mark for "nobody looked" rather than sending the pointer's movement to
  /// the disk. Asserted by giving it a record nobody filled in: an accessor that went and looked
  /// would find something, and one that only reads the record cannot.
  /// </summary>
  [Test]
  public void ItCollectsNothing() {
    var untouched = new ProcessRecord { Key = new(4242, 1), Name = "never-sampled" };

    var facts = QuickFacts.Of(in untouched, null, 0);

    Assert.That(facts, Is.Not.Empty, "it still describes what it has");
    foreach (var (label, value) in facts)
      Assert.That(value, Is.Not.Null, label);
  }

  /// <summary>
  /// And a field the run did not collect says so rather than reading as a measurement — the same
  /// rule every column follows, because it is the same accessor (PRD §72.3).
  /// </summary>
  [Test]
  public void AFieldNobodyCollectedSaysSoRatherThanReadingAsAMeasurement() {
    var untouched = new ProcessRecord {
      Key = new(4242, 1),
      Name = "never-sampled",
      HandleCount = Counter.Unknown(UnknownReason.NotSampledYet),
    };

    Assert.That(
      FieldAccessor.Text(ProcessField.HandleCount, in untouched, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet))
    );
  }

}
