namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// One thread of a process. Collected on demand for the thread view only — walking every thread of
/// every process on every tick is most of what makes a monitor expensive (PRD §3.5).
/// </summary>
/// <param name="Name">
/// The thread's own name. Linux keeps one per thread and it is in the same <c>stat</c> line the rest
/// of this comes from, so it costs nothing; Windows names threads only when a program bothers to.
/// </param>
/// <param name="LastCpu">Which logical processor it last ran on, or -1 when unknown.</param>
/// <param name="WaitReason">
/// What the thread is blocked on. The answer to "why is this hanging" more often than any other
/// field here (PRD §2), and the one that needs no stack walk to get.
/// </param>
/// <param name="VoluntaryContextSwitches">
/// Switches the thread asked for by blocking. Split from the total because the two halves mean
/// opposite things: a thread with millions of these is waiting on something, while the same number
/// of involuntary ones is a thread being pushed off a contended processor.
/// </param>
/// <param name="InvoluntaryContextSwitches">Switches the scheduler imposed — see above.</param>
/// <param name="BasePriority">
/// The priority the thread was given rather than the one it is running at: the nice value on Linux,
/// where the effective priority in <see cref="Priority"/> moves with it. <see langword="null"/> when
/// the platform did not say, because every integer in the Unix range is a legal nice value and none
/// of them can stand in for "unknown".
/// </param>
/// <param name="Policy">Which scheduler class runs it (PRD §5.3).</param>
/// <param name="Affinity">
/// The processors it is allowed on, in the kernel's own list notation (<c>0-7,16</c>), or
/// <see langword="null"/> when unreadable. Kept as the kernel wrote it rather than expanded to a
/// mask: on a 128-way machine the list is the readable form and the mask is not.
/// </param>
public readonly record struct ThreadRecord(
  int Tid,
  ProcessState State,
  Counter CpuTimeNs,
  long StartTimeUtcTicks,
  ulong StartAddress,
  string? StartSymbol,
  int Priority,
  string? Name,
  Counter UserTimeNs,
  Counter KernelTimeNs,
  Counter ContextSwitches,
  int LastCpu,
  string? WaitReason,
  Counter VoluntaryContextSwitches,
  Counter InvoluntaryContextSwitches,
  int? BasePriority,
  SchedulingPolicy Policy,
  string? Affinity
);

/// <summary>
/// The per-thread half of <c>/proc/[pid]/task/[tid]/status</c> (PRD §29).
/// </summary>
/// <remarks>
/// A record rather than a handful of out-parameters because the whole point is that the failure
/// cases travel with the numbers: a status nobody could open has to reach the view as
/// <see cref="UnknownReason.NotPermitted"/> and not as a thread that has never been switched off a
/// processor in its life (PRD §72.3).
/// </remarks>
public readonly record struct ThreadStatus(
  Counter VoluntaryContextSwitches,
  Counter InvoluntaryContextSwitches,
  string? Affinity
) {

  /// <summary>A status that could not be read, with the reason in every counter it would have had.</summary>
  public static ThreadStatus Unreadable(UnknownReason reason)
    => new(Counter.Unknown(reason), Counter.Unknown(reason), null);

  /// <summary>
  /// Both halves added up, or the reason one of them is missing.
  /// </summary>
  /// <remarks>
  /// Adding a known half to an unknown one would produce a total that is quietly too small, so the
  /// unknown wins — which is <see cref="Counter.Since"/>'s rule applied to a sum.
  /// </remarks>
  public Counter TotalContextSwitches
    => this.VoluntaryContextSwitches.TryGetValue(out var voluntary)
      && this.InvoluntaryContextSwitches.TryGetValue(out var involuntary)
        ? Counter.Of(voluntary + involuntary)
        : this.VoluntaryContextSwitches.HasValue
          ? this.InvoluntaryContextSwitches
          : this.VoluntaryContextSwitches;

}

/// <summary>
/// What a mapping may be done with, as the four characters of a <c>maps</c> line spell it.
/// </summary>
/// <remarks>
/// <see cref="None"/> is "nobody read the permissions", not "no permission is granted": every real
/// mapping carries either <see cref="Shared"/> or <see cref="Private"/>, so a mapping with no access
/// at all still parses to <see cref="Private"/> rather than to zero (PRD §72.3). Windows has no
/// per-module equivalent — its protection is per-region, not per-image — and reports
/// <see cref="None"/> for exactly that reason.
/// </remarks>
[Flags]
public enum MapPermissions : byte {
  None = 0,
  Read = 1,
  Write = 2,
  Execute = 4,
  Shared = 8,
  Private = 16,
}

