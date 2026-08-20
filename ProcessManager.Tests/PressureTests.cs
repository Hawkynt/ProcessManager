using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Pressure stall information (PRD §46, §47, §48).
/// </summary>
/// <remarks>
/// A different question from utilisation and usually the better one: a processor at 100 % is not in
/// trouble if nothing is waiting for it, and one at 60 % with things queued behind it is.
/// </remarks>
[TestFixture]
public sealed class PressureTests {

  /// <summary>Read off this machine while it was under real load.</summary>
  private const string _Cpu = """
    some avg10=37.83 avg60=55.76 avg300=48.44 total=4111918357
    full avg10=0.00 avg60=0.00 avg300=0.00 total=0
    """;

  [Test]
  public void BothHalvesAreReadWithAllThreeWindows() {
    var reading = PressureParser.Parse(_Cpu);

    Assert.That(reading.Some.Average10.Value, Is.EqualTo(37.83).Within(0.001));
    Assert.That(reading.Some.Average60.Value, Is.EqualTo(55.76).Within(0.001));
    Assert.That(reading.Some.Average300.Value, Is.EqualTo(48.44).Within(0.001));
    Assert.That(reading.Some.TotalMicroseconds.Value, Is.EqualTo(4111918357ul));
    Assert.That(reading.Full.Average10.Value, Is.Zero);
    Assert.That(reading.Full.TotalMicroseconds.Value, Is.Zero);
  }

  /// <summary>
  /// "some" and "full" are different questions and swapping them would be catastrophic and quiet:
  /// full pressure means nothing ran at all, and reporting a busy machine's "some" as "full" would
  /// call every loaded machine a thrashing one.
  /// </summary>
  [Test]
  public void SomeAndFullAreNotConfused() {
    var reading = PressureParser.Parse(_Cpu);

    Assert.That(reading.Some.Average10.Value, Is.GreaterThan(0));
    Assert.That(reading.Full.Average10.Value, Is.Zero);
  }

  /// <summary>
  /// <c>/proc/pressure/irq</c> has only a full line — an interrupt stalls everything or nothing, so
  /// "some" would be meaningless. The missing half must be unknown, not zero.
  /// </summary>
  [Test]
  public void AFileWithOnlyOneHalfLeavesTheOtherUnknown() {
    var reading = PressureParser.Parse("full avg10=0.00 avg60=0.00 avg300=0.00 total=721646572");

    Assert.That(reading.Full.HasValue, Is.True);
    Assert.That(reading.Some.HasValue, Is.False);
    Assert.That(reading.Some.Average10.HasValue, Is.False);
  }

  /// <summary>
  /// A kernel without CONFIG_PSI, or booted with psi=0, has no such file. Unknown rather than zero:
  /// a machine under no pressure and a machine that cannot say look identical otherwise, and one of
  /// them may be thrashing (PRD §5.3).
  /// </summary>
  [Test]
  public void AKernelThatCannotSayIsNotAMachineUnderNoPressure() {
    var missing = PressureReading.Unknown;

    Assert.That(missing.HasValue, Is.False);
    Assert.That(missing.Some.Average10.HasValue, Is.False);
    Assert.That(missing.Some.Average10.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));

    var parsed = PressureParser.Parse(string.Empty);
    Assert.That(parsed.HasValue, Is.False);
  }

  /// <summary>Genuinely nought is a value, and must not read as an absence.</summary>
  [Test]
  public void ZeroPressureIsAReadingAndNotAnAbsence() {
    var reading = PressureParser.Parse("some avg10=0.00 avg60=0.00 avg300=0.00 total=0");

    Assert.That(reading.Some.HasValue, Is.True);
    Assert.That(reading.Some.Average10.HasValue, Is.True);
    Assert.That(reading.Some.Average10.Value, Is.Zero);
  }

  [Test]
  public void RubbishIsSkippedRatherThanThrown() {
    Assert.That(() => PressureParser.Parse("nonsense\nsome\nsome avg10=\n"), Throws.Nothing);
    Assert.That(PressureParser.Parse("nonsense").HasValue, Is.False);
  }

  /// <summary>
  /// The kernel prints two decimals with a point, whatever the machine's locale. A parser that used
  /// the current culture would read 37.83 as 3783 where the comma is the decimal separator.
  /// </summary>
  /// <remarks>
  /// The culture is built by cloning the invariant one and changing its separator rather than by
  /// naming a locale: this program is published with invariant globalization, so
  /// <c>new CultureInfo("de-DE")</c> throws here — which is itself worth knowing, and is why the
  /// first version of this test failed.
  /// </remarks>
  [Test]
  public void TheNumbersAreReadTheWayTheKernelWritesThemAndNotHowTheLocaleWould() {
    var comma = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
    comma.NumberFormat.NumberDecimalSeparator = ",";
    comma.NumberFormat.NumberGroupSeparator = ".";

    var was = System.Globalization.CultureInfo.CurrentCulture;
    try {
      System.Globalization.CultureInfo.CurrentCulture = comma;
      Assert.That(PressureParser.Parse(_Cpu).Some.Average10.Value, Is.EqualTo(37.83).Within(0.001));
    } finally {
      System.Globalization.CultureInfo.CurrentCulture = was;
    }
  }

  #region on the page

  private static string Value(IReadOnlyList<PerformanceSection> sections, string label) {
    foreach (var section in sections)
      foreach (var row in section.Rows)
        if (row.Label == label)
          return row.Value;

    Assert.Fail($"no row called {label}");
    return string.Empty;
  }

  private static SystemSnapshot Machine(PressureReading cpu) {
    var snapshot = new SystemSnapshot();
    snapshot.PrepareProcesses(0);
    snapshot.System.CpuPressure = cpu;
    snapshot.System.MemoryPressure = PressureReading.Unknown;
    snapshot.System.IoPressure = PressureReading.Unknown;
    return snapshot;
  }

  /// <summary>
  /// All three windows on one line, because the shape between them is the information: ten above
  /// sixty is a spike starting, ten below sixty is one ending.
  /// </summary>
  [Test]
  public void ThePageShowsAllThreeWindows() {
    var sections = PerformanceReport.Build(new(), Machine(PressureParser.Parse(_Cpu)));

    Assert.That(Value(sections, "Stalled on CPU"), Does.Contain("37.8"));
    Assert.That(Value(sections, "Stalled on CPU"), Does.Contain("55.8"));
    Assert.That(Value(sections, "Stalled on CPU"), Does.Contain("48.4"));
  }

  [Test]
  public void AMachineThatCannotSaySaysSoOnThePageToo() {
    var sections = PerformanceReport.Build(new(), Machine(PressureReading.Unknown));

    Assert.That(Value(sections, "Stalled on CPU"), Is.EqualTo("n/a"));
    Assert.That(Value(sections, "Stalled on CPU"), Does.Not.Contain("0"));
  }

  #endregion

}
