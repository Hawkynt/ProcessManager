using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// A battery, from the attributes <c>/sys/class/power_supply</c> publishes (PRD §45.3).
/// </summary>
/// <remarks>
/// <para>
/// Takes the attributes rather than a directory, with no platform attribute on it, so the reading of
/// a recorded laptop is exercised on every CI leg — including the machines that have no battery at
/// all and could otherwise never run this code (PRD §9.3).
/// </para>
/// <para>
/// The awkward part is that two families of hardware answer in different units. ACPI batteries that
/// report in µWh publish <c>energy_now</c>; the ones that report in µAh publish <c>charge_now</c>
/// and expect the reader to multiply by the voltage. A laptop with the second kind and a reader that
/// knows only the first shows an empty page while the battery is plainly working.
/// </para>
/// </remarks>
public static class PowerSupplyParser {

  /// <summary>
  /// Reads one supply.
  /// </summary>
  /// <param name="name">The directory's name — <c>BAT0</c>, <c>BAT1</c>.</param>
  /// <param name="attributes">Attribute name to its contents, trimmed.</param>
  /// <param name="onExternalPower">Whether a mains supply reports itself online.</param>
  public static BatteryInfo Parse(
    string name,
    IReadOnlyDictionary<string, string> attributes,
    bool onExternalPower
  ) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(attributes);

    var voltage = Number(attributes, "voltage_now");
    var (energyNow, energyFull, energyDesign, power) = Energy(attributes, voltage);

    return new(
      name,
      State(Text(attributes, "status")),
      Text(attributes, "status"),
      onExternalPower,
      Percent(attributes, energyNow, energyFull),
      energyNow,
      energyFull,
      energyDesign,
      power,
      voltage,
      Number(attributes, "cycle_count"),
      Text(attributes, "technology"),
      Text(attributes, "manufacturer"),
      Text(attributes, "model_name"),
      Text(attributes, "serial_number")
    );
  }

  /// <summary>
  /// The four energy figures, whichever units this battery reports in.
  /// </summary>
  /// <remarks>
  /// µAh × µV is 10^-12 Wh, so the product is divided by a million to land back in µWh. Done in
  /// <see cref="ulong"/> throughout: a full laptop battery is around 60 000 000 µWh and the
  /// intermediate product of charge and voltage is comfortably inside 64 bits, where a
  /// <see cref="double"/> would quietly round the last digits of a figure people compare between
  /// readings.
  /// </remarks>
  private static (Counter Now, Counter Full, Counter Design, Counter Power) Energy(
    IReadOnlyDictionary<string, string> attributes,
    Counter voltage
  ) {
    // The µWh kind, which needs no arithmetic at all.
    var now = Number(attributes, "energy_now");
    if (now.HasValue)
      return (
        now,
        Number(attributes, "energy_full"),
        Number(attributes, "energy_full_design"),
        Number(attributes, "power_now")
      );

    // The µAh kind. Without a voltage the charge cannot be turned into energy, and a charge figure
    // labelled as energy would be wrong by a factor of about twelve.
    var charge = Number(attributes, "charge_now");
    if (!charge.HasValue || !voltage.HasValue)
      return (
        Counter.NotSupported,
        Counter.NotSupported,
        Counter.NotSupported,
        Counter.NotSupported
      );

    var volts = voltage.Value;
    var current = Number(attributes, "current_now");
    return (
      Scale(charge, volts),
      Scale(Number(attributes, "charge_full"), volts),
      Scale(Number(attributes, "charge_full_design"), volts),
      Scale(current, volts)
    );
  }

  private static Counter Scale(Counter microAmpHours, ulong microVolts)
    => microAmpHours.HasValue
      ? Counter.Of(microAmpHours.Value * microVolts / 1_000_000)
      : microAmpHours;

  /// <summary>
  /// How full the battery is.
  /// </summary>
  /// <remarks>
  /// The kernel's own <c>capacity</c> where it publishes one, because a driver that rounds or
  /// smooths knows something about its hardware that arithmetic here does not. Computed from the
  /// two energy figures otherwise, and unknown when neither is available — never nought, which on
  /// this particular field reads as a battery about to die.
  /// </remarks>
  private static Counter Percent(
    IReadOnlyDictionary<string, string> attributes,
    Counter now,
    Counter full
  ) {
    var reported = Number(attributes, "capacity");
    if (reported.HasValue)
      return reported;

    return now.HasValue && full is { HasValue: true, Value: > 0 }
      ? Counter.Of(now.Value * 100 / full.Value)
      : Counter.NotSupported;
  }

  /// <summary>
  /// The documented states, and nothing invented for the ones that are not.
  /// </summary>
  /// <remarks>
  /// The text is carried alongside this, so a driver reporting something outside the list is shown
  /// as whatever it said rather than flattened to "unknown".
  /// </remarks>
  private static ChargeState State(string? status) => status switch {
    "Charging" => ChargeState.Charging,
    "Discharging" => ChargeState.Discharging,
    "Not charging" => ChargeState.NotCharging,
    "Full" => ChargeState.Full,
    _ => ChargeState.Unknown,
  };

  private static string? Text(IReadOnlyDictionary<string, string> attributes, string key)
    => attributes.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

  /// <summary>
  /// One numeric attribute.
  /// </summary>
  /// <remarks>
  /// An attribute that is not there is not supported by this driver; one that is there and does not
  /// parse is a counter that read as nonsense. Two different answers, and neither of them nought:
  /// <c>default(Counter)</c> would claim the battery holds no charge at all.
  /// </remarks>
  private static Counter Number(IReadOnlyDictionary<string, string> attributes, string key) {
    if (!attributes.TryGetValue(key, out var text) || text.Length == 0)
      return Counter.NotSupported;

    // Signed, because current_now is negative while discharging on some drivers and the magnitude
    // is what the reader wants either way — the direction is already in `status`.
    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
      return Counter.Of((ulong)Math.Abs(signed));

    return Counter.Unknown(UnknownReason.CounterInvalid);
  }

}