/// <summary>What an image declares itself to be in its own header.</summary>
public enum ModuleType : byte {
  Unknown = 0,
  Executable,
  SharedObject,
  Relocatable,
  CoreDump,
  /// <summary>Readable, and not an image at all: a locale archive, a font, a mapped database.</summary>
  Data,
}

/// <summary>
/// A file mapped into a process: a shared library, the image itself, or a data mapping.
/// </summary>
/// <remarks>
/// One row per load, not per mapping. A shared library appears in <c>maps</c> as four or five
/// consecutive lines — text, rodata, relro, data — and listing it five times helps nobody, so
/// adjacent lines naming the same file are folded: <paramref name="Size"/> is their total,
/// <paramref name="BaseAddress"/> and <paramref name="EndAddress"/> span them, and
/// <paramref name="MappingCount"/> says how many were folded (PRD §31). A file mapped twice at
/// unrelated addresses — which is what .NET does to every assembly it loads — is two rows, because
/// one row spanning the gap between them would describe an image occupying most of the address space.
/// </remarks>
/// <param name="Permissions">
/// The union of the mappings' permission characters, or empty when the platform does not report any.
/// Empty is a hole, not "no access" — see <see cref="MapPermissions"/>.
/// </param>
/// <param name="ResidentBytes">
/// How much of the image is actually in RAM, summed over its mappings. From <c>smaps</c>, which the
/// cheaper <c>maps</c> does not carry — so this is unknown rather than zero whenever the caller read
/// the cheap file or was refused the expensive one (PRD §5.4).
/// </param>
/// <param name="FileModifiedUtcTicks">
/// The backing file's last-write time, or 0 when it could not be stat'ed. Zero here is year 1 rather
/// than a plausible date, which is what makes it safe to render as "unknown".
/// </param>
/// <param name="EntryPoint">
/// Where execution starts, already biased by the load address for a position-independent image, so
/// that it is an address in <em>this</em> process and can be compared with a stack frame.
/// </param>
/// <param name="Interpreter">
/// The program interpreter the image asks for — the dynamic loader. Only an executable declares one;
/// a library, and anything that is not an image, leave it null.
/// </param>
public readonly record struct ModuleRecord(
  string Path,
  ulong BaseAddress,
  ulong Size,
  string Permissions,
  ulong EndAddress,
  Counter ResidentBytes,
  Counter FileOffset,
  Counter Inode,
  string? Device,
  bool IsDeleted,
  int MappingCount,
  Counter FileSizeBytes,
  long FileModifiedUtcTicks,
  ModuleType Type,
  string? Architecture,
  Counter EntryPoint,
  string? Soname,
  string? Interpreter
);

/// <summary>What a handle or file descriptor refers to.</summary>
public enum HandleKind : byte {
  Unknown = 0,
  File,
  Directory,
  Socket,
  Pipe,
  Event,
  Mutex,
  Section,
  Key,
  Thread,
  Process,
  Device,
  AnonInode,
  /// <summary>An epoll set: a descriptor whose whole purpose is to watch other descriptors.</summary>
  EventPoll,
  Timer,
  Signal,
  /// <summary>An inotify or fanotify watch — a descriptor that reports file-system events.</summary>
  Notify,
  /// <summary>A memfd, or a file under a <c>tmpfs</c> that exists to be shared between processes.</summary>
  SharedMemory,
}

/// <summary>
/// One open handle (Windows) or file descriptor (Unix). <paramref name="Name"/> is null when the
/// platform would not name it — on Windows that includes handles whose name resolution timed out,
/// which is a normal outcome rather than a failure (PRD §5.2).
/// </summary>
/// <param name="Access">
/// What the holder may do with it: <c>r</c>, <c>w</c> or <c>rw</c> on Unix, from the access mode in
/// the open flags; the access mask on Windows. Null when nothing said.
/// </param>
/// <param name="Position">
/// The file offset the next read or write would use. Meaningless for a socket or an event descriptor,
/// which is why it is a <see cref="Counter"/> and not a number that is always there.
/// </param>
/// <param name="OpenFlags">The raw <c>O_*</c> word the descriptor was opened with.</param>
/// <param name="Inode">
/// The inode behind the descriptor. For a socket this is the number that joins it to a row of
/// <c>/proc/net/tcp</c> — the one field that turns "this process holds a socket" into "this process
/// holds <em>that</em> connection" (PRD §32, §40).
/// </param>
/// <param name="TargetPid">
/// The process a pidfd refers to. Unknown for every other kind, because every other kind refers to
/// something that is not a process.
/// </param>
public readonly record struct HandleRecord(
  ulong Handle,
  HandleKind Kind,
  string? Name,
  string? Access,
  Counter Position,
  Counter OpenFlags,
  Counter Inode,
  Counter TargetPid
);

