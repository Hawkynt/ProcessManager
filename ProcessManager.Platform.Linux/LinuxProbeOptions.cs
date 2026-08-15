namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Everything the Linux probe needs to be told rather than to discover, so that a test can point it
/// at a recorded <c>/proc</c> tree and get the same answers on any machine (PRD §9.1).
/// </summary>
public sealed record LinuxProbeOptions {

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
  /// Whose processes the probe can expect to read privileged files of. Defaults to the running
  /// user; a fixture replay sets 0 so that every recorded file is attempted regardless of who
  /// recorded it.
  /// </summary>
  public int EffectiveUserId { get; init; } = Native.EffectiveUserId;

}
