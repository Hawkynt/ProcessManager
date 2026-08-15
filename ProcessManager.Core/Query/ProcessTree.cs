using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Walks parent/child links over a snapshot, for the operations that act on a whole subtree.
/// </summary>
/// <remarks>
/// Lives in Core because both front-ends need it and both had written it themselves — the terminal
/// UI's "kill tree" and the CLI's <c>--kill --tree</c> were the same twenty lines twice, which is
/// two places for the ordering rule below to be got wrong.
/// </remarks>
public static class ProcessTree {

  /// <summary>
  /// A process and everything descended from it, deepest first.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The order is the point. Ending a parent first can leave its children reparented to init — on
  /// Linux they are handed to pid 1, on Windows they are simply orphaned — and they are then no
  /// longer findable as its descendants, so a "kill tree" that starts at the top kills the top and
  /// loses the rest. Deepest first, always.
  /// </para>
  /// <para>
  /// A cycle in the links cannot loop this: every process is visited at most once, tracked by
  /// identity rather than by pid.
  /// </para>
  /// </remarks>
  public static List<ProcessKey> DescendantsFirst(SystemSnapshot snapshot, int rootPid) {
    ArgumentNullException.ThrowIfNull(snapshot);

    var result = new List<ProcessKey>();
    var visited = new HashSet<ProcessKey>();
    Collect(rootPid, 0);
    result.Reverse();
    return result;

    void Collect(int pid, int depth) {
      // Bounded by the process count: a chain longer than that has revisited something, and the
      // visited set below would already have stopped it. This is the belt to that's braces.
      if (depth > snapshot.ProcessCount)
        return;

      var processes = snapshot.Processes;
      for (var i = 0; i < processes.Length; ++i) {
        if (processes[i].Pid != pid)
          continue;
        if (!visited.Add(processes[i].Key))
          return;

        result.Add(processes[i].Key);
        break;
      }

      for (var i = 0; i < processes.Length; ++i)
        if (processes[i].ParentPid == pid && processes[i].Pid != pid)
          Collect(processes[i].Pid, depth + 1);
    }
  }

  /// <summary>The identity of a pid in this snapshot, or <see cref="ProcessKey.None"/>.</summary>
  public static ProcessKey Find(SystemSnapshot snapshot, int pid) {
    ArgumentNullException.ThrowIfNull(snapshot);

    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process.Key;

    return ProcessKey.None;
  }

}