public enum ConnectionProtocol : byte { Tcp, Tcp6, Udp, Udp6, Unix }

/// <summary>
/// What a socket delivers: a byte stream, individual messages, or messages with record boundaries.
/// </summary>
/// <remarks>
/// Redundant for TCP and UDP, where the protocol already says it, and the whole point for a Unix
/// socket — a stream socket and a datagram socket on the same path are different endpoints and only
/// this tells them apart. Named <c>SocketKind</c> rather than <c>SocketType</c> so that a file with
/// <c>using System.Net.Sockets</c> in it does not silently bind to the other one.
/// </remarks>
public enum SocketKind : byte { Unknown = 0, Stream, Datagram, SeqPacket }

/// <summary>
/// A socket, with the state the kernel reports for it (PRD §40).
/// </summary>
/// <param name="Inode">
/// The socket's inode, which on Linux is the only thing joining it to a process: a descriptor points
/// at <c>socket:[n]</c> and the network tables are keyed by the same <c>n</c>. Zero where the
/// platform has no such number — Windows names the owning process in the table itself and needs no
/// join.
/// </param>
/// <param name="Pid">
/// The process holding a descriptor on it, or 0 when none was found. Zero is never a real owner —
/// pid 0 is the kernel — so it reads as "nobody visible", which on Linux usually means the socket
/// belongs to another user's process whose descriptors we may not list, and sometimes means nothing
/// holds it at all: a <c>TIME_WAIT</c> socket outlives the process that closed it.
/// </param>
/// <param name="UserId">
/// The uid the kernel charges the socket to, or -1 where the platform does not say. This is the
/// socket's own owner and not the owning process's — they are the same in almost every case, and
/// the exception is a socket passed between processes over a Unix socket, which keeps the uid of
/// whoever created it.
/// </param>
/// <param name="Interface">
/// Which interface the local address lives on, or null when nothing claims it. <c>*</c> means the
/// socket is bound to the wildcard address and so is on all of them.
/// </param>
/// <param name="SendQueueBytes">
/// Bytes written and not yet acknowledged by the peer, and bytes received and not yet read. A
/// send queue that stays high is a peer that is not keeping up; a receive queue that stays high is
/// this process not keeping up. On a listening TCP socket the kernel reuses the same two fields for
/// the accept backlog instead, which is why <see cref="State"/> has to be read alongside them.
/// </param>
/// <param name="Retransmits">
/// How often the segment currently awaiting acknowledgement has been sent again — not a total of
/// everything ever retransmitted on this connection, which is
/// <see cref="SocketStatistics.TotalRetransmits"/>. Non-zero here means this connection is losing
/// packets <em>now</em>.
/// </param>
/// <param name="Statistics">
/// What the kernel's socket diagnostics said about this connection, or the reason they said nothing.
/// The socket tables under <c>/proc/net</c> cannot fill this: they carry no byte counters and no
/// round-trip time, and the only way to those is <c>NETLINK_INET_DIAG</c>.
/// </param>
/// <param name="SendRate">
/// Bytes per second in each direction over the interval between the two readings this was derived
/// from. <see cref="UnknownReason.NotSampledYet"/> on the first sight of a socket, because a rate
/// needs two — and a one-shot listing never gets a second.
/// </param>
/// <param name="OwningService">
/// The service or scope the owning process belongs to — a systemd unit name on Linux — or null when
/// nothing owns it, no process was attributed, or the process is not under a unit at all. A socket
/// is not charged to a unit by the kernel; this is the unit of whoever holds the descriptor.
/// </param>
/// <param name="ContainerPath">
/// The owning process's cgroup path, which is what says whether a listening port belongs to the host
/// or to a container. Null for the same three reasons <paramref name="OwningService"/> is.
/// </param>
public readonly record struct ConnectionRecord(
  ConnectionProtocol Protocol,
  SocketKind Kind,
  string LocalAddress,
  int LocalPort,
  string RemoteAddress,
  int RemotePort,
  string State,
  ulong Inode,
  int Pid,
  int UserId,
  string? UserName,
  string? Interface,
  Counter SendQueueBytes,
  Counter ReceiveQueueBytes,
  Counter Retransmits,
  SocketStatistics Statistics,
  Rate SendRate,
  Rate ReceiveRate,
  string? OwningService,
  string? ContainerPath
);

