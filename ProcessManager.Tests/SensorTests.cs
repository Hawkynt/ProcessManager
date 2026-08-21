using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Batteries and sensor chips (PRD §45.3).
/// </summary>
/// <remarks>
/// Driven by recorded attribute sets rather than by this machine, because the interesting cases are
/// the ones this machine does not have: a battery that reports in µWh, a desktop with none at all, a
/// chip that publishes a channel it has nothing wired to. Every one of these runs on Windows and
/// macOS too, which is the point of keeping the parsing away from the file access (PRD §9.3).
/// </remarks>
[TestFixture]
public sealed class SensorTests {

  #region batteries

  /// <summary>
  /// The µAh family: charge and a voltage, which have to be multiplied to mean anything. Recorded
  /// from a Dell laptop, and the figures are that machine's.
  /// </summary>
  private static Dictionary<string, string> Charge() => new(StringComparer.Ordinal) {
    ["type"] = "Battery",
    ["status"] = "Discharging",
    ["capacity"] = "78",
    ["charge_now"] = "7566000",
    ["charge_full"] = "7566000",
    ["charge_full_design"] = "8334000",
    ["current_now"] = "1500000",
    ["voltage_now"] = "11878000",
    ["cycle_count"] = "132",
    ["technology"] = "Li-poly",
    ["manufacturer"] = "BYD",
    ["model_name"] = "DELL CR72X26",
  };

  /// <summary>The µWh family, which needs no arithmetic and must not be given any.</summary>
  private static Dictionary<string, string> Energy() => new(StringComparer.Ordinal) {
    ["type"] = "Battery",
    ["status"] = "Charging",
    ["energy_now"] = "30000000",
    ["energy_full"] = "50000000",
    ["energy_full_design"] = "60000000",
    ["power_now"] = "10000000",
    ["voltage_now"] = "11000000",
  };

  [Test]
  public void AChargeReportingBatteryIsTurnedIntoEnergy() {
    var battery = PowerSupplyParser.Parse("BAT0", Charge(), onExternalPower: false);

    // 7 566 000 µAh × 11 878 000 µV / 1e6 = 89 869 348 µWh, near enough 89.9 Wh.
    Assert.That(battery.EnergyNowMicrowattHours.Value, Is.EqualTo(89_869_348ul).Within(1000));
    Assert.That(battery.EnergyDesignMicrowattHours.Value, Is.GreaterThan(battery.EnergyFullMicrowattHours.Value));
    Assert.That(battery.PowerMicrowatts.Value, Is.EqualTo(17_817_000ul).Within(1000), "1.5 A at 11.878 V");
  }

  [Test]
  public void AnEnergyReportingBatteryIsTakenAsItIs() {
    var battery = PowerSupplyParser.Parse("BAT0", Energy(), onExternalPower: true);

    Assert.That(battery.EnergyNowMicrowattHours.Value, Is.EqualTo(30_000_000ul), "not multiplied by anything");
    Assert.That(battery.PowerMicrowatts.Value, Is.EqualTo(10_000_000ul));
  }

  /// <summary>
  /// The kernel's own capacity where it publishes one: a driver that rounds or smooths knows
  /// something about its hardware that arithmetic here does not.
  /// </summary>
  [Test]
  public void TheDriversOwnPercentageIsPreferred() =>
    Assert.That(PowerSupplyParser.Parse("BAT0", Charge(), false).ChargePercent.Value, Is.EqualTo(78ul));

  [Test]
  public void WithoutAPercentageItIsComputedFromTheEnergy() {
    var attributes = Energy();
    var battery = PowerSupplyParser.Parse("BAT0", attributes, true);

    Assert.That(battery.ChargePercent.Value, Is.EqualTo(60ul), "30 of 50 Wh");
  }

  /// <summary>
  /// Worn out is a different question from empty. This pack charges to 90.8 % of what it left the
  /// factory with, which rounds to 91 and not to 90 — the truncated version of this number is what
  /// somebody decides whether to replace a battery on.
  /// </summary>
  [Test]
  public void HealthIsTheFullChargeAgainstTheDesignCharge() {
    var battery = PowerSupplyParser.Parse("BAT0", Charge(), false);

    Assert.That(battery.HealthPercent.Value, Is.EqualTo(91ul));
  }

