using System.Buffers.Binary;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The wire format of <c>NETLINK_INET_DIAG</c>: the request that asks the kernel about every socket,
/// and the reply that carries what <c>/proc/net/tcp</c> has no column for (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// This is the source <c>ss -i</c> reads. A <c>SOCK_DIAG_BY_FAMILY</c> request goes out naming a
/// family, a protocol and a set of states; the kernel answers with one <c>inet_diag_msg</c> per
/// matching socket, each optionally carrying a <c>tcp_info</c> as an attribute. Bytes sent and
/// received, segments in and out, the smoothed round-trip time and the lifetime retransmission
/// count are all in that attribute and nowhere else — no arrangement of the <c>/proc</c> columns
/// produces them.
/// </para>
/// <para>
/// <c>tcp_info</c> grows. It was 240 bytes a few releases ago and is 280 on 7.1; the kernel sends
/// whatever its own version is, and a build against a newer header talking to an older kernel gets a
/// short one. So every field is read only after checking the attribute is long enough to hold it,
/// and a field past the end is <see cref="UnknownReason.NotSupportedOnPlatform"/> — this kernel does
/// not have it — rather than nought.
/// </para>
/// <para>
/// No platform attribute, no socket and no file access: it is handed a buffer and hands back
/// records, so the awkward replies can be recorded once and replayed on every CI leg (PRD §9.2).
/// The socket itself is <c>Platform.Linux</c>'s problem.
/// </para>
/// </remarks>
public static class InetDiagParser {

  /// <summary>Netlink message header, and the alignment everything in netlink is padded to.</summary>
  private const int _HeaderLength = 16;
  private const int _Alignment = 4;

  /// <summary>The two message types that end a dump rather than describing a socket.</summary>
  private const ushort _Done = 3;
  private const ushort _Error = 2;

  /// <summary><c>SOCK_DIAG_BY_FAMILY</c>, the only request type this asks for.</summary>
  public const ushort SockDiagByFamily = 20;

  /// <summary><c>NLM_F_REQUEST | NLM_F_DUMP</c>: ask, and ask about all of them.</summary>
  private const ushort _RequestDump = 0x001 | 0x300;

  /// <summary><c>inet_diag_req_v2</c> is 56 bytes: four bytes of selector, four of states, 48 of id.</summary>
  private const int _RequestBodyLength = 56;

  /// <summary>How long a whole request is, header included.</summary>
  public const int RequestLength = _HeaderLength + _RequestBodyLength;

  /// <summary>
  /// <c>INET_DIAG_INFO</c>, the attribute carrying <c>tcp_info</c>. The extension bitmask in the
  /// request numbers its bits from zero while the attribute numbers from one, so asking for
  /// attribute 2 means setting bit 1.
  /// </summary>
  private const ushort _AttributeInfo = 2;
  public const byte ExtensionInfo = 1 << (_AttributeInfo - 1);

  /// <summary>
  /// Every TCP state at once, which is what <c>ss -a</c> asks for.
  /// </summary>
  /// <remarks>
  /// Bit <c>n</c> is state <c>n</c>, and the states run 1..12. Bit 0 is unused and left clear
  /// deliberately: it is not a state, and setting it makes some kernels reject the request.
  /// </remarks>
  public const uint AllStates = 0xFFFE;

  /// <summary>
  /// Writes one dump request into <paramref name="into"/> and returns how many bytes it wrote.
  /// </summary>
  /// <param name="family">2 for IPv4, 10 for IPv6. One request answers about one family.</param>
  /// <param name="protocol">6 for TCP, 17 for UDP.</param>
  /// <param name="extensions">
  /// Which extra attributes to attach; <see cref="ExtensionInfo"/> for <c>tcp_info</c>. Asking for
  /// nothing still returns the socket, its queues and its inode — the join is in the message itself.
  /// </param>
  /// <param name="sequence">Echoed back on every reply, so a stale answer can be told from a fresh one.</param>
  public static int BuildRequest(
    Span<byte> into,
    byte family,
    byte protocol,
    byte extensions,
    uint states,
    uint sequence
  ) {
    if (into.Length < RequestLength)
      throw new ArgumentException($"a request needs {RequestLength} bytes.", nameof(into));

    var request = into[..RequestLength];
    request.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(request, RequestLength);
    BinaryPrimitives.WriteUInt16LittleEndian(request[4..], SockDiagByFamily);
    BinaryPrimitives.WriteUInt16LittleEndian(request[6..], _RequestDump);
    BinaryPrimitives.WriteUInt32LittleEndian(request[8..], sequence);
    // The port id is left zero, which asks the kernel to fill in this socket's own. Choosing one
    // means colliding with whatever else in the process has already bound a netlink socket.

    var body = request[_HeaderLength..];
    body[0] = family;
    body[1] = protocol;
    body[2] = extensions;
    body[3] = 0;                                           // pad
    BinaryPrimitives.WriteUInt32LittleEndian(body[4..], states);
    // The remaining 48 bytes are inet_diag_sockid, all zero: no filter, every socket. A dump ignores
    // it except for the cookie, and a zero cookie means "any".

    return RequestLength;
  }

