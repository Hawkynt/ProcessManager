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
  string? WaitReason
);

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

/// <summary>A socket owned by a process, with the state the kernel reports for it.</summary>
public readonly record struct ConnectionRecord(
  ConnectionProtocol Protocol,
  string LocalAddress,
  int LocalPort,
  string RemoteAddress,
  int RemotePort,
  string State,
  ulong Inode
);
