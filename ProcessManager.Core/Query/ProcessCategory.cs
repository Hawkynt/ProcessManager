using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What kind of process a row is, which is what its colour means.
/// </summary>
/// <remarks>
/// Process Explorer and Process Hacker are readable at a glance because the colour answers "what am
/// I looking at" before you have read a single word. The categories here are the ones both tools
/// agree on and that both platforms can actually tell apart — deliberately not the full Process
/// Hacker palette, because several of its colours (packed, .NET, immersive) need information no
/// probe here collects, and a colour that is sometimes right is worse than no colour.
/// </remarks>
public enum ProcessCategory : byte {

  /// <summary>Nothing distinguishing: another user's ordinary process.</summary>
  Other = 0,

  /// <summary>Belongs to the user running this program.</summary>
  Own,

  /// <summary>Belongs to root / SYSTEM.</summary>
  System,

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

}

/// <summary>Sorts a process into a <see cref="ProcessCategory"/>.</summary>
public static class ProcessCategories {

  /// <summary>
  /// Classifies one row. <paramref name="isNew"/> comes from the delta, and wins over everything
  /// else — a process that just started is worth seeing whatever else it is.
  /// </summary>
  public static ProcessCategory Classify(in ProcessRecord process, int currentUserId, bool isNew) {
    if (isNew)
      return ProcessCategory.New;

    if (process.State == ProcessState.Zombie)
      return ProcessCategory.Zombie;

    if (process.IsSuspended || process.State == ProcessState.Stopped)
      return ProcessCategory.Suspended;

    // Root on Unix; on Windows the well-known SYSTEM account's relative id, and the two kernel
    // pseudo-processes that have no token at all.
    if (process.UserId == 0 || process.Pid is 0 or 4 && OperatingSystem.IsWindows())
      return ProcessCategory.System;

    if (IsService(in process))
      return ProcessCategory.Service;

    return currentUserId >= 0 && process.UserId == currentUserId
      ? ProcessCategory.Own
      : ProcessCategory.Other;
  }

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
    ProcessCategory.Service => "Service processes",
    ProcessCategory.Suspended => "Suspended",
    ProcessCategory.Zombie => "Zombie — exited, not yet reaped",
    ProcessCategory.New => "Started since the last refresh",
    ProcessCategory.Exited => "Ended since the last refresh",
    _ => "Other users' processes",
  };

}
