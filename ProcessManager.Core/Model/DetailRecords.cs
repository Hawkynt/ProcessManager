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

/// <summary>A file mapped into a process: a shared library, the image itself, or a data mapping.</summary>
public readonly record struct ModuleRecord(
  string Path,
  ulong BaseAddress,
  ulong Size,
  string Permissions
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
}

/// <summary>
/// One open handle (Windows) or file descriptor (Unix). <paramref name="Name"/> is null when the
/// platform would not name it — on Windows that includes handles whose name resolution timed out,
/// which is a normal outcome rather than a failure (PRD §5.2).
/// </summary>
public readonly record struct HandleRecord(
  ulong Handle,
  HandleKind Kind,
  string? Name,
  string? Access
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
/// everything ever retransmitted on this connection, which needs the netlink socket diagnostics.
/// Non-zero here means this connection is losing packets <em>now</em>.
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
  Counter Retransmits
);