/// <summary>
/// What <c>NETLINK_INET_DIAG</c> reports about one socket: the counters <c>/proc/net/tcp</c> has no
/// column for (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// This is <c>ss -i</c>'s source. The kernel answers a <c>SOCK_DIAG_BY_FAMILY</c> request with an
/// <c>inet_diag_msg</c> per socket and, where asked for it, a <c>tcp_info</c> attached — and
/// <c>tcp_info</c> is where the bytes, the segments, the round-trip time and the lifetime
/// retransmission count live.
/// </para>
/// <para>
/// Every member is a <see cref="Counter"/> and none of them is ever a plain zero, because the four
/// ways to have no reading are all common here: the kernel may be built without the diagnostics, the
/// socket may be a datagram socket the module does not describe, the process may not be allowed to
/// ask, or the connection may be in a state that has no <c>tcp_info</c> left. Constructing one of
/// these positionally is deliberate: <c>default</c> would say every counter is a measured nought
/// (PRD §72.3), so there is no parameterless way to make one.
/// </para>
/// </remarks>
/// <param name="BytesSent">
/// Payload bytes handed to the peer over this connection's life, retransmissions included — the
/// figure <c>ss</c> prints as <c>bytes_sent</c>. Not what the interface carried: headers are not in
/// it.
/// </param>
/// <param name="BytesReceived">Payload bytes taken from the peer, the same way round.</param>
/// <param name="PacketsSent">
/// Segments in each direction, which is the honest packet count for a TCP connection: the kernel
/// counts segments and the wire carries one packet per segment except where something fragments.
/// </param>
/// <param name="TotalRetransmits">
/// Every retransmission this connection has ever made, as against <see cref="ConnectionRecord.Retransmits"/>,
/// which is only the segment being retried right now.
/// </param>
/// <param name="RoundTripTimeMicroseconds">
/// The smoothed round-trip time, in microseconds, and the variance the kernel keeps beside it. This
/// is the latency of the path this connection is actually using, measured by the connection itself
/// — not a ping to the same address, which may take a different route.
/// </param>
public readonly record struct SocketStatistics(
  Counter BytesSent,
  Counter BytesReceived,
  Counter PacketsSent,
  Counter PacketsReceived,
  Counter TotalRetransmits,
  Counter RoundTripTimeMicroseconds,
  Counter RoundTripVarianceMicroseconds
) {

  /// <summary>
  /// Nothing was read, and the reason is the same for every counter.
  /// </summary>
  /// <remarks>
  /// The one supported way to make an empty set. A <c>TryGetValue</c> that misses leaves
  /// <c>default</c> behind, whose reason is <see cref="UnknownReason.None"/> — "the value is here and
  /// it is zero" — and that is the defect this exists to make impossible to write by accident.
  /// </remarks>
  public static SocketStatistics Unknown(UnknownReason reason) {
    var counter = Counter.Unknown(reason);
    return new(counter, counter, counter, counter, counter, counter, counter);
  }

  /// <summary>The socket diagnostics were not consulted, or have not answered about this socket.</summary>
  public static readonly SocketStatistics NotRead = Unknown(UnknownReason.NotSampledYet);

  /// <summary>There is no such source here: another platform, or a protocol with no such counters.</summary>
  public static readonly SocketStatistics NotSupported = Unknown(UnknownReason.NotSupportedOnPlatform);

  /// <summary>True when at least one counter carries a reading.</summary>
  public bool HasAny
    => this.BytesSent.HasValue
    || this.BytesReceived.HasValue
    || this.PacketsSent.HasValue
    || this.PacketsReceived.HasValue
    || this.TotalRetransmits.HasValue
    || this.RoundTripTimeMicroseconds.HasValue;

}
