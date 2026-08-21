namespace Hawkynt.ProcessManager.Model;

/// <summary>What a battery is doing, as the machine reports it.</summary>
/// <remarks>
/// Deliberately not an enum of every string Linux can put in <c>status</c>: the kernel documents
/// five, drivers invent more, and one nobody anticipated must reach the screen as itself rather than
/// as <see cref="Unknown"/> — which would say the machine does not know when in fact we do not.
/// </remarks>
public enum ChargeState : byte {
  Unknown = 0,
  Charging,
  Discharging,
  NotCharging,
  Full,
}

/// <summary>
/// A battery, and what it is doing (PRD §45.3).
/// </summary>
/// <remarks>
/// <para>
/// Two families of machine report this two different ways and neither is wrong. Some batteries
/// publish charge in µAh with a separate voltage, others publish energy in µWh directly; a laptop
/// with the first kind and a reader that only knows the second reports nothing at all. Both are read
/// and both end up as the same figures here, because "how full is it" is one question.
/// </para>
/// <para>
/// Every counter says why it is missing rather than reading nought. A battery at rest genuinely
/// draws no current, and that zero must not look like the zero of a driver that does not publish
/// the figure (PRD §72.3).
/// </para>
/// </remarks>
public sealed record BatteryInfo(
  string Name,
  ChargeState State,
  string? StateText,
  bool OnExternalPower,
  Counter ChargePercent,
  Counter EnergyNowMicrowattHours,
  Counter EnergyFullMicrowattHours,
  Counter EnergyDesignMicrowattHours,
  Counter PowerMicrowatts,
  Counter VoltageMicrovolts,
  Counter CycleCount,
  string? Technology,
  string? Manufacturer,
  string? Model,
  string? Serial
) {

  /// <summary>
  /// How much of its original capacity the battery still holds, as a percentage.
  /// </summary>
  /// <remarks>
  /// The number that says whether a battery is worn out, which is not the same question as how full
  /// it is now. A pack that charges to 90 % of what it left the factory with is healthy; one that
  /// charges to 60 % is the reason a laptop stopped lasting the afternoon.
  /// </remarks>
  public Counter HealthPercent
    => this.EnergyFullMicrowattHours.HasValue
      && this.EnergyDesignMicrowattHours is { HasValue: true, Value: > 0 }
        // Rounded, not truncated: a pack at 90.7 % of its design capacity is not a pack at 90 %,
        // and this is the number somebody decides whether to replace a battery on.
        ? Counter.Of(
            (this.EnergyFullMicrowattHours.Value * 100 + this.EnergyDesignMicrowattHours.Value / 2)
            / this.EnergyDesignMicrowattHours.Value
          )
        : Counter.NotSupported;

  /// <summary>
  /// Hours until the battery is empty, or until it is full when charging.
  /// </summary>
  /// <remarks>
  /// Computed rather than read, because most drivers do not publish it — and stated as a figure the
  /// caller may format, not as a sentence, so that a page and a terminal can disagree about wording
  /// without disagreeing about the number. Null while nothing is moving: a battery drawing no
  /// current has no meaningful time remaining, and dividing by it would produce infinity.
  /// </remarks>
  public double? HoursRemaining {
    get {
      if (!this.PowerMicrowatts.HasValue || this.PowerMicrowatts.Value == 0)
        return null;

      var watts = (double)this.PowerMicrowatts.Value;
      return this.State switch {
        ChargeState.Discharging when this.EnergyNowMicrowattHours.HasValue
          => this.EnergyNowMicrowattHours.Value / watts,
        ChargeState.Charging when this.EnergyFullMicrowattHours.HasValue && this.EnergyNowMicrowattHours.HasValue
          => (this.EnergyFullMicrowattHours.Value - this.EnergyNowMicrowattHours.Value) / watts,
        _ => null,
      };
    }
  }

}

/// <summary>One thing a sensor chip measures.</summary>
/// <param name="Label">
/// What the chip calls it — "Package id 0", "Core 3", "Composite". Kept verbatim: renaming a
/// sensor is how a reader ends up comparing two different things believing they are the same.
/// </param>
public readonly record struct SensorReading(
  string Label,
  SensorKind Kind,
  Counter Value,
  Counter High,
  Counter Critical
);

/// <summary>What a reading measures, which decides its unit and how it is drawn.</summary>
public enum SensorKind : byte {
  Unknown = 0,

  /// <summary>Millidegrees Celsius, as hwmon publishes them.</summary>
  Temperature,

  /// <summary>Revolutions per minute.</summary>
  Fan,

  /// <summary>
  /// Millivolts — <em>not</em> microvolts.
  /// </summary>
  /// <remarks>
  /// hwmon and <c>power_supply</c> disagree about this and both are published by the same laptop:
  /// the battery's own <c>voltage_now</c> reads 11922000 while the sensor chip beside it reads
  /// 11922 for the same rail. Reading the second in the first's units shows a battery at a
  /// hundredth of a volt, which is what this said before somebody looked at the number.
  /// </remarks>
  Voltage,

  /// <summary>Milliamps, for the same reason as <see cref="Voltage"/>.</summary>
  Current,

  /// <summary>Microwatts. This one really is micro — the hwmon interface is not consistent.</summary>
  Power,
}

/// <summary>
/// One sensor chip and everything it measures (PRD §45.3).
/// </summary>
/// <remarks>
/// Grouped by chip rather than flattened into one list of temperatures, because "70 °C" means
/// nothing without knowing whether it came from the processor package, an SSD or the wireless card —
/// and a machine has several of each.
/// </remarks>
public sealed record SensorGroup(string Name, IReadOnlyList<SensorReading> Readings);
