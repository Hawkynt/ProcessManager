namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Turns a PCI identity into something a person recognises (PRD §50).
/// </summary>
/// <remarks>
/// <para>
/// Vendors only, and deliberately. The full PCI id database is several megabytes of device names
/// that go stale the month after a build, and a program that ships one is a program that tells
/// somebody their new card is unknown hardware. The vendor is the half that does not change, and
/// "NVIDIA 24b6" beside the driver name is enough to know which adapter is which — which is the
/// question this answers.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class PciNames {

  /// <summary>The vendors that make the graphics adapters this program is likely to meet.</summary>
  public static string? Vendor(ushort id) => id switch {
    0x1002 => "AMD",
    0x10DE => "NVIDIA",
    0x8086 => "Intel",
    0x1AF4 => "Virtio",
    0x1234 => "QEMU",
    0x15AD => "VMware",
    0x1414 => "Microsoft",
    0x5143 => "Qualcomm",
    0x13B5 => "ARM",
    0x1AE0 => "Google",
    _ => null,
  };

  /// <summary>
  /// A model name from a <c>PCI_ID=10DE:24B6</c> pair, or null when the vendor is not one this
  /// knows — in which case the raw pair is more use than a guess.
  /// </summary>
  public static string? Describe(ReadOnlySpan<char> pciId) {
    if (!TryParse(pciId, out var vendor, out var device))
      return null;

    var name = Vendor(vendor);
    return name is null ? null : $"{name} {device:x4}";
  }

  /// <summary>Parses <c>vvvv:dddd</c>, in either case, with or without a <c>0x</c> on either half.</summary>
  public static bool TryParse(ReadOnlySpan<char> pciId, out ushort vendor, out ushort device) {
    vendor = 0;
    device = 0;

    var colon = pciId.IndexOf(':');
    if (colon <= 0 || colon == pciId.Length - 1)
      return false;

    return TryHex(pciId[..colon], out vendor) && TryHex(pciId[(colon + 1)..], out device);
  }

  private static bool TryHex(ReadOnlySpan<char> text, out ushort value) {
    text = text.Trim();
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      text = text[2..];

    return ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
  }

}