  /// <summary>
  /// Walks the netlink messages in <paramref name="buffer"/>, adding one entry per socket.
  /// </summary>
  /// <param name="into">
  /// Keyed by inode, which is the same number <c>/proc/net/tcp</c> prints and so the join to a
  /// process. Sockets with no inode — a <c>TIME_WAIT</c> stub, a request socket — are skipped
  /// rather than all colliding on key zero.
  /// </param>
  /// <param name="finished">
  /// True when the kernel sent <c>NLMSG_DONE</c> and there is nothing more to read. A dump arrives in
  /// as many datagrams as it takes, so the caller reads until this says stop.
  /// </param>
  /// <returns>
  /// False when the kernel sent an error instead of an answer, with <paramref name="errorCode"/>
  /// holding the negated errno it reported. An error ends the dump the same way a done does.
  /// </returns>
  public static bool Parse(
    ReadOnlySpan<byte> buffer,
    Dictionary<ulong, SocketStatistics> into,
    out bool finished,
    out int errorCode
  ) {
    ArgumentNullException.ThrowIfNull(into);

    finished = false;
    errorCode = 0;
    var offset = 0;
    while (buffer.Length - offset >= _HeaderLength) {
      var message = buffer[offset..];
      var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(message);
      // A length shorter than the header or longer than what arrived is a truncated datagram, and
      // walking on from it would read a header out of the middle of a payload.
      if (length < _HeaderLength || length > message.Length)
        break;

      var type = BinaryPrimitives.ReadUInt16LittleEndian(message[4..]);
      switch (type) {
        case _Done:
          finished = true;
          return true;
        case _Error:
          finished = true;
          // The payload is a signed errno followed by the header of the message it is about. Zero is
          // not a failure at all — netlink says "understood" that way when acknowledgements are on.
          if (length >= _HeaderLength + 4)
            errorCode = -BinaryPrimitives.ReadInt32LittleEndian(message[_HeaderLength..]);

          return errorCode == 0;
        default:
          ReadSocket(message[_HeaderLength..length], into);
          break;
      }

      offset += Align(length);
    }

    return true;
  }

  /// <summary><c>inet_diag_msg</c> is 72 bytes; attributes follow it.</summary>
  private const int _SocketLength = 72;
  private const int _StateOffset = 1;
  private const int _InodeOffset = 68;

  /// <summary>
  /// <c>TCP_LISTEN</c>, the one state whose <c>tcp_info</c> is almost entirely a block of zeros.
  /// </summary>
  /// <remarks>
  /// <c>tcp_get_info</c> clears the whole structure, fills in four fields that mean something for a
  /// listener — the pacing rate, the receive threshold, and the accept backlog pair it aliases onto
  /// <c>tcpi_unacked</c> and <c>tcpi_sacked</c> — and returns. Everything this reads is therefore the
  /// <c>memset</c> and not a measurement: passing it on would say a listening socket has moved no
  /// bytes, sent no segments, never retransmitted and has a round-trip time of nought, about
  /// something nobody measured. It is the same trap as the queue columns of <c>/proc/net/tcp</c>,
  /// which a listener fills with its backlog instead of with byte counts (PRD §40, §72.3).
  /// </remarks>
  private const byte _Listen = 10;

