using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a sensor chip measures, from the files <c>hwmon</c> publishes (PRD §45.3).
/// </summary>
/// <remarks>
/// <para>
/// The naming is a convention rather than a schema: a chip exposes <c>temp1_input</c>,
/// <c>temp1_label</c>, <c>temp1_crit</c>, <c>fan2_input</c> and so on, with no list of what it has
/// and gaps in the numbering where a channel is not wired up. So the attribute names are read and
/// grouped rather than probed one index at a time, which is also the only way to avoid asking for
/// files that are not there.
/// </para>
/// <para>
/// No platform attribute, so a recorded chip is parsed on every CI leg — including the machines
/// that have no sensors and could never otherwise run this.
/// </para>
/// </remarks>
public static class HwmonParser {

  /// <summary>
  /// Reads one chip.
  /// </summary>
  /// <param name="chipName">The contents of the chip's <c>name</c> file — <c>coretemp</c>, <c>nvme</c>.</param>
  /// <param name="attributes">Attribute name to contents, trimmed, for everything the chip publishes.</param>
  public static SensorGroup Parse(string chipName, IReadOnlyDictionary<string, string> attributes) {
    ArgumentNullException.ThrowIfNull(chipName);
    ArgumentNullException.ThrowIfNull(attributes);

    var readings = new List<SensorReading>();
    foreach (var (key, _) in attributes) {
      if (!key.EndsWith("_input", StringComparison.Ordinal))
        continue;

      var channel = key[..^"_input".Length];
      var kind = KindOf(channel);
      if (kind == SensorKind.Unknown)
        continue;

      var value = Number(attributes, key);
      if (!value.HasValue)
        // A channel that is present and unreadable is worth nothing to a reader; a channel that is
        // present and reads as a number is the whole point. Skipped rather than listed as a row of
        // placeholder, because a chip can publish a dozen channels it has nothing wired to.
        continue;

      readings.Add(new(
        Label(attributes, channel, chipName),
        kind,
        value,
        Number(attributes, channel + "_max"),
        Number(attributes, channel + "_crit")
      ));
    }

    // By label, so a chip's channels do not reorder between reads: a dictionary hands its keys back
    // in whatever order it likes, and a list of temperatures that shuffles every second is unreadable.
    readings.Sort(static (left, right) => string.CompareOrdinal(left.Label, right.Label));
    return new(chipName, readings);
  }

  /// <summary>
  /// What a channel measures, from the prefix its files use.
  /// </summary>
  /// <remarks>
  /// <c>in</c> is voltage rather than anything to do with input, which is a trap worth naming: the
  /// file <c>in1_input</c> is the first voltage channel, not the input of channel <c>in1</c>.
  /// </remarks>
  private static SensorKind KindOf(ReadOnlySpan<char> channel) {
    if (channel.StartsWith("temp", StringComparison.Ordinal))
      return SensorKind.Temperature;
    if (channel.StartsWith("fan", StringComparison.Ordinal))
      return SensorKind.Fan;
    if (channel.StartsWith("in", StringComparison.Ordinal))
      return SensorKind.Voltage;
    if (channel.StartsWith("curr", StringComparison.Ordinal))
      return SensorKind.Current;
    if (channel.StartsWith("power", StringComparison.Ordinal))
      return SensorKind.Power;

    return SensorKind.Unknown;
  }

  /// <summary>
  /// What the chip calls this channel, or the channel's own name where it says nothing.
  /// </summary>
  /// <remarks>
  /// Prefixed with the chip for an unlabelled channel, because "temp1" on its own is not an answer
  /// to "what is at 70 degrees" on a machine with eight sensor chips.
  /// </remarks>
  private static string Label(
    IReadOnlyDictionary<string, string> attributes,
    string channel,
    string chipName
  ) => attributes.TryGetValue(channel + "_label", out var label) && label.Length > 0
    ? label
    : $"{chipName} {channel}";

  private static Counter Number(IReadOnlyDictionary<string, string> attributes, string key) {
    if (!attributes.TryGetValue(key, out var text) || text.Length == 0)
      return Counter.NotSupported;

    return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
      ? Counter.Of((ulong)value)
      : Counter.Unknown(UnknownReason.CounterInvalid);
  }

}
