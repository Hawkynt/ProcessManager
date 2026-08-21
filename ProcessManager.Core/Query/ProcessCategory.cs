using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What kind of process a row is, which is what its colour means.
/// </summary>
/// <remarks>
/// Process Explorer and Process Hacker are readable at a glance because the colour answers "what am
/// I looking at" before you have read a single word. The categories here are the ones both tools
/// agree on and that both platforms can actually <em>prove</em>. Several of Process Hacker's are
/// still missing — packed, unsigned, invalid signature, suspicious reputation — because nothing here
/// establishes them, and a colour that is sometimes right is worse than no colour.
/// </remarks>
public enum ProcessCategory : byte {

  /// <summary>Nothing distinguishing: another user's ordinary process.</summary>
  Other = 0,

  /// <summary>Belongs to the user running this program.</summary>
  Own,

  /// <summary>Belongs to root / SYSTEM.</summary>
  System,

  /// <summary>
  /// Started by an ordinary user and now running as root: a setuid binary, or anything else that
  /// gained privilege after it was launched.
  /// </summary>
  /// <remarks>
  /// Distinct from <see cref="System"/>, which is a process root started. This one is a process
  /// <em>somebody else</em> started that is root now, which is the more interesting of the two and
  /// the one worth a colour of its own.
  /// </remarks>
  Elevated,

  /// <summary>A background service — a systemd unit's process, or a Windows service host.</summary>
  Service,

  /// <summary>Stopped: SIGSTOP on Unix, every thread suspended on Windows.</summary>
  Suspended,

  /// <summary>Exited but not reaped. It holds a slot and nothing else.</summary>
  Zombie,

  /// <summary>Appeared since the previous sample — the green flash.</summary>
  New,

  /// <summary>Gone since the previous sample — the red flash.</summary>
  Exited,

  /// <summary>
  /// Running an image that is no longer on disk where it was loaded from: replaced by an upgrade, or
  /// deleted outright.
  /// </summary>
  /// <remarks>
  /// The kernel appends <c>" (deleted)"</c> to the <c>exe</c> link of such a process, and that marker
  /// is the whole of the evidence — no hash, no timestamp comparison, no watch. It is a fact the
  /// kernel states rather than one this program infers, which is why this category exists and
  /// "unsigned" does not.
  /// </remarks>
  ImageReplaced,

  /// <summary>
  /// A sandboxed application: a Flatpak, a snap or an AppImage.
  /// </summary>
  /// <remarks>
  /// Deliberately not "some package owns this file" — <c>pacman</c> and <c>dpkg</c> own nearly every
  /// binary on a machine, and a colour that paints nine rows in ten distinguishes nothing. What is
  /// worth a colour is the application that brought its own filesystem with it, which is the same
  /// thing Windows means by a packaged application.
  /// </remarks>
  Packaged,

  /// <summary>A virtual machine or an interpreter is mapped into it: .NET, a JVM, CPython, Wine.</summary>
  /// <remarks>
  /// From the module list, never from the name. A program called <c>java</c> may be a shell script and
  /// a program called anything at all may have a runtime inside it (PRD §5.3).
  /// </remarks>
  ManagedRuntime,

}

/// <summary>Sorts a process into a <see cref="ProcessCategory"/>.</summary>
public static class ProcessCategories {

  /// <summary>
  /// Classifies one row. <paramref name="isNew"/> comes from the delta, and wins over everything
  /// else — a process that just started is worth seeing whatever else it is.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The order below is the whole design, because a row has one colour and most processes qualify for
  /// several. Two rules settle it.
  /// </para>
  /// <para>
  /// <b>The transient beats the permanent.</b> Started, ended, stopped and running-a-deleted-image are
  /// all things that were not true an hour ago and will not be true an hour from now; being root, or
  /// yours, or a service, is true for the process's whole life and is in a column besides. So
  /// <see cref="ImageReplaced"/> sits above <see cref="System"/> rather than below it — a root daemon
  /// still holding a replaced <c>libc</c> is the exact row somebody is hunting after an upgrade, and
  /// painting it blue like every other daemon hides it.
  /// </para>
  /// <para>
  /// <b>The two identity colours only ever replace "nothing distinguishing".</b>
  /// <see cref="Packaged"/> and <see cref="ManagedRuntime"/> are tested last, after privilege and
  /// service membership, so no colour that already meant something loses its row to them: a .NET
  /// service stays a service. They take the place of <see cref="Own"/> and <see cref="Other"/>, which
  /// is where the palette had nothing to say.
  /// </para>
  /// </remarks>
  public static ProcessCategory Classify(in ProcessRecord process, int currentUserId, bool isNew) {
    if (isNew)
      return ProcessCategory.New;

    if (process.State == ProcessState.Zombie)
      return ProcessCategory.Zombie;

    if (process.IsSuspended || process.State == ProcessState.Stopped)
      return ProcessCategory.Suspended;

    if (IsImageReplaced(in process))
      return ProcessCategory.ImageReplaced;

    // Root on Unix; on Windows the well-known SYSTEM account's relative id, and the two kernel
    // pseudo-processes that have no token at all.
    if (process.UserId == 0 || process.Pid is 0 or 4 && OperatingSystem.IsWindows())
      return ProcessCategory.System;

    // Effective uid 0 with a real uid that is not: privilege was gained rather than granted at
    // launch. Only claimed when the probe actually read it — an unknown is not a "no" (PRD §72.3).
    if (process.IsElevated.HasValue && process.IsElevated.Value != 0)
      return ProcessCategory.Elevated;

    if (IsService(in process))
      return ProcessCategory.Service;

    if (IsSandboxed(process.Package.Source))
      return ProcessCategory.Packaged;

    if (IsManaged(process.Runtime))
      return ProcessCategory.ManagedRuntime;

    return currentUserId >= 0 && process.UserId == currentUserId
      ? ProcessCategory.Own
      : ProcessCategory.Other;
  }

