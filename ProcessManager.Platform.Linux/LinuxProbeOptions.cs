namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Everything the Linux probe needs to be told rather than to discover, so that a test can point it
/// at a recorded <c>/proc</c> tree and get the same answers on any machine (PRD §9.1).
/// </summary>
public sealed record LinuxProbeOptions {

  /// <summary>
  /// Read each process's LSM label from <c>attr/current</c> (PRD §21, §36).
  /// </summary>
  /// <remarks>
  /// Off by default: it is one more open and read per process, which at six hundred processes is the
  /// same order of cost as the file-descriptor scan that had to leave the sample loop (PRD §5.4).
  /// </remarks>
  public bool ReadSecurityContext { get; init; }

  /// <summary>Where <c>/proc</c> is. A fixture directory in tests.</summary>
  public string ProcRoot { get; init; } = "/proc";

  /// <summary>Where the password file is, for uid → name.</summary>
  public string PasswdPath { get; init; } = "/etc/passwd";

  /// <summary>
  /// <c>USER_HZ</c>. Defaults to <c>sysconf(_SC_CLK_TCK)</c> on the running machine; a fixture
  /// recorded elsewhere must state the value it was recorded with, or every CPU time is wrong by a
  /// constant factor and nothing says so.
  /// </summary>
  public long ClockTicksPerSecond { get; init; } = Native.ClockTicksPerSecond;

  public long PageSize { get; init; } = Native.PageSize;

  /// <summary>
  /// Read <c>smaps_rollup</c> for a true PSS instead of using <c>RssAnon</c> from <c>status</c>.
  /// </summary>
  /// <remarks>
  /// PSS is the honest "what would I get back" number, and it costs the kernel a walk of the whole
  /// page table of every process, every sample. Off by default: the default column is anonymous RSS,
  /// which comes free with a file already being read and is wrong only in that it ignores the
  /// process's share of what it maps.
  /// </remarks>
  public bool UseProportionalSetSize { get; init; }

  /// <summary>
  /// Count open file descriptors for <em>every</em> process on <em>every</em> sample.
  /// </summary>
  /// <remarks>
  /// Off, and measured: reading <c>/proc/[pid]/fd</c> makes the kernel materialise one directory
  /// entry per open descriptor, which on a machine with 877 processes cost 85 µs per process — 74 ms
  /// of a 113 ms sample, against a whole-sample budget of 25 ms. The column is filled for the rows a
  /// front-end actually draws, through <see cref="Abstractions.ISystemProbe.GetHandleCount"/>
  /// (PRD §3.5). Turn this on only for a one-shot dump where the whole table is the output.
  /// </remarks>
  public bool CountFileDescriptors { get; init; }

  /// <summary>Read the cgroup path of each process, for the container column.</summary>
  public bool ReadCgroups { get; init; } = true;

  /// <summary>
  /// Read files through the BCL rather than through syscalls, even on Linux.
  /// </summary>
  /// <remarks>
  /// Off Linux this is what happens anyway — there is no <c>getdents64</c> on macOS and no libc on
  /// Windows, and the fixture tests run on all three legs (PRD §9.1). The switch exists so that the
  /// portable path can be exercised <em>on</em> Linux too: otherwise it is code that only ever runs
  /// where nobody can debug it, and the first time it breaks is on somebody else's CI.
  /// </remarks>
  public bool UsePortableFileAccess { get; init; }

  /// <summary>
  /// Whose processes the probe can expect to read privileged files of. Defaults to the running
  /// user; a fixture replay sets 0 so that every recorded file is attempted regardless of who
  /// recorded it.
  /// </summary>
  public int EffectiveUserId { get; init; } = Native.EffectiveUserId;

  /// <summary>
  /// A channel to the privileged helper, or null to run entirely unprivileged (PRD §8).
  /// </summary>
  /// <remarks>
  /// Used only for the <em>on-demand</em> queries — another user's environment block, their open
  /// descriptors — and never inside a sample. Each request is a round trip to another process; doing
  /// that per process per second would cost more than everything else in the sample put together
  /// (PRD §4). The per-sample I/O column stays unprivileged and reports `NotPermitted`, which is the
  /// truth about what this user can see.
  /// </remarks>
  public Abstractions.ElevatedChannel? Elevated { get; init; }

}
