using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>Which page of one process's detail a table describes (PRD §6.2, §11, §59).</summary>
public enum ProcessDetailPage : byte { Overview, Threads, Modules, Handles, Environment, Network }

/// <summary>
/// One page of a process's detail, as a table of already-formatted cells.
/// </summary>
/// <param name="Title">What the page is called.</param>
/// <param name="Headers">The column headings.</param>
/// <param name="Widths">
/// How wide each column wants to be where the space is fixed. Advisory: a terminal cell grid needs a
/// number before it draws anything, and a report that sizes its columns to what is actually in them
/// ignores these.
/// </param>
/// <param name="Rows">The cells, one array per row, in the order the headers are in.</param>
public readonly record struct DetailTable(
  string Title,
  IReadOnlyList<string> Headers,
  IReadOnlyList<int> Widths,
  IReadOnlyList<string[]> Rows
);

/// <summary>
/// What a process's detail pages contain, as data rather than as a drawing (PRD §58, §59).
/// </summary>
/// <remarks>
/// <para>
/// The terminal's detail view and <c>--process</c> read their rows from here, which is the same
/// argument that put the process fields in one registry (§5.1): a thread column that exists in one
/// front-end and not in another is exactly what §58 forbids, and two lists written a year apart drift
/// whether or not anybody meant them to.
/// </para>
/// <para>
/// Every cell is formatted through <see cref="Humanize"/>, so a counter nobody could read renders as
/// its reason rather than as a zero (PRD §3.4, §72.3).
/// </para>
/// </remarks>
public static class ProcessDetailTables {

  /// <summary>Builds one page by name, so a caller can dispatch on the argument it was given.</summary>
  public static DetailTable Build(
    ProcessDetailPage page,
    ISystemProbe probe,
    ProcessKey key,
    in ProcessRecord process,
    ProcessRules? rules = null
  ) => page switch {
    ProcessDetailPage.Threads => Threads(probe, key),
    ProcessDetailPage.Modules => Modules(probe, key),
    ProcessDetailPage.Handles => Handles(probe, key),
    ProcessDetailPage.Environment => Environment(probe, key),
    ProcessDetailPage.Network => Network(probe, key),
    _ => Overview(in process, rules),
  };

  /// <summary>
  /// Reads a page name the way somebody would type it.
  /// </summary>
  /// <remarks>
  /// The plural and the singular both, because both are what people write, and no abbreviations: a
  /// prefix match would make <c>m</c> mean either the modules or nothing at all depending on which
  /// page was added last.
  /// </remarks>
  public static bool TryParsePage(string? name, out ProcessDetailPage page) {
    page = ProcessDetailPage.Overview;
    switch (name?.ToLowerInvariant()) {
      case null or "" or "overview" or "summary": return true;
      case "threads" or "thread": page = ProcessDetailPage.Threads; return true;
      case "modules" or "module" or "libraries": page = ProcessDetailPage.Modules; return true;
      case "handles" or "handle" or "fds" or "descriptors": page = ProcessDetailPage.Handles; return true;
      case "environment" or "env": page = ProcessDetailPage.Environment; return true;
      case "network" or "net" or "sockets" or "connections": page = ProcessDetailPage.Network; return true;
      default: return false;
    }
  }

  /// <summary>Every page name, for a help line and for the message a mistyped one gets.</summary>
  public const string PageVocabulary = "overview, threads, modules, handles, environment, network";

