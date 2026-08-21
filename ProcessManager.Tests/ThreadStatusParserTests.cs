using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The context-switch counts and affinity a thread's <c>status</c> carries (PRD §29).
/// </summary>
/// <remarks>
/// No file access and no platform attribute, so this runs on every CI leg — the Linux reading is
/// checked on machines that have no <c>/proc</c> at all (PRD §9.2).
/// </remarks>
[TestFixture]
public sealed class ThreadStatusParserTests {

  // Written with explicit escapes rather than as a raw string: the kernel separates every label from
  // its value with a TAB, and a raw literal would carry the two characters "\t" instead of one tab —
  // which is exactly the separator this parser has to get right.
  private const string COMPLETE =
    "Name:\trt-audio\n"
    + "Umask:\t0022\n"
    + "State:\tS (sleeping)\n"
    + "Tgid:\t1001\n"
    + "Pid:\t1017\n"
    + "Cpus_allowed:\t00000000,0000000c\n"
    + "Cpus_allowed_list:\t2-3\n"
    + "Mems_allowed_list:\t0\n"
    + "voluntary_ctxt_switches:\t4200\n"
    + "nonvoluntary_ctxt_switches:\t17\n";

  [Test]
  public void BothHalvesAndTheAffinityComeOffTheSameFile() {
    var status = ThreadStatusParser.Parse(COMPLETE);

    Assert.That(status.VoluntaryContextSwitches.Value, Is.EqualTo(4200ul));
    Assert.That(status.InvoluntaryContextSwitches.Value, Is.EqualTo(17ul));
    Assert.That(status.Affinity, Is.EqualTo("2-3"));
    Assert.That(status.TotalContextSwitches.Value, Is.EqualTo(4217ul));
  }

  /// <summary>
  /// <c>nonvoluntary_ctxt_switches</c> contains the whole of <c>voluntary_ctxt_switches</c>, so a
  /// label search that is not anchored at the start of the line reads the involuntary count into
  /// both halves and reports a total twice the truth.
  /// </summary>
  [Test]
  public void TheInvoluntaryLineIsNotMistakenForTheVoluntaryOne() {
    var status = ThreadStatusParser.Parse(COMPLETE);

    Assert.That(status.VoluntaryContextSwitches.Value, Is.Not.EqualTo(status.InvoluntaryContextSwitches.Value));
    Assert.That(status.TotalContextSwitches.Value, Is.EqualTo(4217ul), "not 34, and not 8400");
  }

  /// <summary>
  /// The label is separated from the value by a TAB, not a space. Trimming only spaces is how the
  /// capability mask in this same file was once read as zero for every process on the machine.
  /// </summary>
  [Test]
  public void TheTabAfterTheLabelIsNotPartOfTheValue()
    => Assert.That(ThreadStatusParser.Parse("Cpus_allowed_list:\t0-7,16\n").Affinity, Is.EqualTo("0-7,16"));

  /// <summary>
  /// Only kernels built with <c>CONFIG_SCHEDSTATS</c> or <c>CONFIG_TASK_DELAY_ACCT</c> write the
  /// switch lines. Their absence means nobody told us, not that the thread never switched — and
  /// <c>default(Counter)</c> is a confident zero, so this has to be stated (PRD §72.3).
  /// </summary>
  [Test]
  public void AKernelThatWritesNoSwitchLinesLeavesThemUnknown() {
    var status = ThreadStatusParser.Parse("Name:\tworker\nCpus_allowed_list:\t0-3\n");

    Assert.That(status.VoluntaryContextSwitches.HasValue, Is.False);
    Assert.That(status.VoluntaryContextSwitches.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(status.InvoluntaryContextSwitches.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(status.Affinity, Is.EqualTo("0-3"), "the lines that are there are still answers");
  }

  /// <summary>A status file with nothing we want in it still must not invent numbers.</summary>
  [Test]
  public void AnEmptyFileYieldsNoValuesAtAll() {
    var status = ThreadStatusParser.Parse(string.Empty);

    Assert.That(status.VoluntaryContextSwitches.HasValue, Is.False);
    Assert.That(status.InvoluntaryContextSwitches.HasValue, Is.False);
    Assert.That(status.TotalContextSwitches.HasValue, Is.False);
    Assert.That(status.Affinity, Is.Null);
  }

  /// <summary>
  /// A status nobody was allowed to open. The reason has to survive to the view, because
  /// "not permitted" is the one the elevated helper can do something about.
  /// </summary>
  [Test]
  public void AStatusThatCouldNotBeReadCarriesTheReasonInEveryCounter() {
    var status = ThreadStatus.Unreadable(UnknownReason.NotPermitted);

    Assert.That(status.VoluntaryContextSwitches.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(status.InvoluntaryContextSwitches.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(status.TotalContextSwitches.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(status.Affinity, Is.Null);
  }

  /// <summary>
  /// One known half plus one unknown half is not the known half: a total that quietly leaves out
  /// everything the kernel did not report is worse than no total.
  /// </summary>
  [Test]
  public void ATotalWithOneMissingHalfIsUnknownRatherThanTheOtherHalf() {
    var status = ThreadStatusParser.Parse("voluntary_ctxt_switches:\t5\n");

    Assert.That(status.VoluntaryContextSwitches.Value, Is.EqualTo(5ul));
    Assert.That(status.TotalContextSwitches.HasValue, Is.False);
    Assert.That(status.TotalContextSwitches.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>A value that is not a number is a corrupt reading, which is not the same as a zero.</summary>
  [Test]
  public void AValueThatIsNotANumberIsInvalidRatherThanZero()
    => Assert.That(
      ThreadStatusParser.Parse("voluntary_ctxt_switches:\tmany\n").VoluntaryContextSwitches.Reason,
      Is.EqualTo(UnknownReason.CounterInvalid)
    );

  /// <summary>A fixture edited on Windows must parse the same as one the kernel wrote.</summary>
  [Test]
  public void CarriageReturnsDoNotBecomePartOfTheAffinity()
    => Assert.That(ThreadStatusParser.Parse("Cpus_allowed_list:\t0-3\r\n").Affinity, Is.EqualTo("0-3"));

}