  /// <summary>
  /// Whether the file this process was loaded from is gone from the path it was loaded from.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>readlink("/proc/[pid]/exe")</c> answers with the path and, where the inode has been unlinked,
  /// the suffix <c>" (deleted)"</c>. That is the kernel saying so; nothing here is inferred, which is
  /// what separates this category from the signature ones §23 leaves off.
  /// </para>
  /// <para>
  /// <b>It under-reports and never over-reports.</b> The link is read once per process, so a process
  /// whose image is replaced while this program is already watching it keeps the path it had — the
  /// mark appears the next time the table is built from scratch. The reverse cannot happen: a path
  /// carrying the marker was never a live file. An under-reported colour costs a reader a discovery;
  /// an over-reported one costs them their trust in every other colour.
  /// </para>
  /// <para>
  /// A path may legitimately end in those characters — a file really can be called
  /// <c>rm (deleted)</c> — and such a process would be marked wrongly. The kernel offers no way to
  /// tell the two apart through this interface, which is the same ambiguity <c>/proc/[pid]/maps</c>
  /// carries and the same one every reader of it lives with.
  /// </para>
  /// </remarks>
  private static bool IsImageReplaced(in ProcessRecord process)
    => process.ImagePath is { } path && path.EndsWith(" (deleted)", StringComparison.Ordinal);

  /// <summary>
  /// Whether the image came from an application bundle rather than from the machine's own packages.
  /// </summary>
  /// <remarks>
  /// <see cref="PackageSource.Unknown"/> is not a "no": it means nobody looked, and the identity is
  /// opt-in because answering it costs a read of every installed package's file list (PRD §5.4). So a
  /// table with no package column paints no row this colour, which is the same discipline the cell
  /// marks follow — an unread field is never marked, in either direction.
  /// </remarks>
  private static bool IsSandboxed(PackageSource source)
    => source is PackageSource.Flatpak or PackageSource.Snap or PackageSource.AppImage;

  /// <summary>
  /// Whether a runtime is mapped into the process.
  /// </summary>
  /// <remarks>
  /// <see cref="ProcessRuntime.Native"/> is a finding — every module was looked at and none was a
  /// runtime — and <see cref="ProcessRuntime.Unknown"/> is the absence of one. Neither is coloured,
  /// and they are checked separately rather than as "anything but Unknown", because collapsing them
  /// is the defect the enum's own two values exist to prevent (PRD §72.3).
  /// </remarks>
  private static bool IsManaged(ProcessRuntime runtime)
    => runtime is not (ProcessRuntime.Unknown or ProcessRuntime.Native);

  /// <summary>
  /// Whether a process looks like a background service.
  /// </summary>
  /// <remarks>
  /// Read off the cgroup path on Linux — systemd puts a unit's processes under <c>system.slice</c> —
  /// and off the image name on Windows, where a service almost always lives in <c>svchost.exe</c> or
  /// under <c>services.exe</c>. Neither is authoritative: the honest answer needs the service control
  /// manager on Windows and a D-Bus call to systemd on Linux, and both are dependencies this program
  /// does not otherwise have (PRD §13, open question 4). A row miscoloured here is miscoloured, not
  /// wrong about anything it says in words.
  /// </remarks>
  private static bool IsService(in ProcessRecord process) {
    if (process.ContainerPath is { } cgroup)
      return cgroup.Contains("system.slice", StringComparison.Ordinal)
          || cgroup.Contains(".service", StringComparison.Ordinal);

    return process.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)
        || process.Name.Equals("services.exe", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>What the colour means, for the legend.</summary>
  public static string Describe(ProcessCategory category) => category switch {
    ProcessCategory.Own => "Your processes",
    ProcessCategory.System => "System processes (root / SYSTEM)",
    ProcessCategory.Elevated => "Elevated — started by a user, running as root",
    ProcessCategory.Service => "Service processes",
    ProcessCategory.Suspended => "Suspended",
    ProcessCategory.Zombie => "Zombie — exited, not yet reaped",
    ProcessCategory.New => "Started since the last refresh",
    ProcessCategory.Exited => "Ended since the last refresh",
    ProcessCategory.ImageReplaced => "Running an image that is no longer on disk — restart it",
    ProcessCategory.Packaged => "Packaged application — a Flatpak, a snap or an AppImage",
    ProcessCategory.ManagedRuntime => "A runtime is mapped into it — .NET, a JVM, Python, Wine",
    _ => "Other users' processes",
  };

}
