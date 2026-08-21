namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Which interface a socket's local address lives on (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// The kernel does not record an interface against a socket — a connection belongs to a route, not
/// to a card — so this is the address answered the other way round: the interface that owns the
/// address the socket is bound to. <c>/proc/net/if_inet6</c> states that outright for IPv6, and for
/// IPv4 it is the on-link route that contains the address, because an address is on-link exactly on
/// the interface it is configured on.
/// </para>
/// <para>
/// It can therefore fail to answer, and says so rather than guessing: an address on a point-to-point
/// interface has a host route and no subnet, and two interfaces on the same subnet are genuinely
/// ambiguous. Both leave the interface unknown rather than naming a plausible one.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public sealed class NetworkInterfaceMap {

  /// <summary>The name of an address bound to every interface at once.</summary>
  public const string Wildcard = "*";

  /// <summary>
  /// Linux calls the loopback interface <c>lo</c> and puts the whole of 127/8 on it. It is spelled
  /// out here because the routing table does not carry it: loopback routes live in the kernel's
  /// <c>local</c> table, and <c>/proc/net/route</c> only shows <c>main</c>.
  /// </summary>
  private const string _Loopback = "lo";

  private readonly (uint Destination, uint Mask, string Interface)[] _routes;
  private readonly (byte[] Address, string Interface)[] _addresses;

  private NetworkInterfaceMap(
    (uint Destination, uint Mask, string Interface)[] routes,
    (byte[] Address, string Interface)[] addresses
  ) {
    this._routes = routes;
    this._addresses = addresses;
  }

  /// <summary>Knows nothing, and answers null to everything except the wildcard and loopback.</summary>
  public static readonly NetworkInterfaceMap Empty = new([], []);

  /// <summary>
  /// Reads <c>/proc/net/route</c> and <c>/proc/net/if_inet6</c>.
  /// </summary>
  /// <remarks>
  /// The two files disagree about how to write an address, which is the trap here. A route's
  /// destination is a 32-bit word in the recording machine's byte order — <c>002CA8C0</c> is
  /// 192.168.44.0 — while <c>if_inet6</c> prints the sixteen bytes straight through, so <c>::1</c>
  /// ends <c>…0001</c> there and <c>…0100</c> in <c>/proc/net/tcp6</c>. Everything is decoded to
  /// bytes before anything is compared, because comparing the text works on one file and silently
  /// fails on the other.
  /// </remarks>
  public static NetworkInterfaceMap Parse(ReadOnlySpan<char> routes, ReadOnlySpan<char> inet6) {
    var parsedRoutes = new List<(uint, uint, string)>();
    var scanner = new TextScanner(routes);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      var fields = new TextScanner(line);
      var name = fields.NextField();
      var destination = fields.NextField();
      var gateway = fields.NextField();
      fields.Skip(4);                                      // flags, refcount, use, metric
      var mask = fields.NextField();
      if (name.IsEmpty || mask.IsEmpty || !IsHex(destination) || !IsHex(mask) || !IsHex(gateway))
        continue;                                          // the header, or a line we do not know

      // Only on-link subnets. A route through a gateway says where to send traffic, not which card
      // an address sits on, and the default route would otherwise claim every address on the
      // machine; a zero mask is that default route by another name.
      var maskValue = TextScanner.ParseHex32(mask);
      if (maskValue == 0 || TextScanner.ParseHex32(gateway) != 0)
        continue;

      parsedRoutes.Add((TextScanner.ParseHex32(destination), maskValue, new(name)));
    }

    var parsedAddresses = new List<(byte[], string)>();
    scanner = new(inet6);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      var fields = new TextScanner(line);
      var address = fields.NextField();
      if (address.Length != 32 || !IsHex(address))
        continue;

      fields.Skip(4);                                      // index, prefix length, scope, flags
      var name = fields.NextField();
      if (name.IsEmpty)
        continue;

      var bytes = new byte[16];
      for (var i = 0; i < 16; ++i)
        bytes[i] = (byte)TextScanner.ParseHex32(address.Slice(i * 2, 2));

      parsedAddresses.Add((bytes, new(name)));
    }

    return new([.. parsedRoutes], [.. parsedAddresses]);
  }

  /// <summary>
  /// The interface an address is on, <see cref="Wildcard"/> when it is on all of them, or null when
  /// nothing here claims it.
  /// </summary>
  /// <param name="address">Four or sixteen bytes, in network order.</param>
  public string? Resolve(ReadOnlySpan<byte> address) => address.Length switch {
    4 => this.ResolveV4(address),
    16 => this.ResolveV6(address),
    _ => null,
  };

  private string? ResolveV4(ReadOnlySpan<byte> address) {
    if (IsAllZero(address))
      return Wildcard;
    if (address[0] == 127)
      return _Loopback;

    // /proc writes an address as one word in host byte order, so the route table's destinations are
    // composed the same way round rather than being compared byte by byte.
    var value = (uint)(address[0] | (address[1] << 8) | (address[2] << 16) | (address[3] << 24));
    string? best = null;
    uint bestMask = 0;
    foreach (var (destination, mask, name) in this._routes) {
      if ((value & mask) != destination)
        continue;

      // Longest prefix wins, exactly as the kernel would pick it: a machine with both a /16 and a
      // /24 covering the address is on the interface holding the /24.
      if (best is not null && System.Numerics.BitOperations.PopCount(mask) <= System.Numerics.BitOperations.PopCount(bestMask))
        continue;

      best = name;
      bestMask = mask;
    }

    return best;
  }

  private string? ResolveV6(ReadOnlySpan<byte> address) {
    if (IsAllZero(address))
      return Wildcard;

    // ::ffff:0:0/96 is an IPv4 address wearing an IPv6 socket, which is what a dual-stack listener
    // accepting a v4 connection looks like in /proc/net/tcp6. Answering it from the v6 table would
    // find nothing, because the address is not configured on any interface as an IPv6 address.
    if (IsV4Mapped(address))
      return this.ResolveV4(address[12..]);

    foreach (var (candidate, name) in this._addresses)
      if (address.SequenceEqual(candidate))
        return name;

    return null;
  }

  private static bool IsV4Mapped(ReadOnlySpan<byte> address) {
    for (var i = 0; i < 10; ++i)
      if (address[i] != 0)
        return false;

    return address[10] == 0xFF && address[11] == 0xFF;
  }

  private static bool IsAllZero(ReadOnlySpan<byte> address) {
    foreach (var b in address)
      if (b != 0)
        return false;

    return true;
  }

  private static bool IsHex(ReadOnlySpan<char> field) {
    if (field.IsEmpty)
      return false;

    foreach (var c in field)
      if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
        return false;

    return true;
  }

}
