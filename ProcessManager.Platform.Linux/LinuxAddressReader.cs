using System.Globalization;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// The addresses each interface carries, and the way off the machine (PRD §49).
/// </summary>
/// <remarks>
/// Read once with the rest of an interface's description. Addresses do change while a program runs —
/// a lease expires, a VPN comes up — and this is the same contract the link speed and the MAC
/// address beside them have: the page describes the adapter as it was found, and a new interface
/// gets a fresh reading.
/// </remarks>
internal static class LinuxAddressReader {

  /// <summary>Every address of one interface, as <c>address/prefix</c>, IPv4 first.</summary>
  /// <remarks>
  /// With the prefix length, because an address without one does not say which network it is on —
  /// which is the whole question somebody reads an adapter's addresses to answer. Empty rather than
  /// null when the machine could be asked and the interface has none: an interface that is up with no
  /// address is a real and interesting state.
  /// </remarks>
  public static IReadOnlyList<string>? Read(string interfaceName) {
    ArgumentNullException.ThrowIfNull(interfaceName);

    var v4 = new List<string>();
    var v6 = new List<string>();
    var asked = Native.ForEachInterfaceAddress((name, family, address, netmask) => {
      if (!string.Equals(name, interfaceName, StringComparison.Ordinal))
        return;

      if (family == Native.AF_INET) {
        // sockaddr_in: family, port, then the four bytes of the address.
        var text = IpAddressText.V4(address.Slice(4, 4));
        v4.Add(Prefixed(text, netmask.Length >= 8 ? netmask.Slice(4, 4) : default));
        return;
      }

      // sockaddr_in6: family, port, flow label, then sixteen bytes.
      var six = IpAddressText.V6(address.Slice(8, 16));
      v6.Add(Prefixed(six, netmask.Length >= 24 ? netmask.Slice(8, 16) : default));
    });

    if (!asked)
      return null;

    // IPv4 first because that is the one most readers are looking for, and link-local IPv6 last
    // because every interface has one and it identifies nothing.
    v4.AddRange(v6);
    return v4;
  }

  private static string Prefixed(string address, ReadOnlySpan<byte> netmask) {
    if (address.Length == 0)
      return address;

    var prefix = PrefixLength(netmask);
    return prefix < 0
      ? address
      : address + "/" + prefix.ToString(CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// How many leading bits a netmask sets, or -1 where there is no netmask to count.
  /// </summary>
  /// <remarks>
  /// Counted rather than looked up, and it stops at the first zero bit: a mask with a hole in it —
  /// 255.0.255.0 — is not a prefix length at all, and reporting the total number of set bits would
  /// turn a misconfigured interface into a plausible one.
  /// </remarks>
  private static int PrefixLength(ReadOnlySpan<byte> netmask) {
    if (netmask.IsEmpty)
      return -1;

    var bits = 0;
    for (var i = 0; i < netmask.Length; ++i) {
      var octet = netmask[i];
      if (octet == 0xFF) {
        bits += 8;
        continue;
      }

      while ((octet & 0x80) != 0) {
        ++bits;
        octet = (byte)(octet << 1);
      }

      // Everything after the first non-full octet has to be zero for this to be a prefix.
      for (var j = i + 1; j < netmask.Length; ++j)
        if (netmask[j] != 0)
          return -1;

      break;
    }

    return bits;
  }

}