  /// <summary>
  /// What one process is, at a glance.
  /// </summary>
  /// <param name="process">The process.</param>
  /// <param name="rules">
  /// What somebody has written about programs, if anything. A rule that recognises this one adds its
  /// note, category and expected publisher to the bottom of the page (PRD §66) — at the bottom
  /// because they are a person's words about the machine and everything above them is the machine's
  /// own, and the two should not be read as one another.
  /// </param>
  public static DetailTable Overview(in ProcessRecord process, ProcessRules? rules = null) {
    var rows = new List<string[]>();
    rows.Add(["name", process.Name]);
    rows.Add(["pid", process.Pid.ToString(CultureInfo.InvariantCulture)]);
    rows.Add(["parent", process.ParentPid.ToString(CultureInfo.InvariantCulture)]);
    rows.Add(["user", process.UserName ?? (process.UserId >= 0 ? process.UserId.ToString(CultureInfo.InvariantCulture) : "?")]);
    rows.Add(["state", Humanize.State(process.State)]);
    rows.Add(["session", process.SessionId.ToString(CultureInfo.InvariantCulture)]);
    rows.Add(["threads", process.ThreadCount.ToString(CultureInfo.InvariantCulture)]);
    rows.Add(["priority", $"{process.Priority} (nice {process.Nice})"]);
    rows.Add(["cpu time", Humanize.Duration(process.CpuTimeNs)]);
    rows.Add(["  user", Humanize.Duration(process.UserTimeNs)]);
    rows.Add(["  kernel", Humanize.Duration(process.KernelTimeNs)]);
    rows.Add(["private", Humanize.Bytes(process.PrivateBytes)]);
    rows.Add(["working set", Humanize.Bytes(process.WorkingSetBytes)]);
    rows.Add(["virtual", Humanize.Bytes(process.VirtualBytes)]);
    rows.Add(["swap", Humanize.Bytes(process.SwapBytes)]);
    rows.Add(["read", Humanize.Bytes(process.ReadBytes)]);
    rows.Add(["written", Humanize.Bytes(process.WriteBytes)]);
    rows.Add(["handles", Humanize.Count(process.HandleCount)]);
    rows.Add(["ctx switches", Humanize.Count(process.ContextSwitches)]);
    rows.Add(["started", process.StartTimeUtcTicks > 0
      ? new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
      : "—"]);
    rows.Add(["image", process.ImagePath ?? "—"]);
    rows.Add(["cgroup", process.ContainerPath ?? "—"]);
    rows.Add(["command", process.CommandLine ?? "—"]);

    AddWhatSomebodySaid(rows, process, rules);
    return new("Overview", ["Field", "Value"], [18, 100], rows);
  }

  /// <summary>
  /// The rows a rule contributes, or none (PRD §66).
  /// </summary>
  /// <remarks>
  /// The expected publisher is the interesting one and is deliberately shown as a comparison rather
  /// than as a value. "Expected: Mozilla Corporation" beside a signer field somebody has to scroll
  /// back to is two facts a reader has to join; saying whether they agree is the question they were
  /// asking. Where the signature has not been read it says so, because a publisher nobody checked and
  /// a publisher that did not match are opposite conclusions (§21, §70, §72.3).
  /// </remarks>
  private static void AddWhatSomebodySaid(List<string[]> rows, in ProcessRecord process, ProcessRules? rules) {
    if (rules is null || rules.For(process) is not { } rule)
      return;

    if (rule.Note is { Length: > 0 } note)
      rows.Add(["note", note]);

    if (rule.Category is { Length: > 0 } category)
      rows.Add(["category", category]);

    if (rule.ExpectedPublisher is { Length: > 0 } expected)
      rows.Add(["publisher", process.ImageSigner is { Length: > 0 } signer
        ? string.Equals(signer, expected, StringComparison.OrdinalIgnoreCase)
          ? $"{expected} — as expected"
          : $"{signer} — expected {expected}"
        : $"expected {expected}; the signature has not been read"]);

    if (rule.HasPreferences)
      rows.Add(["rule", rule.AppliesScheduling
        ? $"matched {ProcessRules.NameOf(rule.Match)} \"{rule.Pattern}\"; its scheduling preferences are applied"
        : $"matched {ProcessRules.NameOf(rule.Match)} \"{rule.Pattern}\"; its scheduling preferences are recorded only"]);
  }

  public static DetailTable Threads(ISystemProbe probe, ProcessKey key) {
    ArgumentNullException.ThrowIfNull(probe);

    var rows = new List<string[]>();
    foreach (var thread in probe.GetThreads(key))
      rows.Add([
        thread.Tid.ToString(CultureInfo.InvariantCulture),
        thread.Name ?? "—",
        Humanize.State(thread.State),
        Humanize.Timestamp(thread.StartTimeUtcTicks),
        Humanize.Duration(thread.CpuTimeNs),
        Humanize.Duration(thread.UserTimeNs),
        Humanize.Duration(thread.KernelTimeNs),
        Humanize.Count(thread.ContextSwitches),
        Humanize.Pair(thread.VoluntaryContextSwitches, thread.InvoluntaryContextSwitches),
        thread.LastCpu >= 0 ? thread.LastCpu.ToString(CultureInfo.InvariantCulture) : "—",
        thread.Priority.ToString(CultureInfo.InvariantCulture),
        thread.BasePriority?.ToString(CultureInfo.InvariantCulture) ?? "—",
        Humanize.SchedulingPolicy(thread.Policy),
        thread.Affinity ?? "—",
        Humanize.Count(thread.Cycles),
        // Ideal is beside CPU# on purpose: the pair is the question. A thread the scheduler prefers
        // on processor 2 that keeps running on 7 is a thread being bounced off its own cache, and
        // neither column says that alone.
        Humanize.Count(thread.IdealProcessor),
        Humanize.Address(thread.TebBase),
        // The wait reason last and widest: it is what somebody opened this page to find out.
        thread.WaitReason ?? Humanize.Address(thread.StartAddress),
      ]);

    return new(
      "Threads",
      ["TID", "Name", "S", "Started", "CPU time", "User", "Kernel", "Ctx", "Vol / invol",
       "CPU#", "Pri", "Base", "Policy", "Affinity", "Cycles", "Ideal", "TEB", "Waiting on"],
      [8, 16, 6, 20, 10, 9, 9, 9, 14, 5, 4, 5, 14, 12, 12, 6, 18, 28],
      rows
    );
  }

