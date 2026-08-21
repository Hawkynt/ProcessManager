namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/net/wireless</c>: how well an adapter can hear the thing it is talking to (PRD §49).
/// </summary>
/// <remarks>
/// <para>
/// Two header lines and then one line per wireless interface, whose name ends in a colon.
/// The three figures after the status are link quality, signal level and noise, each written with a
/// trailing full stop that is part of the format rather than the number.
/// </para>
/// <para>
/// Level is in dBm on every driver anybody still runs and is negative — a strong signal is about
/// −40 and an unusable one about −85. A driver that reports it as a relative 0–255 figure instead is
/// old enough that its readings would be a guess, so anything positive is refused rather than shown
/// as an impossibly strong signal.
/// </para>
/// <para>
/// Quality is a driver's own opinion out of a maximum only it knows; it is carried because it is the
/// figure the driver actually optimises against, and never converted into a percentage of a
/// denominator this file does not contain (PRD §5.3).
/// </para>
/// <para>
/// No platform attribute and no file access, so it runs on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class WirelessStatusParser {

  /// <param name="SignalDbm">Received signal, in dBm. Null where the driver published none.</param>
  /// <param name="Quality">The driver's own link-quality figure, on its own scale.</param>
  public readonly record struct WirelessStatus(int? SignalDbm, int? Quality, int? NoiseDbm);

  /// <summary>What the file says about one interface, or null when it says nothing about it.</summary>
  public static WirelessStatus? Find(ReadOnlySpan<byte> content, string interfaceName) {
    ArgumentNullException.ThrowIfNull(interfaceName);

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      var colon = line.IndexOf((byte)':');
      if (colon <= 0)
        continue;

      var name = line[..colon].Trim((byte)' ');
      if (!Matches(name, interfaceName))
        continue;

      var fields = new AsciiScanner(line[(colon + 1)..]);
      fields.NextField();                                // status, in hex and of interest to nobody
      var quality = Number(fields.NextField());
      var signal = Number(fields.NextField());
      var noise = Number(fields.NextField());

      return new(
        // A positive "dBm" is a driver using the old relative scale, which is a different unit
        // wearing this one's name.
        signal is < 0 ? signal : null,
        quality,
        noise is < 0 ? noise : null
      );
    }

    return null;
  }

  /// <summary>
  /// One of the three figures, which are written as "−61." with the full stop attached.
  /// </summary>
  /// <remarks>
  /// The stop is a fixed-point marker with nothing after it and not a decimal point with a missing
  /// fraction. Parsing it as a number and stopping at the stop is right; treating it as a separator
  /// and reading the next field would take the following column.
  /// </remarks>
  private static int? Number(ReadOnlySpan<byte> field) {
    if (field.IsEmpty)
      return null;

    var negative = field[0] == (byte)'-';
    var digits = negative ? field[1..] : field;
    if (digits.IsEmpty || digits[0] is < (byte)'0' or > (byte)'9')
      return null;

    var value = (int)AsciiScanner.ParseUInt64(digits);
    return negative ? -value : value;
  }

  private static bool Matches(ReadOnlySpan<byte> field, string name) {
    if (field.Length != name.Length)
      return false;

    for (var i = 0; i < field.Length; ++i)
      if (field[i] != (byte)name[i])
        return false;

    return true;
  }

}
