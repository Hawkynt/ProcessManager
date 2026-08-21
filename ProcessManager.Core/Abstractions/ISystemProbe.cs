using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>
/// The one seam between the engine and an operating system.
/// </summary>
/// <remarks>
/// <para>
/// A probe returns <em>raw counters</em> and nothing else: no rates, no percentages, no deltas, no
/// sorting. Everything derived is computed above this line, identically on every platform, which is
/// what lets the whole engine be tested against recorded fixtures instead of against the machine
/// running the tests (PRD §2, §9.1).
/// </para>
/// <para>
/// A probe never guesses. Anything it cannot read is a <see cref="Counter"/> carrying the reason
/// (PRD §3.4), never a zero.
/// </para>
/// </remarks>
public interface ISystemProbe : IDisposable {

  /// <summary>A short name for the UI and the logs, e.g. <c>linux:/proc</c>.</summary>
  string Description { get; }

  /// <summary>
  /// What the machine is, as opposed to what it is doing (PRD §96).
  /// </summary>
  /// <remarks>
  /// Cached by the probe: none of it changes between samples, and several of the reads would be
  /// indefensible at one hertz. Callers may ask as often as they like.
  /// </remarks>
  HostInfo DescribeHost();

  /// <summary>
  /// Fills <paramref name="snapshot"/> with the current state of the machine. Called on a background
  /// thread; never called re-entrantly for the same probe.
  /// </summary>
  void Sample(SystemSnapshot snapshot);

  /// <summary>
  /// Open handles / descriptors of one process, counted now.
  /// </summary>
  /// <remarks>
  /// Deliberately not part of <see cref="Sample"/>. On Linux the kernel materialises one directory
  /// entry per descriptor when <c>/proc/[pid]/fd</c> is read, which measured at 85 µs per process —
  /// twice the cost of everything else in a sample put together, for a column that is only legible
  /// on the rows actually on screen. Front-ends call this for the rows they draw (PRD §3.5).
  /// </remarks>
  Counter GetHandleCount(ProcessKey key);

  /// <summary>Threads of one process, for the detail view. Empty when they cannot be read.</summary>
  IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key);

  /// <summary>Mapped files of one process.</summary>
  IReadOnlyList<ModuleRecord> GetModules(ProcessKey key);

  /// <summary>Open handles / descriptors of one process.</summary>
  IReadOnlyList<HandleRecord> GetHandles(ProcessKey key);

  /// <summary>Sockets owned by one process.</summary>
  IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key);

  /// <summary>
  /// Every socket on the machine, for the network view and <c>--connections</c> (PRD §40, §59).
  /// </summary>
  /// <remarks>
  /// Not the per-process call in a loop: the socket tables are machine-wide and reading them once
  /// per process would read the same few files several hundred times. Costly enough to be asked for
  /// rather than sampled (PRD §5.4).
  /// <para>
  /// The default is no sockets, which is what a probe that has not learnt to look yet honestly
  /// knows — a caller must not read it as a machine with nothing connected.
  /// </para>
  /// </remarks>
  IReadOnlyList<ConnectionRecord> GetConnections() => [];

  /// <summary>
  /// The machine's services (PRD §41), or an empty list where they are not read yet.
  /// </summary>
  IReadOnlyList<ServiceRecord> GetServices();

  /// <summary>
  /// Every graphics adapter this machine has, with whatever its driver is willing to say (PRD §50).
  /// </summary>
  /// <remarks>
  /// Read on demand by the page that shows it and never from the sample loop, whose allocation
  /// budget is a build gate (PRD §5.4).
  /// <para>
  /// The default is no adapters, which is what a probe that has not learnt to look yet honestly
  /// knows. It is not a promise that the machine has none — a page that showed "0 GPUs" on Windows
  /// would be stating something false rather than admitting a gap.
  /// </para>
  /// </remarks>
  IReadOnlyList<GpuInfo> DescribeGpus() => [];

  /// <summary>
  /// How the machine's logical processors are arranged: sockets, physical cores, and which are
  /// performance and which efficiency cores (PRD §46).
  /// </summary>
  /// <remarks>
  /// Read once — a machine does not repartition its cores while a program watches it. The default
  /// is an empty topology, which a caller must read as "nothing is known about the arrangement" and
  /// fall back to a flat list of cores, not as "this machine has none".
  /// </remarks>
  CpuTopology DescribeTopology() => CpuTopology.Empty;

  /// <summary>
  /// The desktop's windows and the processes behind them (PRD §39).
  /// </summary>
  /// <remarks>
  /// Carries why it could not answer rather than only an empty list. "No windows" and "this session
  /// will not tell you about windows" look identical to a caller that only gets a list, and on a
  /// Wayland desktop — half of all Linux users — the second is the true one (PRD §5.3).
  /// </remarks>
  WindowList GetWindows() => WindowList.NotImplemented;

  /// <summary>The window under the pointer, for picking a process by pointing at it.</summary>
  WindowRecord? WindowUnderPointer() => null;

  /// <summary>
  /// Who is logged in (PRD §43), or an empty list where that is not read yet.
  /// </summary>
  IReadOnlyList<SessionRecord> GetSessions();

  /// <summary>What a storage device is, by name (PRD §48).</summary>
  DiskInfo DescribeDisk(string name);

  /// <summary>What a network interface is, by name (PRD §49).</summary>
  NetworkInterfaceInfo DescribeInterface(string name);

  /// <summary>
  /// What is configured to start at login (PRD §42), or an empty list where that is not read yet.
  /// </summary>
  IReadOnlyList<StartupEntry> GetStartupEntries();

  /// <summary>The process's environment block, or an empty list when it may not be read.</summary>
  IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key);

}
