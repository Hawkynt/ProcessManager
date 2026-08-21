using System.Globalization;
using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/net/route</c> and <c>/proc/net/ipv6_route</c>: which way off this machine (PRD §49).
/// </summary>
/// <remarks>
/// Only the default route is wanted, and only per interface: an adapter's gateway is the thing a
/// person checks when the network is not working, and the rest of the table answers a question a
/// performance page does not ask.
/// <para>
/// The addresses in both files are hexadecimal, and the two files disagree about the byte order:
/// the IPv4 table writes each word in the host's own order — little-endian on everything this runs
/// on — and the IPv6 table writes bytes in network order. Reading either with the other's rule
/// produces a plausible address that belongs to somebody else entirely.
/// </para>
/// <para>
/// No platform attribute and no file access, so both are exercised on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class RouteTableParser {

  /// <summary>
  /// The IPv4 default gateway of one interface, or null where it has none.
  /// </summary>
  /// <remarks>
  /// The default route is the one whose destination and mask are both zero. An interface can carry
  /// several, one per metric, and the first is taken: the kernel writes them in the order it will
  /// try them, so the first is the one packets will actually use.
  /// </remarks>
  public static string? DefaultGateway(ReadOnlySpan<byte> content, string interfaceName) {
    ArgumentNullException.ThrowIfNull(interfaceName);

    var scanner = new AsciiScanner(content);
    var line = 0;
    while (!scanner.IsEmpty) {
      var text = scanner.NextLine();
      // "Iface Destination Gateway Flags RefCnt Use Metric Mask …", then one route per line.
      if (++line == 1 || text.IsEmpty)
        continue;

      var fields = new AsciiScanner(text);
      var name = fields.NextField();
      if (!Matches(name, interfaceName))
        continue;

      var destination = AsciiScanner.ParseHex(fields.NextField());
      var gateway = AsciiScanner.ParseHex(fields.NextField());
      if (destination != 0 || gateway == 0)
        continue;

      return Dotted((uint)gateway);
    }

    return null;
  }

  /// <summary>
  /// The IPv6 default gateway of one interface, or null where it has none.
  /// </summary>
  /// <remarks>
  /// The columns are destination, prefix length, source, source prefix, next hop, metric, refcount,
  /// use, flags and the interface name last. A default route has a prefix length of nought, which is
  /// the second column and not a mask.
  /// </remarks>
  public static string? DefaultGatewayV6(ReadOnlySpan<byte> content, string interfaceName) {
    ArgumentNullException.ThrowIfNull(interfaceName);

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var text = scanner.NextLine();
      if (text.IsEmpty)
        continue;

      var fields = new AsciiScanner(text);
      fields.NextField();
      var prefix = AsciiScanner.ParseHex(fields.NextField());
      fields.Skip(2);
      var nextHop = fields.NextField();
      fields.Skip(4);
      var name = fields.NextField();
      if (prefix != 0 || !Matches(name, interfaceName))
        continue;

      var address = Colonned(nextHop);
      if (address is { Length: > 0 } and not "::")
        return address;
    }

    return null;
  }

  private static bool Matches(ReadOnlySpan<byte> field, string name) {
    if (field.Length != name.Length)
      return false;

    for (var i = 0; i < field.Length; ++i)
      if (field[i] != (byte)name[i])
        return false;

    return true;
  }

  /// <summary>
  /// A word from the IPv4 table as an address.
  /// </summary>
  /// <remarks>
  /// Host byte order, so <c>012CA8C0</c> is 192.168.44.1 and not 1.44.168.192. The file has been
  /// written this way since it existed and is the classic way to read a routing table backwards.
  /// </remarks>
  private static string Dotted(uint word) => string.Format(
    CultureInfo.InvariantCulture,
    "{0}.{1}.{2}.{3}",
    word & 0xFF,
    (word >> 8) & 0xFF,
    (word >> 16) & 0xFF,
    (word >> 24) & 0xFF
  );

  /// <summary>Thirty-two hex digits, in network order, as the eight groups people write.</summary>
  private static string? Colonned(ReadOnlySpan<byte> hex) {
    if (hex.Length != 32)
      return null;

    Span<byte> bytes = stackalloc byte[16];
    for (var i = 0; i < 16; ++i) {
      var high = Nibble(hex[i * 2]);
      var low = Nibble(hex[(i * 2) + 1]);
      if (high < 0 || low < 0)
        return null;

      bytes[i] = (byte)((high << 4) | low);
    }

    return IpAddressText.V6(bytes);
  }

  private static int Nibble(byte digit) => digit switch {
    >= (byte)'0' and <= (byte)'9' => digit - (byte)'0',
    >= (byte)'a' and <= (byte)'f' => digit - (byte)'a' + 10,
    >= (byte)'A' and <= (byte)'F' => digit - (byte)'A' + 10,
    _ => -1,
  };

}

/// <summary>
/// Sixteen bytes as the address people write (PRD §49).
/// </summary>
/// <remarks>
/// Its own type because two different readers produce those bytes — the routing table out of hex,
/// and the interface list out of a <c>sockaddr</c> — and both have to spell them the same way, or
/// the same address appears twice on one page in two notations.
/// </remarks>
public static class IpAddressText {

  /// <summary>
  /// The canonical short form: groups of four hex digits, leading zeros dropped, and the longest run
  /// of zero groups replaced by <c>::</c>.
  /// </summary>
  /// <remarks>
  /// The run has to be at least two groups long, because <c>::</c> standing for a single zero group
  /// is not shorter and RFC 5952 forbids it — and an address written both ways does not compare
  /// equal to itself by eye.
  /// </remarks>
  public static string V6(ReadOnlySpan<byte> bytes) {
    if (bytes.Length != 16)
      return string.Empty;

    Span<int> groups = stackalloc int[8];
    for (var i = 0; i < 8; ++i)
      groups[i] = (bytes[i * 2] << 8) | bytes[(i * 2) + 1];

    var bestStart = -1;
    var bestLength = 0;
    for (var i = 0; i < 8;) {
      if (groups[i] != 0) {
        ++i;
        continue;
      }

      var start = i;
      while (i < 8 && groups[i] == 0)
        ++i;

      if (i - start > bestLength) {
        bestStart = start;
        bestLength = i - start;
      }
    }

    if (bestLength < 2) {
      bestStart = -1;
      bestLength = 0;
    }

    var text = new StringBuilder(40);
    var afterRun = false;
    for (var i = 0; i < 8; ++i) {
      if (i == bestStart) {
        text.Append("::");
        i += bestLength - 1;
        afterRun = true;
        continue;
      }

      // No separator straight after the run — the run's own pair of colons is the separator, and a
      // third would spell an address nothing parses.
      if (text.Length > 0 && !afterRun)
        text.Append(':');

      afterRun = false;
      text.Append(groups[i].ToString("x", CultureInfo.InvariantCulture));
    }

    return text.Length == 0 ? "::" : text.ToString();
  }

  /// <summary>Four bytes in network order as dotted decimal.</summary>
  public static string V4(ReadOnlySpan<byte> bytes) => bytes.Length != 4
    ? string.Empty
    : string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", bytes[0], bytes[1], bytes[2], bytes[3]);

}
