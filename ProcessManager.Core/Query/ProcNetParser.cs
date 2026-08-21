using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The kernel's socket tables: <c>/proc/net/{tcp,tcp6,udp,udp6}</c> and <c>/proc/net/unix</c>
/// (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// These tables are the whole of what Linux will say about a socket without opening a netlink
/// socket of our own. What is here is read; what is not — bytes transferred, round-trip time, the
/// cumulative retransmission count — is left unknown rather than filled with a zero, because a
/// connection that has moved nothing and a connection nobody measured look identical otherwise
/// (PRD §72.3).
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ProcNetParser {

  /// <summary>The two TCP states whose rows the kernel fills in differently from the rest.</summary>
  private const uint _TimeWait = 6;
  private const uint _Listen = 10;

  /// <summary>
  /// Reads one of the four internet tables into <paramref name="into"/>.
  /// </summary>
  /// <param name="interfaces">
  /// Used to name the interface each local address is on; pass <see cref="NetworkInterfaceMap.Empty"/>
  /// to leave that unknown.
  /// </param>
  /// <param name="onlyInodes">
  /// Keeps only these sockets, for the per-process query. Null keeps the machine's whole table.
  /// </param>
  public static void ParseInet(
    ReadOnlySpan<char> content,
    ConnectionProtocol protocol,
    NetworkInterfaceMap interfaces,
    IReadOnlySet<ulong>? onlyInodes,
    List<ConnectionRecord> into
  ) {
    ArgumentNullException.ThrowIfNull(interfaces);
    ArgumentNullException.ThrowIfNull(into);

    var isTcp = protocol is ConnectionProtocol.Tcp or ConnectionProtocol.Tcp6;

    // Outside the loop: a table with ten thousand rows would otherwise put ten thousand of these on
    // the stack, since a stackalloc inside a loop is only released when the method returns.
    Span<byte> localAddress = stackalloc byte[16];
    Span<byte> remoteAddress = stackalloc byte[16];
    var scanner = new TextScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      var fields = new TextScanner(line);
      fields.NextField();                                  // slot number, with its colon attached
      var local = fields.NextField();
      var remote = fields.NextField();

      // This is also how the header is recognised: "local_address" carries no colon, so the column
      // titles fall out here rather than by counting the first line — a table with no rows at all
      // still has that line, and a table read through a helper may not have it.
      if (local.IndexOf(':') <= 0)
        continue;

      var state = TextScanner.ParseHex32(fields.NextField());
      var queues = fields.NextField();                     // tx_queue:rx_queue, one field, not two
      fields.NextField();                                  // timer:jiffies until it fires
      var retransmits = TextScanner.ParseHex32(fields.NextField());
      var uid = (int)TextScanner.ParseUInt64(fields.NextField());
      fields.NextField();                                  // timeout
      var inode = TextScanner.ParseUInt64(fields.NextField());
      if (onlyInodes is not null && !onlyInodes.Contains(inode))
        continue;

      var localLength = SplitEndpoint(local, localAddress, out var localPort);
      var remoteLength = SplitEndpoint(remote, remoteAddress, out var remotePort);
      if (localLength == 0)
        continue;

      // A listening socket's two queue columns are not byte counts at all — the kernel puts the TCP
      // Fast Open queue length and the accept backlog there instead. A socket in TIME_WAIT has no
      // socket structure left to ask, so the kernel writes literal zeros for both queues and for the
      // retransmit count. Either read as bytes would be a number that is precise, plausible and
      // wrong: "nothing is queued" about a socket nobody measured.
      var listening = isTcp && state == _Listen;
      var noQueues = listening || (isTcp && state == _TimeWait);
      var colon = queues.IndexOf(':');
      into.Add(new(
        protocol,
        isTcp ? SocketKind.Stream : SocketKind.Datagram,
        Format(localAddress[..localLength]),
        localPort,
        remoteLength == 0 ? string.Empty : Format(remoteAddress[..remoteLength]),
        remotePort,
        isTcp ? TcpStateName(state) : UdpStateName(state),
        inode,
        0,                                                 // the owning pid; joined by the probe
        // An inode of zero means no open file refers to this socket, and the uid column then holds a
        // zero the kernel wrote rather than an owner: a closed-but-lingering connection would
        // otherwise be reported as root's, which on a multi-user machine is an accusation.
        inode == 0 ? -1 : uid,
        null,                                              // the owning user; resolved by the probe
        interfaces.Resolve(localAddress[..localLength]),
        noQueues || colon <= 0 ? Counter.NotSupported : Counter.Of(TextScanner.ParseHex64(queues[..colon])),
        noQueues || colon <= 0 ? Counter.NotSupported : Counter.Of(TextScanner.ParseHex64(queues[(colon + 1)..])),
        // UDP has the column and never fills it: the kernel prints a literal zero there for every
        // datagram socket, so reading it would report "no retransmissions" for a protocol that has
        // no such concept.
        isTcp && !noQueues ? Counter.Of((ulong)retransmits) : Counter.NotSupported
      ));
    }
  }

  /// <summary>
  /// Reads <c>/proc/net/unix</c>, which is a different table describing a different thing.
  /// </summary>
  /// <remarks>
  /// A Unix socket is named by a filesystem path rather than by an address and a port, has no peer
  /// address to report — the kernel keeps one but does not publish it here — and is charged to no
  /// uid. Squeezing it into the shape of a TCP row would mean inventing three fields; it is carried
  /// as what it is instead, with the rest left unknown (PRD §5.3).
  /// </remarks>
  public static void ParseUnix(
    ReadOnlySpan<char> content,
    IReadOnlySet<ulong>? onlyInodes,
    List<ConnectionRecord> into
  ) {
    ArgumentNullException.ThrowIfNull(into);

    var scanner = new TextScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      var fields = new TextScanner(line);
      var slot = fields.NextField();                       // the kernel pointer, with its colon
      if (slot.IsEmpty || slot[^1] != ':')
        continue;                                          // the header line

      fields.Skip(2);                                      // reference count, protocol
      var flags = TextScanner.ParseHex32(fields.NextField());
      var type = TextScanner.ParseHex32(fields.NextField());
      var state = TextScanner.ParseHex32(fields.NextField());
      var inode = TextScanner.ParseUInt64(fields.NextField());
      if (onlyInodes is not null && !onlyInodes.Contains(inode))
        continue;

      // Whatever is left, spaces and all: a socket path may contain them, and a socket bound to
      // "/tmp/my socket" must not be reported as one bound to "/tmp/my".
      var path = fields.Rest();
      into.Add(new(
        ConnectionProtocol.Unix,
        UnixSocketKind(type),
        path.IsEmpty ? string.Empty : new string(path),
        0,
        string.Empty,
        0,
        UnixStateName(state, flags),
        inode,
        0,                                                 // the owning pid; joined by the probe
        -1,                                                // this table carries no uid
        null,
        null,                                              // and no interface: it never leaves the machine
        Counter.NotSupported,
        Counter.NotSupported,
        Counter.NotSupported
      ));
    }
  }

  /// <summary>
  /// The inode a descriptor's target names, as in <c>socket:[12345]</c>.
  /// </summary>
  /// <remarks>
  /// This is the join between a process and its sockets, and the only one Linux offers: the network
  /// tables know nothing about processes, and <c>/proc/[pid]/fd</c> knows nothing about addresses.
  /// </remarks>
  public static bool TryParseSocketInode(ReadOnlySpan<char> descriptorTarget, out ulong inode) {
    inode = 0;
    var open = descriptorTarget.IndexOf('[');
    var close = descriptorTarget.IndexOf(']');
    if (open < 0 || close <= open + 1)
      return false;

    if (!descriptorTarget[..open].EndsWith("socket:", StringComparison.Ordinal))
      return false;

    var digits = descriptorTarget[(open + 1)..close];
    foreach (var c in digits)
      if (c is < '0' or > '9')
        return false;

    inode = TextScanner.ParseUInt64(digits);
    return true;
  }

  /// <summary>
  /// Splits <c>0100007F:0277</c> into address bytes and a port, and returns how many bytes it wrote.
  /// </summary>
  /// <remarks>
  /// The address is written as one 32-bit word per four bytes, in the byte order of the machine that
  /// produced the file, so the words are reversed on the way in. Every Linux this runs on is
  /// little-endian and a fixture recorded on one has to read the same on every CI leg, so the order
  /// is fixed here rather than taken from the machine running the parser — otherwise a recorded
  /// 127.0.0.1 would read as 1.0.0.127 on a big-endian runner.
  /// </remarks>
  private static int SplitEndpoint(ReadOnlySpan<char> field, Span<byte> address, out int port) {
    port = 0;
    var colon = field.LastIndexOf(':');
    if (colon <= 0)
      return 0;

    var hex = field[..colon];
    if (hex.Length != 8 && hex.Length != 32)
      return 0;

    port = (int)TextScanner.ParseHex32(field[(colon + 1)..]);
    for (var word = 0; word < hex.Length / 8; ++word)
      for (var b = 0; b < 4; ++b)
        address[word * 4 + b] = (byte)TextScanner.ParseHex32(hex.Slice(word * 8 + (3 - b) * 2, 2));

    return hex.Length / 2;
  }

  private static string Format(ReadOnlySpan<byte> address) => new System.Net.IPAddress(address).ToString();

  private static SocketKind UnixSocketKind(uint type) => type switch {
    1 => SocketKind.Stream,
    2 => SocketKind.Datagram,
    5 => SocketKind.SeqPacket,
    _ => SocketKind.Unknown,
  };

  /// <summary>
  /// A Unix socket's state, which needs both columns to read.
  /// </summary>
  /// <remarks>
  /// The state column of a listening socket says "unconnected", the same as a socket that was never
  /// bound to anything: the kernel puts the listening bit in the flags column instead, as
  /// <c>SO_ACCEPTCON</c>. Reading only the state would report every server on the machine as idle.
  /// </remarks>
  private static string UnixStateName(uint state, uint flags) => (flags & 0x10000) != 0
    ? "LISTEN"
    : state switch {
      1 => "UNCONNECTED",
      2 => "CONNECTING",
      3 => "CONNECTED",
      4 => "DISCONNECTING",
      _ => "FREE",
    };

  private static string TcpStateName(uint state) => state switch {
    1 => "ESTABLISHED",
    2 => "SYN_SENT",
    3 => "SYN_RECV",
    4 => "FIN_WAIT1",
    5 => "FIN_WAIT2",
    6 => "TIME_WAIT",
    7 => "CLOSE",
    8 => "CLOSE_WAIT",
    9 => "LAST_ACK",
    10 => "LISTEN",
    11 => "CLOSING",
    12 => "NEW_SYN_RECV",
    _ => "UNKNOWN",
  };

  /// <summary>
  /// A datagram socket's state, which reuses the TCP numbers for something else.
  /// </summary>
  /// <remarks>
  /// A UDP socket is either connected to a peer or it is not, and the kernel writes those two as
  /// TCP's <c>ESTABLISHED</c> and <c>CLOSE</c>. "CLOSE" is what an ordinary open, listening UDP
  /// socket looks like, so passing these through the TCP names would report every one of them as a
  /// dead connection.
  /// </remarks>
  private static string UdpStateName(uint state) => state switch {
    1 => "ESTABLISHED",
    7 => "UNCONNECTED",
    _ => TcpStateName(state),
  };

}
