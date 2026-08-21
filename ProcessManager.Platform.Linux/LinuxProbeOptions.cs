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

  /// <summary>
  /// Keep the <c>Groups:</c> line of <c>status</c> as text (PRD §36).
  /// </summary>
  /// <remarks>
  /// Off by default, and for a different reason than the LSM label: the line costs no extra read at
  /// all — it is in a file the sampler already has open — but turning it into a string is one
  /// allocation per process per sample, and the sample loop's budget is zero (PRD §4). So the switch
  /// buys the string, not the read, and is set the same way: by somebody naming the column.
  /// </remarks>
  public bool ReadSupplementaryGroups { get; init; }

  /// <summary>
  /// Keep the <c>Cpus_allowed_list:</c> line of <c>status</c> as text (PRD §15).
  /// </summary>
  /// <remarks>
  /// Off for exactly the reason the group list is off: the line costs no extra read — it is in a
  /// file the sampler already has open — but turning it into a string is one allocation per process
  /// per sample against a budget of zero (PRD §4).
  /// </remarks>
  public bool ReadCpuAffinity { get; init; }

  /// <summary>
  /// Read the mitigation, umask, tracer and descriptor-table lines of <c>status</c> (PRD §21, §20).
  /// </summary>
  /// <remarks>
  /// Off, and for the same reason the group list and the affinity list are off: the lines cost no
  /// extra read — they are in a file the sampler already has open — but recognising them is not
  /// free. Reading all five unconditionally measured seven to eight milliseconds per thousand
  /// processes against a sample whose whole budget is twenty-five, and it was the room the labels
  /// took up in the parse loop rather than the comparisons themselves: fifty lines per process, six
  /// hundred processes, and five more labels of up to twenty-six bytes to carry through all of it.
  /// Moving them out of line recovered most of that and not all of it, so the rest is bought the
  /// way §5.4 says it should be — by somebody naming one of the columns.
  /// </remarks>
  public bool ReadSecurityStatus { get; init; }

  /// <summary>
  /// Read <c>cpu.stat</c> from each process's cgroup, for the throttling column (PRD §15, §38).
  /// </summary>
  /// <remarks>
  /// Off by default: it is a file outside <c>/proc</c> per <em>cgroup</em> per sample. Per cgroup
  /// rather than per process because the answer belongs to the group — a machine's six hundred
  /// processes live in a few dozen of them, and reading it once each is what makes the column
  /// affordable at all when somebody does ask for it (PRD §5.4).
  /// </remarks>
  public bool ReadCpuThrottling { get; init; }

  /// <summary>Where the running kernel publishes its own processes.</summary>
  public const string LiveProcRoot = "/proc";

  /// <summary>Where <c>/proc</c> is. A fixture directory in tests.</summary>
  public string ProcRoot { get; init; } = LiveProcRoot;

  /// <summary>
  /// Where <c>/sys</c> is. Separate from <see cref="ProcRoot"/> so a recorded machine can carry both
  /// and the host description is testable the same way the process list is (PRD §9.1).
  /// </summary>
  public string SysRoot { get; init; } = "/sys";

  /// <summary>Where this user's autostart entries live; null uses XDG_CONFIG_HOME or ~/.config.</summary>
  public string? AutostartUserDirectory { get; init; }

  /// <summary>The machine-wide autostart directories; null uses /etc/xdg/autostart.</summary>
  public IReadOnlyList<string>? AutostartSystemDirectories { get; init; }

  /// <summary>Overrides XDG_CURRENT_DESKTOP, so the desktop-specific rules are testable.</summary>
  public string? CurrentDesktop { get; init; }

  /// <summary>Where the login records live. /var/run is a symlink to /run on any current system.</summary>
  public string UtmpPath { get; init; } = "/run/utmp";

  /// <summary>Unit directories, least specific first; null uses the systemd defaults (PRD §41).</summary>
  public IReadOnlyList<string>? UnitDirectories { get; init; }

  /// <summary>The .wants directories that say which units start at boot.</summary>
  public IReadOnlyList<string>? WantsDirectories { get; init; }

  /// <summary>Where a service's processes live; null uses the whole /sys/fs/cgroup tree.</summary>
  public string? ServiceCgroupRoot { get; init; }

  /// <summary>
  /// Where the unified cgroup hierarchy is mounted, for reading a process's limits (PRD §38).
  /// </summary>
  /// <remarks>
  /// Its own option rather than derived from <see cref="ServiceCgroupRoot"/>: that one may be
  /// pointed at a subtree to narrow a service scan, and the limits of an arbitrary process live
  /// anywhere in the hierarchy.
  /// </remarks>
  public string CgroupRoot { get; init; } = "/sys/fs/cgroup";

  /// <summary>Where the password file is, for uid → name.</summary>
  public string PasswdPath { get; init; } = "/etc/passwd";

  /// <summary>Where the group file is, for gid → name (PRD §36).</summary>
  /// <remarks>
  /// Its own option rather than the password file's directory with the name changed, so that a
  /// recorded machine can carry one without the other — which is what a fixture that cares about
  /// group membership and not about logins actually looks like.
  /// </remarks>
  public string GroupPath { get; init; } = "/etc/group";

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

  /// <summary>
  /// Split each process's descriptors by what they point at — sockets, files, pipes (PRD §20).
  /// </summary>
  /// <remarks>
  /// Off, and the most expensive thing in this file. It is the descriptor scan of
  /// <see cref="CountFileDescriptors"/> plus a link to resolve for every descriptor found, which is
  /// a syscall and a string each. §20 says the per-type tallies must not move into the sample loop,
  /// and this is how they do not: nothing turns it on but somebody naming one of the three columns
  /// (PRD §5.4).
  /// </remarks>
  public bool CountDescriptorKinds { get; init; }

  /// <summary>
  /// Hash the image each process is running — SHA-256 and SHA-1 (PRD §21, §70).
  /// </summary>
  /// <remarks>
  /// Off, and the one read here whose cost is the size of a file rather than a syscall. Hashed once
  /// per image rather than once per process — three hundred processes of one runtime share one
  /// binary — and again only when that file is replaced underneath them, which is what makes it
  /// affordable for somebody who does ask for the column (PRD §5.4).
  /// </remarks>
  public bool ReadImageHashes { get; init; }

  /// <summary>
  /// Find out which package each running image belongs to (PRD §14).
  /// </summary>
  /// <remarks>
  /// Off, and expensive once rather than expensive per process: answering it means reading every
  /// installed package's file list — thirty megabytes of text across thirteen hundred packages on
  /// the machine this was written on — to build the index that turns a path into an owner. After
  /// that it is a dictionary lookup per image, and one file read per process for the sandboxed
  /// ones, which say who they are in their own way rather than in the package database (PRD §5.4).
  /// </remarks>
  public bool ReadPackageIdentity { get; init; }

  /// <summary>
  /// Check each running image against the digest its package recorded (PRD §70).
  /// </summary>
  /// <remarks>
  /// Off, and the dearest reading in this file: it is <see cref="ReadPackageIdentity"/> plus the
  /// hash of every distinct image on the machine, because a comparison against a recorded digest
  /// needs a digest to compare. The hash is taken from the same per-image cache the hash columns
  /// use, so asking for both costs no more than asking for either (PRD §5.4).
  /// </remarks>
  public bool ReadPackageVerification { get; init; }

  /// <summary>
  /// Work out which runtime is executing inside each process, from its module list (PRD §14).
  /// </summary>
  /// <remarks>
  /// Off: it reads <c>/proc/[pid]/maps</c>, which for a browser tab is tens of kilobytes the kernel
  /// formats one page at a time. Read once per process rather than once per sample — a process does
  /// not change what it is running — which is what makes it affordable when somebody does ask
  /// (PRD §5.4).
  /// </remarks>
  public bool ReadRuntime { get; init; }

  /// <summary>
  /// Ask the file system when each image was created (PRD §14).
  /// </summary>
  /// <remarks>
  /// Off: one <c>statx</c> per process, and unlike the rest of a <c>stat</c> it is not on any path
  /// already being read. Once per process, for the same reason as the runtime.
  /// </remarks>
  public bool ReadImageCreationTime { get; init; }

  /// <summary>
  /// Where the packaging databases are. A fixture directory in tests.
  /// </summary>
  /// <remarks>
  /// Its own root rather than derived from <see cref="ProcRoot"/>: a recorded <c>/proc</c> and a
  /// recorded package database are two captures of two different parts of a machine, and a test that
  /// wants one should not have to carry the other (PRD §9.1).
  /// </remarks>
  public string PackageDatabaseRoot { get; init; } = "/var/lib";

  /// <summary>
  /// Account for what each process is doing to the graphics adapters (PRD §19).
  /// </summary>
  /// <remarks>
  /// Off, and for the same reason the descriptor count is. The kernel's own per-client accounting
  /// lives in <c>/proc/[pid]/fdinfo</c>, one file per open descriptor, which is the same scan that
  /// cost 85 µs per process and had to leave the sample loop; NVIDIA's is one library call per card
  /// per sample, measured at 5-25 ms on an RTX A5000, against a whole-sample budget of 25 ms.
  /// Neither is affordable for a column nobody asked to see (PRD §5.4).
  /// </remarks>
  public bool ReadGpuUsage { get; init; }

  /// <summary>
  /// Count the sockets each process holds, for the connection columns (PRD §18, §40).
  /// </summary>
  /// <remarks>
  /// Off, and the most expensive of the three: joining a socket to a process means a
  /// <c>readlink</c> per open descriptor on the whole machine, which is the descriptor scan of
  /// <see cref="CountFileDescriptors"/> plus a syscall for every entry it finds. Measured at 201 µs
  /// per process against 45 µs for a sample that reads neither, on a machine with 820 processes —
  /// 165 ms of a sample whose whole budget is 25 ms. The four columns it
  /// fills are counts of endpoints and never of traffic — Linux attributes no bytes to a process
  /// without packet accounting or eBPF, and a column that summed what it could reach would be a
  /// model wearing a measurement's clothes (PRD §5.4, §72.3).
  /// </remarks>
  public bool ReadSocketCounts { get; init; }

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