  [Test]
  public void TimeRemainingIsEnergyOverDraw() {
    var battery = PowerSupplyParser.Parse("BAT0", Charge(), false);

    // 89.9 Wh at 17.8 W is a little over five hours.
    Assert.That(battery.HoursRemaining!.Value, Is.EqualTo(5.04).Within(0.05));
  }

  [Test]
  public void TimeToFullIsWhatIsMissingOverTheDraw() {
    var battery = PowerSupplyParser.Parse("BAT0", Energy(), true);

    // 20 Wh still to put in, at 10 W.
    Assert.That(battery.HoursRemaining!.Value, Is.EqualTo(2).Within(0.01));
  }

  /// <summary>
  /// A battery that is full and idle has no time remaining, and saying "0:00" about it would be
  /// alarming and wrong.
  /// </summary>
  [Test]
  public void AFullBatteryHasNoTimeRemaining() {
    var attributes = Charge();
    attributes["status"] = "Full";
    attributes["current_now"] = "0";

    Assert.That(PowerSupplyParser.Parse("BAT0", attributes, true).HoursRemaining, Is.Null);
  }

  /// <summary>
  /// The recurring trap. A driver that does not publish a figure and a battery genuinely at nought
  /// must not look alike (PRD §72.3).
  /// </summary>
  [Test]
  public void AnAttributeThatIsNotThereIsNotNought() {
    var battery = PowerSupplyParser.Parse("BAT0", new Dictionary<string, string> {
      ["type"] = "Battery",
      ["status"] = "Unknown",
    }, false);

    Assert.That(battery.ChargePercent.HasValue, Is.False);
    Assert.That(battery.EnergyNowMicrowattHours.HasValue, Is.False);
    Assert.That(battery.PowerMicrowatts.HasValue, Is.False);
    Assert.That(battery.HealthPercent.HasValue, Is.False);
    Assert.That(battery.ChargePercent.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>
  /// Charge without a voltage cannot become energy, and reporting µAh as though it were µWh would
  /// be wrong by about a factor of twelve — which looks plausible, which is what makes it dangerous.
  /// </summary>
  [Test]
  public void ChargeWithoutAVoltageIsNotPretendedToBeEnergy() {
    var attributes = Charge();
    attributes.Remove("voltage_now");

    var battery = PowerSupplyParser.Parse("BAT0", attributes, false);

    Assert.That(battery.EnergyNowMicrowattHours.HasValue, Is.False);
  }

  /// <summary>
  /// A driver reporting something outside the documented five is shown as whatever it said, rather
  /// than flattened into "unknown" — which would claim the machine does not know when we do not.
  /// </summary>
  [Test]
  public void AnUnrecognisedStateKeepsItsOwnWords() {
    var attributes = Charge();
    attributes["status"] = "Quick-charging";

    var battery = PowerSupplyParser.Parse("BAT0", attributes, true);

    Assert.That(battery.State, Is.EqualTo(ChargeState.Unknown));
    Assert.That(battery.StateText, Is.EqualTo("Quick-charging"));
  }

  /// <summary>Discharging drivers report a negative current; the magnitude is the reading.</summary>
  [Test]
  public void ANegativeCurrentIsReadAsADraw() {
    var attributes = Charge();
    attributes["current_now"] = "-1500000";

    Assert.That(PowerSupplyParser.Parse("BAT0", attributes, false).PowerMicrowatts.Value,
      Is.EqualTo(17_817_000ul).Within(1000));
  }

  #endregion

  #region sensor chips

  private static Dictionary<string, string> CoreTemp() => new(StringComparer.Ordinal) {
    ["name"] = "coretemp",
    ["temp1_input"] = "61000",
    ["temp1_label"] = "Package id 0",
    ["temp1_crit"] = "100000",
    ["temp2_input"] = "62000",
    ["temp2_label"] = "Core 0",
    // A channel the chip publishes and has nothing wired to.
    ["temp9_label"] = "Core 7",
    ["in0_input"] = "11922",
    ["fan1_input"] = "2708",
  };

  [Test]
  public void EveryChannelWithAReadingIsListed() {
    var group = HwmonParser.Parse("coretemp", CoreTemp());

    Assert.That(group.Readings, Has.Count.EqualTo(4), "two temperatures, a voltage and a fan");
    Assert.That(group.Name, Is.EqualTo("coretemp"));
  }

  /// <summary>A channel with a label and no reading is not a reading. It is a label.</summary>
  [Test]
  public void AChannelWithNothingWiredToItIsLeftOut() {
    foreach (var reading in HwmonParser.Parse("coretemp", CoreTemp()).Readings)
      Assert.That(reading.Label, Is.Not.EqualTo("Core 7"));
  }

  [Test]
  public void TheChipsOwnLabelIsKept() {
    var readings = HwmonParser.Parse("coretemp", CoreTemp()).Readings;

    var labels = new List<string>();
    foreach (var reading in readings)
      labels.Add(reading.Label);

    Assert.That(labels, Does.Contain("Package id 0"));
    Assert.That(labels, Does.Contain("Core 0"));
  }

  /// <summary>
  /// An unlabelled channel is named after its chip, because "temp1" on its own is not an answer to
  /// "what is at seventy degrees" on a machine with eight sensor chips.
  /// </summary>
  [Test]
  public void AnUnlabelledChannelIsNamedAfterItsChip() {
    var group = HwmonParser.Parse("dell_smm", new Dictionary<string, string> {
      ["name"] = "dell_smm",
      ["temp1_input"] = "60000",
    });

    Assert.That(group.Readings[0].Label, Is.EqualTo("dell_smm temp1"));
  }

  [Test]
  public void EachChannelKnowsWhatItMeasures() {
    var kinds = new Dictionary<string, SensorKind>(StringComparer.Ordinal);
    foreach (var reading in HwmonParser.Parse("coretemp", CoreTemp()).Readings)
      kinds[reading.Label] = reading.Kind;

    Assert.That(kinds["Core 0"], Is.EqualTo(SensorKind.Temperature));
    Assert.That(kinds["coretemp fan1"], Is.EqualTo(SensorKind.Fan));
    Assert.That(kinds["coretemp in0"], Is.EqualTo(SensorKind.Voltage), "in is voltage, not input");
  }

  /// <summary>
  /// A dictionary hands its keys back in whatever order it likes. A list of temperatures that
  /// reshuffles every second is unreadable, whatever the numbers in it say.
  /// </summary>
  [Test]
  public void ReadingsComeBackInAStableOrder() {
    var first = HwmonParser.Parse("coretemp", CoreTemp()).Readings;
    var second = HwmonParser.Parse("coretemp", CoreTemp()).Readings;

    for (var i = 0; i < first.Count; ++i)
      Assert.That(second[i].Label, Is.EqualTo(first[i].Label));
  }

  [Test]
  public void AChipThatMeasuresNothingHasNoReadings() =>
    Assert.That(HwmonParser.Parse("empty", new Dictionary<string, string> { ["name"] = "empty" }).Readings, Is.Empty);

  #endregion

  #region what a machine without either does

  /// <summary>
  /// A desktop has no battery, and that is an empty list rather than a battery at nought per cent —
  /// which is what a page that assumed one would draw.
  /// </summary>
  [Test]
  public void NoSourceMeansNoneRatherThanAFailure() {
    SensorSources.Batteries = null;
    SensorSources.Sensors = null;

    Assert.That(SensorSources.Ask(SensorSources.Batteries), Is.Empty);
    Assert.That(SensorSources.Ask(SensorSources.Sensors), Is.Empty);
  }

  /// <summary>
  /// A chip can disappear between two reads — a power supply unplugged, a driver unloaded — and a
  /// performance page must not close because a fan controller went away.
  /// </summary>
  [Test]
  public void ASourceThatThrowsIsAMachineWithNone() =>
    Assert.That(
      SensorSources.Ask<BatteryInfo>(() => throw new IOException("the driver went away")),
      Is.Empty
    );

  #endregion

}