  private static void ReadSocket(ReadOnlySpan<byte> body, Dictionary<ulong, SocketStatistics> into) {
    if (body.Length < _SocketLength)
      return;

    var state = body[_StateOffset];
    var inode = BinaryPrimitives.ReadUInt32LittleEndian(body[_InodeOffset..]);
    // Zero means no open file refers to this socket, so nothing can join it to a process. Several
    // sockets can be in that state at once and they would all land on the same key.
    if (inode == 0)
      return;

    // No tcp_info attached is the normal answer for a datagram socket and for a connection with no
    // socket structure left: the module has nothing of this kind to say about it, which is not the
    // same as having said nought.
    var statistics = SocketStatistics.NotSupported;
    var attributes = body[_SocketLength..];
    while (attributes.Length >= _Alignment) {
      var length = BinaryPrimitives.ReadUInt16LittleEndian(attributes);
      var type = BinaryPrimitives.ReadUInt16LittleEndian(attributes[2..]);
      if (length < _Alignment || length > attributes.Length)
        break;

      if (type == _AttributeInfo)
        statistics = state == _Listen
          ? SocketStatistics.NotSupported
          : ReadTcpInfo(attributes[_Alignment..length]);

      var step = Align(length);
      if (step > attributes.Length)
        break;

      attributes = attributes[step..];
    }

    into[inode] = statistics;
  }

  /// <summary>
  /// Where each field this reads sits in <c>struct tcp_info</c>, checked against the running kernel's
  /// own headers rather than remembered.
  /// </summary>
  /// <remarks>
  /// The struct only ever grows at the end, which is what makes reading it by offset safe across
  /// versions — a field that exists has never moved. Everything is little-endian because netlink
  /// speaks the host's byte order and every Linux this runs on is little-endian; a recorded reply
  /// has to read the same on a big-endian CI runner, so the order is fixed here rather than taken
  /// from the machine doing the parsing.
  /// </remarks>
  private const int _RoundTripTime = 68;
  private const int _RoundTripVariance = 72;
  private const int _TotalRetransmits = 100;
  private const int _BytesReceived = 128;
  private const int _SegmentsOut = 136;
  private const int _SegmentsIn = 140;
  private const int _BytesSent = 200;

  private static SocketStatistics ReadTcpInfo(ReadOnlySpan<byte> info) => new(
    // bytes_sent counts retransmissions in, which is what the connection put on the wire and so what
    // the send rate should be derived from. bytes_acked, next to it, is what the peer admitted to.
    Read64(info, _BytesSent),
    Read64(info, _BytesReceived),
    Read32(info, _SegmentsOut),
    Read32(info, _SegmentsIn),
    Read32(info, _TotalRetransmits),
    ReadRoundTrip(info, _RoundTripTime),
    ReadRoundTrip(info, _RoundTripVariance)
  );

  /// <summary>
  /// A round-trip time, where zero means the connection has never measured one.
  /// </summary>
  /// <remarks>
  /// The kernel starts a socket's smoothed round-trip time at zero and only ever writes to it from a
  /// completed sample, which it clamps to at least a microsecond before scaling — so a connection
  /// that has measured anything at all reports at least 1 here, and 0 is the initial value showing
  /// through. A socket still in <c>SYN_SENT</c> is the ordinary way to see it. <c>ss</c> prints no
  /// <c>rtt:</c> at all in that case; this says why there is none instead, because a column that
  /// simply disappears is not available to a table (PRD §72.3).
  /// </remarks>
  private static Counter ReadRoundTrip(ReadOnlySpan<byte> info, int offset) {
    var raw = Read32(info, offset);
    return raw.HasValue && raw.Value == 0 ? Counter.Unknown(UnknownReason.NotSampledYet) : raw;
  }

  /// <summary>
  /// A field the kernel is too old to have sent. Not a zero: a kernel without
  /// <c>tcpi_bytes_sent</c> has not said this connection sent nothing.
  /// </summary>
  private static Counter Read32(ReadOnlySpan<byte> info, int offset)
    => info.Length >= offset + 4
      ? Counter.Of(BinaryPrimitives.ReadUInt32LittleEndian(info[offset..]))
      : Counter.NotSupported;

  private static Counter Read64(ReadOnlySpan<byte> info, int offset)
    => info.Length >= offset + 8
      ? Counter.Of(BinaryPrimitives.ReadUInt64LittleEndian(info[offset..]))
      : Counter.NotSupported;

  /// <summary>Netlink rounds every length up to four bytes, and the padding is not part of the field.</summary>
  private static int Align(int length) => (length + _Alignment - 1) & ~(_Alignment - 1);

}
