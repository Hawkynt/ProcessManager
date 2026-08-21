using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// That a field this platform cannot fill says so, rather than looking filled (PRD §72.3, §101).
/// </summary>
/// <remarks>
/// <para>
/// §101 will not let this program claim to replace anything until "every unsupported
/// platform-specific feature explicitly communicates why". That is a claim about all hundred and
/// something fields at once, and checking it one field at a time is how it has gone wrong four
/// times: <c>app.name</c> read "none" on Windows, which is a Linux finding meaning the machine has
/// no desktop entry; <c>runtime</c> rendered an empty placeholder; two counters read as a confident
/// nought. Every one of those sat inside a ticked box.
/// </para>
/// <para>
/// So the check is over the registry rather than over a list somebody maintains. A field declared as
/// belonging to another platform is sampled here, and whatever comes back has to be one of the marks
/// that mean "no answer" — never a number, never a name, never an empty cell that reads as one.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PlatformHonestyTests {

  private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  /// <summary>
  /// Every mark that means "there is no answer here". A cell holding one of these is honest whatever
  /// the field is; a cell holding anything else is a statement.
  /// </summary>
  private static readonly string[] _NonAnswers = [
    Humanize.Placeholder(UnknownReason.NotPermitted),
    Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
    Humanize.Placeholder(UnknownReason.NotImplementedHere),
    Humanize.Placeholder(UnknownReason.ProcessExited),
    Humanize.Placeholder(UnknownReason.SourceGone),
    Humanize.Placeholder(UnknownReason.NotSampledYet),
    Humanize.Placeholder(UnknownReason.CounterInvalid),
    Humanize.Placeholder(UnknownReason.NoLimit),
  ];

  private static ProcessRecord Sample() {
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

    foreach (var process in snapshot.Processes)
      if (process.Pid == 1)
        return process;

    Assert.Fail("no process 1 in the fixture");
    return default;
  }

  /// <summary>
  /// A field only Windows can fill renders a reason here, not a value.
  /// </summary>
  /// <remarks>
  /// The Linux probe never touches these, so what is on screen is whatever the record was
  /// constructed with — which is exactly where a confident default becomes a confident answer. A
  /// blank cell counts as a failure too: a reader cannot tell "nothing to report" from "nobody
  /// asked", and one of those is a finding.
  /// </remarks>
  [Test]
  public void AFieldOnlyAnotherPlatformCanFillSaysSoHere() {
    var process = Sample();

    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All) {
        if (descriptor.Platforms.HasFlag(FieldPlatforms.Linux))
          continue;

        // A drawn history has no text at all; it is a plot, and the registry marks it as one.
        if (descriptor.Series is not null)
          continue;

        var text = FieldAccessor.Text(descriptor.Id, in process, null, 0);
        Assert.That(
          Array.IndexOf(_NonAnswers, text) >= 0,
          Is.True,
          $"{descriptor.Key} is a {descriptor.Platforms} field and reads '{text}' on Linux"
        );
      }
    });
  }

  /// <summary>
  /// And it exports as nothing rather than as one of those marks. The marks are for a person reading
  /// a column; a spreadsheet cell holding "n/a" is a string where every other row has a number.
  /// </summary>
  [Test]
  public void AFieldOnlyAnotherPlatformCanFillExportsAsNothing() {
    var process = Sample();

    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All) {
        if (descriptor.Platforms.HasFlag(FieldPlatforms.Linux) || descriptor.Series is not null)
          continue;

        var raw = FieldAccessor.RawText(descriptor.Id, in process, null, 0);
        Assert.That(raw, Is.Null.Or.Empty, $"{descriptor.Key} exported '{raw}'");
      }
    });
  }

  /// <summary>
  /// The marks are all different from each other. Two reasons rendering the same character would
  /// make "nobody may look" and "this platform has no such thing" one cell, which is the distinction
  /// the whole scheme exists to keep.
  /// </summary>
  [Test]
  public void EveryReasonHasItsOwnMark() {
    Assert.That(_NonAnswers, Is.Unique);
    Assert.That(_NonAnswers, Has.None.Empty);
  }

  /// <summary>
  /// And no mark is something a real value could be mistaken for. A reason rendering as "0" or as a
  /// letter would be indistinguishable from a reading in the column beside it.
  /// </summary>
  [Test]
  public void NoMarkCouldBeMistakenForAReading() {
    Assert.Multiple(() => {
      foreach (var mark in _NonAnswers) {
        Assert.That(double.TryParse(mark, out _), Is.False, $"'{mark}' parses as a number");
        Assert.That(mark.Length, Is.LessThanOrEqualTo(4), $"'{mark}' is wide enough to be a value");
      }
    });
  }

}
