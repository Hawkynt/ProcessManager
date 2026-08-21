using System.Runtime.InteropServices;
using System.Text;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// What a wireless adapter is connected to (PRD §49).
/// </summary>
/// <remarks>
/// <para>
/// Through the wireless extensions — the <c>SIOCGIW*</c> ioctls — rather than through nl80211. They
/// are the compatibility interface and every driver anybody runs still answers them, where nl80211
/// is a generic-netlink protocol needing a family lookup, a dump request and an attribute walk for
/// two strings. A kernel built without <c>CONFIG_CFG80211_WEXT</c> refuses them, and that refusal is
/// reported as "unknown" rather than as an adapter connected to nothing (PRD §5.3).
/// </para>
/// <para>
/// <c>struct iwreq</c> is a sixteen-byte interface name followed by a union. Two members are used:
/// the ESSID's pointer, length and flags, and the frequency's mantissa and exponent. Both are read
/// out of a raw byte buffer at the offsets the ABI fixes rather than through a marshalled structure,
/// because the union's other twenty members would have to be declared to describe two.
/// </para>
/// </remarks>
internal static class LinuxWirelessReader {

  /// <summary>Where the union begins: <c>char ifr_name[IFNAMSIZ]</c>, which is sixteen bytes.</summary>
  private const int _NameLength = 16;

  /// <summary>The whole of <c>iwreq</c>: the name and the largest member of the union.</summary>
  private const int _RequestLength = _NameLength + 32;

  /// <summary>An SSID is at most 32 octets, and is not required to be text at all.</summary>
  private const int _MaximumSsid = 32;

  /// <summary>What the adapter is associated with, or null where it is not, or cannot say.</summary>
  public static unsafe string? Ssid(string interfaceName) {
    if (!OperatingSystem.IsLinux())
      return null;

    Span<byte> essid = stackalloc byte[_MaximumSsid + 1];
    essid.Clear();

    fixed (byte* buffer = essid) {
      Span<byte> request = stackalloc byte[_RequestLength];
      request.Clear();
      if (!WriteName(request, interfaceName))
        return null;

      // The union here is struct iw_point: a pointer to the caller's buffer, its length, and flags.
      MemoryMarshal.Write(request[_NameLength..], (nint)buffer);
      MemoryMarshal.Write(request[(_NameLength + sizeof(nint))..], (ushort)_MaximumSsid);

      if (!Native.TryInterfaceIoctl(Native.SIOCGIWESSID, request))
        return null;

      var length = MemoryMarshal.Read<ushort>(request[(_NameLength + sizeof(nint))..]);
      if (length == 0 || length > _MaximumSsid)
        // Associated with nothing is a real state and not a failure — a scanning adapter reports a
        // length of nought — but it is not a network called "".
        return null;

      // Hidden networks and the odd vendor put non-UTF-8 bytes in an SSID. Decoded permissively
      // rather than refused: a name with a replacement character in it still identifies the network.
      return Encoding.UTF8.GetString(essid[..length]);
    }
  }

  /// <summary>
  /// The frequency the adapter is tuned to, in megahertz, or null where it will not say.
  /// </summary>
  /// <remarks>
  /// <c>struct iw_freq</c> is a mantissa and a power of ten — 2437 × 10⁶ Hz — which is the kernel's
  /// way of expressing both a frequency and a channel number through one member. A small mantissa
  /// with an exponent of nought is a channel number rather than a frequency, and is refused: turning
  /// channel 6 into 6 MHz would put an adapter in the long-wave band.
  /// </remarks>
  public static int? FrequencyMegahertz(string interfaceName) {
    if (!OperatingSystem.IsLinux())
      return null;

    Span<byte> request = stackalloc byte[_RequestLength];
    request.Clear();
    if (!WriteName(request, interfaceName))
      return null;

    if (!Native.TryInterfaceIoctl(Native.SIOCGIWFREQ, request))
      return null;

    var mantissa = MemoryMarshal.Read<int>(request[_NameLength..]);
    var exponent = MemoryMarshal.Read<short>(request[(_NameLength + 4)..]);
    if (mantissa <= 0 || exponent is < 0 or > 12)
      return null;

    var hertz = (double)mantissa;
    for (var i = 0; i < exponent; ++i)
      hertz *= 10;

    var megahertz = hertz / 1_000_000d;
    // Everything anybody transmits on is between 900 MHz and 71 GHz; a number outside that is the
    // channel-number spelling of this member rather than a frequency.
    return megahertz is >= 900 and <= 71_000 ? (int)Math.Round(megahertz) : null;
  }

  private static bool WriteName(Span<byte> request, string interfaceName) {
    var length = Encoding.ASCII.GetByteCount(interfaceName);
    if (length == 0 || length >= _NameLength)
      return false;

    Encoding.ASCII.GetBytes(interfaceName, request);
    return true;
  }

}