  public static DetailTable Modules(ISystemProbe probe, ProcessKey key) {
    ArgumentNullException.ThrowIfNull(probe);

    var rows = new List<string[]>();
    foreach (var module in probe.GetModules(key))
      rows.Add([
        Humanize.Address(module.BaseAddress),
        Humanize.Address(module.EndAddress),
        Humanize.Bytes(module.Size),
        Humanize.Bytes(module.ResidentBytes),
        module.Permissions.Length > 0 ? module.Permissions : "—",
        Humanize.ImageType(module.Type),
        module.Architecture ?? "—",
        module.Soname ?? "—",
        // The deleted marker rides on the path here, as it does in maps itself: there is no room for
        // a column that is empty on all but one row in a thousand.
        module.IsDeleted ? module.Path + " (deleted)" : module.Path,
      ]);

    return new(
      "Modules",
      ["Base", "End", "Size", "Resident", "Perm", "Type", "Arch", "SONAME", "Path"],
      [16, 16, 9, 9, 6, 14, 9, 22, 70],
      rows
    );
  }

  public static DetailTable Handles(ISystemProbe probe, ProcessKey key) {
    ArgumentNullException.ThrowIfNull(probe);

    var rows = new List<string[]>();
    foreach (var handle in probe.GetHandles(key))
      rows.Add([
        Humanize.ResourceKind(handle.Kind),
        handle.Handle.ToString(CultureInfo.InvariantCulture),
        handle.Access ?? "—",
        Humanize.Count(handle.Position),
        Humanize.Count(handle.Inode),
        DescriptorParser.DescribeFlags(handle.OpenFlags) ?? Humanize.Placeholder(handle.OpenFlags.Reason),
        handle.TargetPid.TryGetValue(out var target)
          ? $"{handle.Name} → pid {target}"
          : handle.Name ?? "<not named>",
      ]);

    return new(
      "Handles",
      ["Type", "FD", "Acc", "Position", "Inode", "Flags", "Name"],
      [14, 6, 4, 12, 12, 26, 70],
      rows
    );
  }

  public static DetailTable Environment(ISystemProbe probe, ProcessKey key) {
    ArgumentNullException.ThrowIfNull(probe);

    var rows = new List<string[]>();
    foreach (var (name, value) in probe.GetEnvironment(key))
      rows.Add([name, value]);

    return new("Environment", ["Variable", "Value"], [28, 90], rows);
  }

  public static DetailTable Network(ISystemProbe probe, ProcessKey key) {
    ArgumentNullException.ThrowIfNull(probe);

    // Named ports, as the command line names them and as ss does by default. Asked of the probe once
    // per fill, which is a dictionary lookup and not a read (PRD §40, §58).
    var services = probe.DescribePortNames();
    var rows = new List<string[]>();
    foreach (var connection in probe.GetConnections(key))
      rows.Add([
        connection.Protocol.ToString(),
        Humanize.SocketKindName(connection.Kind),
        Humanize.LocalEndpoint(connection, services, null),
        Humanize.RemoteEndpoint(connection, services, null),
        connection.State,
        Humanize.SocketUser(connection),
        connection.Interface ?? "—",
        Humanize.Bytes(connection.SendQueueBytes),
        Humanize.Bytes(connection.ReceiveQueueBytes),
        Humanize.Count(connection.Retransmits),
      ]);

    return new(
      "Network",
      ["Proto", "Type", "Local", "Remote", "State", "User", "If", "Send-Q", "Recv-Q", "Retx"],
      [6, 9, 24, 24, 12, 9, 8, 7, 7, 5],
      rows
    );
  }

}
