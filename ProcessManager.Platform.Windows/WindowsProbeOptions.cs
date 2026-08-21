namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Which of the expensive Windows readings this run is paying for (PRD §5.4).
/// </summary>
/// <remarks>
/// The same shape and the same rule as the Linux probe's options: a reading that costs a syscall per
/// process, a file per image or a walk of a machine-wide table happens only because somebody named
/// the column or the filter that needs it. Nothing here is on by default, and the default record is
/// the cheapest sample this probe can take.
/// </remarks>
public sealed record WindowsProbeOptions {

  /// <summary>
  /// Read the six per-process mitigation policies (PRD §21).
  /// </summary>
  /// <remarks>
  /// Off by default. Unlike the owner, the integrity level and the protection level — which all come
  /// off a handle the sampler opens once per process anyway — these need a second open with
  /// <c>PROCESS_QUERY_INFORMATION</c>, which is a stronger right, plus six calls. Once per process
  /// rather than once per sample, because a process's mitigation policy does not change under it
  /// except by its own hand.
  /// </remarks>
  public bool ReadMitigations { get; init; }

  /// <summary>
  /// Tally the machine's handle table by object type: events, semaphores, mutexes, sections and
  /// registry keys (PRD §20).
  /// </summary>
  /// <remarks>
  /// Off, and the most expensive thing in this file. There is no per-process handle query on
  /// Windows: the whole machine's table arrives in one call, which on a busy server is megabytes and
  /// a million rows. That makes it cheaper than the equivalent scan is on Linux and nowhere near
  /// free, so §20's rule stands — the per-type tallies stay out of the sample until a column names
  /// one of them.
  /// </remarks>
  public bool ReadObjectCounts { get; init; }

  /// <summary>
  /// Read each process's USER and GDI object counts (PRD §20, §39).
  /// </summary>
  /// <remarks>
  /// Off, and its own switch rather than sharing the one above, because the cost has a different
  /// shape: these are two calls per process per <em>sample</em> and cannot be cached, since the
  /// whole point of the column is that the number moves. The tallies above are one call for the
  /// whole machine.
  /// </remarks>
  public bool ReadGuiObjectCounts { get; init; }

  /// <summary>
  /// Ask the file system when each running image was created (PRD §14).
  /// </summary>
  /// <remarks>
  /// Off, and a stat per process per sample rather than a read of the file. NTFS has recorded a
  /// creation time for every file since it was written, so unlike the Linux half this answers for
  /// everything — which is a reason to have the column, not a reason to pay for it unasked.
  /// </remarks>
  public bool ReadImageCreationTime { get; init; }

  /// <summary>
  /// Read the page priority and the CPU sets of each process (PRD §15, §16).
  /// </summary>
  /// <remarks>
  /// Off, and one switch for two readings because they share the one thing that makes them
  /// expensive: an <c>OpenProcess</c> per process per <em>sample</em>. Not per process — both are
  /// settable while a process runs, and this program has a menu item that sets one of them, so an
  /// answer cached for a process's lifetime would be wrong the moment anybody changed it. That is
  /// what keeps them out of the identity cache and behind a switch (PRD §5.2, §5.4).
  /// <para>
  /// <see cref="ReadPowerThrottling"/> is the same shape and is its own switch: it answers §22's
  /// question rather than §15's and §16's, and a run that named one column should not pay for the
  /// other's call.
  /// </para>
  /// </remarks>
  public bool ReadProcessDetails { get; init; }

  /// <summary>
  /// Read each running image's version resource and subsystem (PRD §14).
  /// </summary>
  /// <remarks>
  /// Off, and the one reading here whose cost is the size of a file rather than a syscall. Read once
  /// per image rather than once per process — three hundred processes of one runtime share one
  /// binary — and again when that file is replaced underneath them.
  /// </remarks>
  public bool ReadImageVersions { get; init; }

  /// <summary>
  /// Check each running image's own Authenticode signature (PRD §21, §70).
  /// </summary>
  /// <remarks>
  /// Off, and the dearest reading in this file by a wide margin: it digests the whole image and then
  /// verifies a public-key signature over that digest. Once per image rather than once per process,
  /// out of the same cache and the same file read the version resource uses — so a machine running
  /// three hundred processes of one runtime pays for one binary, not three hundred.
  /// </remarks>
  public bool ReadSignatures { get; init; }

  /// <summary>
  /// Read each process's power-throttling state (PRD §22).
  /// </summary>
  /// <remarks>
  /// Off, and per <em>sample</em> rather than per process, which is what separates it from
  /// everything the identity resolver caches: an application may change its own throttling at any
  /// moment and a person may change it from Task Manager, so a cached answer would go on reporting
  /// the state something has since altered.
  /// </remarks>
  public bool ReadPowerThrottling { get; init; }

}
