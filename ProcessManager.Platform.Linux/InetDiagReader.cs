using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Asks the kernel's socket diagnostics what <c>/proc/net/tcp</c> cannot say (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// A <c>NETLINK_SOCK_DIAG</c> socket, one dump request per address family, and the replies handed to
/// <see cref="InetDiagParser"/>. This is exactly what <c>ss -i</c> does, and it is the only way to a
/// connection's byte counters, its round-trip time and its lifetime retransmission count: those live
/// in <c>tcp_info</c>, and <c>tcp_info</c> is not in <c>/proc</c>.
/// </para>
/// <para>
/// Everything about it may be absent — a kernel built without <c>CONFIG_INET_DIAG</c>, a seccomp
/// filter that forbids <c>AF_NETLINK</c>, a container with the netlink family unavailable — and each
/// of those is a normal thing to run into rather than an error to report. The failure is carried out
/// as an <see cref="UnknownReason"/> so that the columns say why they are empty instead of saying
/// nought (PRD §72.3).
/// </para>
/// <para>
/// The socket is opened and closed per call. That is one <c>socket</c> and one <c>close</c> against a
/// question somebody asked by opening a tab, and it means nothing holds a descriptor open across the
/// life of a program that may run for days.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class InetDiagReader {

  private const int _AF_NETLINK = 16;
  private const int _AF_INET = 2;
  private const int _AF_INET6 = 10;
  private const int _SOCK_DGRAM = 2;
  private const int _SOCK_CLOEXEC = 0x80000;
  private const int _NETLINK_SOCK_DIAG = 4;
  private const int _IPPROTO_TCP = 6;

  private const int _SOL_SOCKET = 1;
  private const int _SO_RCVTIMEO = 20;

  private const int _EPERM = 1;
  private const int _EACCES = 13;

  /// <summary>
  /// One datagram of a dump. The kernel sizes a dump message to the socket's receive buffer and
  /// stops well short of this; a reply that did not fit would be flagged rather than silently
  /// halved, and the walk stops at the first length that overruns what arrived.
  /// </summary>
  private const int _BufferLength = 64 * 1024;

  /// <summary>
  /// A ceiling on how many datagrams one dump may take, so a kernel that never sends
  /// <c>NLMSG_DONE</c> cannot hold the caller for ever. At roughly two hundred sockets a datagram
  /// this is far more than any real machine has.
  /// </summary>
  private const int _MaximumDatagrams = 512;

  [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
  private static partial int Socket(int domain, int type, int protocol);

  [LibraryImport("libc", EntryPoint = "close")]
  private static partial int Close(int fd);

  [LibraryImport("libc", EntryPoint = "sendto", SetLastError = true)]
  private static partial nint SendTo(
    int fd,
    ref byte buffer,
    nuint length,
    int flags,
    ref byte address,
    uint addressLength
  );

  [LibraryImport("libc", EntryPoint = "recv", SetLastError = true)]
  private static partial nint Receive(int fd, ref byte buffer, nuint length, int flags);

  [LibraryImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
  private static partial int SetSocketOption(int fd, int level, int option, ref byte value, uint length);

  /// <summary>
  /// Fills <paramref name="into"/> with one entry per TCP socket that has an inode, keyed by it.
  /// </summary>
  /// <param name="reason">
  /// Why nothing came back, when nothing did. <see cref="UnknownReason.None"/> on success — including
  /// the success of finding no sockets at all, which is a real answer about a quiet machine.
  /// </param>
  public static bool TryRead(Dictionary<ulong, SocketStatistics> into, out UnknownReason reason) {
    ArgumentNullException.ThrowIfNull(into);

    reason = UnknownReason.NotSupportedOnPlatform;
    var fd = Socket(_AF_NETLINK, _SOCK_DGRAM | _SOCK_CLOEXEC, _NETLINK_SOCK_DIAG);
    if (fd < 0)
      // EPERM and EACCES here mean a sandbox refused the family, which is a statement about this
      // process's privilege; anything else means the kernel has no such diagnostics to offer.
      return Failed(Marshal.GetLastPInvokeError(), out reason);

    try {
      // Without a timeout a kernel that answers nothing leaves the caller in recv for ever, and this
      // is called from a front-end that is drawing a window.
      Span<byte> timeout = stackalloc byte[16];
      timeout.Clear();
      BinaryPrimitives.WriteInt64LittleEndian(timeout[8..], 500_000);   // half a second, in microseconds
      SetSocketOption(fd, _SOL_SOCKET, _SO_RCVTIMEO, ref MemoryMarshal.GetReference(timeout), (uint)timeout.Length);

      // UDP is deliberately not asked about. udp_diag answers, but it has no tcp_info to attach:
      // Linux keeps no byte total, no segment count and no round-trip time for a datagram socket,
      // and a request for them would come back empty in a way that looks like a failed read.
      var v4 = Dump(fd, _AF_INET, into, out var v4Reason);
      var v6 = Dump(fd, _AF_INET6, into, out var v6Reason);
      if (v4 || v6) {
        reason = UnknownReason.None;
        return true;
      }

      // Both refused. A machine with no IPv6 at all still answers the v6 dump — with nothing — so a
      // pair of refusals is about the diagnostics rather than about the address families.
      reason = v4Reason == UnknownReason.None ? v6Reason : v4Reason;
      return false;
    } finally {
      Close(fd);
    }
  }

  private static bool Failed(int errno, out UnknownReason reason) {
    reason = errno is _EPERM or _EACCES
      ? UnknownReason.NotPermitted
      : UnknownReason.NotSupportedOnPlatform;

    return false;
  }

  /// <summary>Sends one dump request for a family and reads until the kernel says it is finished.</summary>
  private static bool Dump(int fd, byte family, Dictionary<ulong, SocketStatistics> into, out UnknownReason reason) {
    reason = UnknownReason.None;

    Span<byte> request = stackalloc byte[InetDiagParser.RequestLength];
    InetDiagParser.BuildRequest(
      request,
      family,
      _IPPROTO_TCP,
      InetDiagParser.ExtensionInfo,
      InetDiagParser.AllStates,
      sequence: family
    );

    // sockaddr_nl: family, two bytes of padding, a port id of zero meaning the kernel, and no
    // multicast groups.
    Span<byte> kernel = stackalloc byte[12];
    kernel.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(kernel, _AF_NETLINK);

    var sent = SendTo(
      fd,
      ref MemoryMarshal.GetReference(request),
      (nuint)request.Length,
      0,
      ref MemoryMarshal.GetReference(kernel),
      (uint)kernel.Length
    );

    if (sent < 0)
      return Failed(Marshal.GetLastPInvokeError(), out reason);

    var buffer = new byte[_BufferLength];
    for (var datagram = 0; datagram < _MaximumDatagrams; ++datagram) {
      var read = Receive(fd, ref buffer[0], (nuint)buffer.Length, 0);
      if (read <= 0)
        // A timeout or a closed socket part way through a dump. What was already parsed stands —
        // those sockets were described — and the ones that never arrived stay unknown.
        return Failed(read == 0 ? 0 : Marshal.GetLastPInvokeError(), out reason);

      if (!InetDiagParser.Parse(buffer.AsSpan(0, (int)read), into, out var finished, out var errorCode))
        return Failed(errorCode, out reason);

      if (finished)
        return true;
    }

    return true;
  }

}
