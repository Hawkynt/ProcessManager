namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Everything a row can be sorted by. The order here is the default column order in both front-ends,
/// so it is the Process-Explorer order rather than alphabetical.
/// </summary>
public enum ProcessColumn : byte {
  Name = 0,
  Pid,
  ParentPid,
  UserName,
  State,
  CpuPercent,
  PrivateBytes,
  WorkingSetBytes,
  VirtualBytes,
  ReadBytesPerSecond,
  WriteBytesPerSecond,
  HandleCount,
  ThreadCount,
  StartTime,
  Priority,
  SessionId,
  CommandLine,
}

public static class ProcessColumnExtensions {

  /// <summary>The header text, and what a `--sort=` argument accepts (case-insensitively).</summary>
  public static string ToHeader(this ProcessColumn column) => column switch {
    ProcessColumn.Name => "Process",
    ProcessColumn.Pid => "PID",
    ProcessColumn.ParentPid => "PPID",
    ProcessColumn.UserName => "User",
    ProcessColumn.State => "State",
    ProcessColumn.CpuPercent => "CPU",
    ProcessColumn.PrivateBytes => "Private",
    ProcessColumn.WorkingSetBytes => "Working set",
    ProcessColumn.VirtualBytes => "Virtual",
    ProcessColumn.ReadBytesPerSecond => "Read/s",
    ProcessColumn.WriteBytesPerSecond => "Write/s",
    ProcessColumn.HandleCount => "Handles",
    ProcessColumn.ThreadCount => "Threads",
    ProcessColumn.StartTime => "Started",
    ProcessColumn.Priority => "Priority",
    ProcessColumn.SessionId => "Session",
    ProcessColumn.CommandLine => "Command line",
    _ => column.ToString(),
  };

  /// <summary>
  /// Whether bigger should come first when the column is picked. Sorting by CPU ascending is not
  /// what anybody wants from one keypress, and sorting names descending is not either.
  /// </summary>
  public static bool PrefersDescending(this ProcessColumn column) => column switch {
    ProcessColumn.Name or ProcessColumn.UserName or ProcessColumn.CommandLine
      or ProcessColumn.State or ProcessColumn.Pid or ProcessColumn.ParentPid => false,
    _ => true,
  };

  public static bool TryParse(string? text, out ProcessColumn column) {
    column = ProcessColumn.CpuPercent;
    if (string.IsNullOrWhiteSpace(text))
      return false;

    switch (text.Trim().ToLowerInvariant()) {
      case "name" or "process" or "comm": column = ProcessColumn.Name; return true;
      case "pid": column = ProcessColumn.Pid; return true;
      case "ppid" or "parent": column = ProcessColumn.ParentPid; return true;
      case "user" or "owner": column = ProcessColumn.UserName; return true;
      case "state" or "status": column = ProcessColumn.State; return true;
      case "cpu": column = ProcessColumn.CpuPercent; return true;
      case "mem" or "memory" or "private" or "pss": column = ProcessColumn.PrivateBytes; return true;
      case "rss" or "ws" or "workingset": column = ProcessColumn.WorkingSetBytes; return true;
      case "virt" or "virtual": column = ProcessColumn.VirtualBytes; return true;
      case "read": column = ProcessColumn.ReadBytesPerSecond; return true;
      case "write": column = ProcessColumn.WriteBytesPerSecond; return true;
      case "handles" or "fds": column = ProcessColumn.HandleCount; return true;
      case "threads": column = ProcessColumn.ThreadCount; return true;
      case "start" or "started": column = ProcessColumn.StartTime; return true;
      case "prio" or "priority": column = ProcessColumn.Priority; return true;
      case "session": column = ProcessColumn.SessionId; return true;
      case "cmd" or "cmdline" or "commandline": column = ProcessColumn.CommandLine; return true;
      default: return false;
    }
  }

}
