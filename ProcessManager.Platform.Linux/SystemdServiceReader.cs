using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// systemd services, read from the files systemd itself reads (PRD §41).
/// </summary>
/// <remarks>
/// <para>
/// No D-Bus and no <c>systemctl</c>. Everything the columns need is on disk: the unit files say what
/// a service is, the <c>*.wants</c> symlinks say whether it starts at boot, and the cgroup says
/// whether it is running and what its main process is. A D-Bus client would be a substantial piece
/// of machinery, and spawning a process to read state is the kind of thing that stops working on the
/// machine you most need it on.
/// </para>
/// <para>
/// What this cannot see, and does not pretend to: sub-states finer than running or not, dependency
/// graphs, failure counts, and anything about a unit that has never been written to disk.
/// </para>
/// </remarks>
internal static class SystemdServiceReader {

  /// <summary>
  /// Reads every service unit.
  /// </summary>
  /// <param name="unitDirectories">
  /// In precedence order, least specific first: the vendor's <c>/usr/lib/systemd/system</c> then the
  /// administrator's <c>/etc/systemd/system</c>. A file in a later directory replaces the earlier
  /// one of the same name entirely, which is how an administrator overrides a packaged unit.
  /// </param>
  /// <param name="cgroupRoot">Usually <c>/sys/fs/cgroup/system.slice</c>.</param>
  public static IReadOnlyList<ServiceRecord> Read(
    IReadOnlyList<string> unitDirectories,
    IReadOnlyList<string> wantsDirectories,
    string cgroupRoot
  ) {
    var byName = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var directory in unitDirectories)
      foreach (var file in Enumerate(directory, "*.service"))
        byName[Path.GetFileName(file)] = file;

    var enabled = new HashSet<string>(StringComparer.Ordinal);
    foreach (var directory in wantsDirectories)
      foreach (var link in Enumerate(directory, "*.service"))
        enabled.Add(Path.GetFileName(link));

    // What is running comes from the cgroup tree rather than from the unit files, because the two
    // answer different questions and neither is a superset. A unit file says a service exists; a
    // cgroup says one is running — including instances of a template, whose file on disk is named
    // for the template rather than for them.
    var running = ReadRunning(cgroupRoot);

    var services = new List<ServiceRecord>(byName.Count);
    foreach (var (name, path) in byName)
      services.Add(Describe(name, path, enabled, running));

    foreach (var (name, pid) in running) {
      if (byName.ContainsKey(name))
        continue;

      // A running unit with no file of its own is an instance: user@1000.service is started from
      // user@.service. Listing it under the template's name would merge every user's session into
      // one row, and leaving it out loses a service that is genuinely running.
      var template = TemplateOf(name);
      var path = template is not null && byName.TryGetValue(template, out var found) ? found : string.Empty;
      var (description, command, restart) = path.Length > 0 ? ParseUnit(path) : (null, null, null);
      services.Add(new(name, description, ServiceState.Running, enabled.Contains(name) ? true : null, false, pid, command, path, restart));
    }

    services.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
    return services;
  }

  /// <summary>
  /// <c>foo@bar.service</c> is an instance of <c>foo@.service</c>.
  /// </summary>
  private static string? TemplateOf(string name) {
    var at = name.IndexOf('@', StringComparison.Ordinal);
    if (at < 0)
      return null;

    var dot = name.LastIndexOf('.');
    return dot > at ? string.Concat(name.AsSpan(0, at + 1), name.AsSpan(dot)) : null;
  }

  /// <summary>
  /// Every service with processes, found by walking the cgroup tree.
  /// </summary>
  /// <remarks>
  /// Recursive, because services are not all directly under the slice: systemd puts some of them in
  /// a sub-slice of their own — cups lives at <c>system.slice/system-cups.slice/cups.service</c> —
  /// and a flat listing reports those as stopped while they are plainly running.
  /// </remarks>
  private static Dictionary<string, int> ReadRunning(string cgroupRoot) {
    var running = new Dictionary<string, int>(StringComparer.Ordinal);
    Walk(cgroupRoot, running, depth: 0);
    return running;
  }

  private static void Walk(string directory, Dictionary<string, int> running, int depth) {
    // The tree is shallow by construction; the bound is only there so a symlink loop cannot hang the
    // program that is supposed to help diagnose one.
    if (depth > 8 || !Directory.Exists(directory))
      return;

    IEnumerable<string> children;
    try {
      children = Directory.EnumerateDirectories(directory);
    } catch (IOException) {
      return;
    } catch (UnauthorizedAccessException) {
      return;
    }

    foreach (var child in children) {
      var name = Path.GetFileName(child);
      if (name.EndsWith(".service", StringComparison.Ordinal)) {
        // Its own processes first, then any in a child cgroup: systemd-udevd keeps its workers in a
        // nested group and its own cgroup.procs is empty, so reading only that reports a service
        // with a hundred running processes as stopped.
        var pid = FirstProcess(Path.Combine(child, "cgroup.procs"));
        if (pid == 0)
          pid = FirstProcessBelow(child, depth + 1);

        if (pid > 0)
          running[name] = pid;

        continue;
      }

      // Anything that is not itself a service may hold one: a slice, or a user's own tree.
      Walk(child, running, depth + 1);
    }
  }

  /// <summary>The first process in any cgroup below this one.</summary>
  private static int FirstProcessBelow(string directory, int depth) {
    if (depth > 8)
      return 0;

    IEnumerable<string> children;
    try {
      children = Directory.EnumerateDirectories(directory);
    } catch (IOException) {
      return 0;
    } catch (UnauthorizedAccessException) {
      return 0;
    }

    foreach (var child in children) {
      var pid = FirstProcess(Path.Combine(child, "cgroup.procs"));
      if (pid > 0)
        return pid;

      pid = FirstProcessBelow(child, depth + 1);
      if (pid > 0)
        return pid;
    }

    return 0;
  }

  private static int FirstProcess(string procs) {
    foreach (var line in ReadLines(procs))
      if (int.TryParse(line.Trim(), out var pid) && pid > 0)
        // The oldest process in the cgroup, which for a service is the one it started with —
        // systemd's own MainPID in every ordinary case.
        return pid;

    return 0;
  }

  private static ServiceRecord Describe(
    string name,
    string path,
    HashSet<string> enabled,
    Dictionary<string, int> running
  ) {
    // A unit masked by the administrator is a symlink to /dev/null. It can never run, whatever the
    // rest of the configuration says, and reporting it as merely disabled hides a decision somebody
    // made and has probably forgotten.
    var masked = IsMasked(path);
    var (description, command, restart) = masked ? (null, null, null) : ParseUnit(path);
    var mainPid = running.TryGetValue(name, out var pid) ? pid : 0;
    var state = mainPid > 0 ? ServiceState.Running : ServiceState.Inactive;

    return new(
      name,
      description,
      state,
      // A unit nothing wants is not necessarily disabled: socket- and timer-activated units are
      // started on demand and appear in no wants directory at all. Saying "no" would be a claim
      // about configuration that was never made (PRD §72.3).
      masked ? false : enabled.Contains(name) ? true : null,
      masked,
      mainPid,
      command,
      path,
      restart
    );
  }

  private static bool IsMasked(string path) {
    try {
      var info = new FileInfo(path);
      return info.LinkTarget is { } target
        && target.EndsWith("/dev/null", StringComparison.Ordinal);
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }
  }

  /// <summary>
  /// Reads the three fields the columns need, from the two sections that hold them.
  /// </summary>
  /// <remarks>
  /// Section-aware because it has to be: <c>Description</c> lives in <c>[Unit]</c> and
  /// <c>ExecStart</c> in <c>[Service]</c>, and a unit file may carry an <c>[Install]</c> section
  /// after both. Reading keys without regard to section picks up whichever came first.
  /// </remarks>
  private static (string? Description, string? Command, string? Restart) ParseUnit(string path) {
    string? description = null, command = null, restart = null;
    var section = string.Empty;

    foreach (var raw in ReadLines(path)) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] is '#' or ';')
        continue;

      if (line[0] == '[') {
        section = line.Trim('[', ']');
        continue;
      }

      var separator = line.IndexOf('=', StringComparison.Ordinal);
      if (separator <= 0)
        continue;

      var key = line[..separator].Trim();
      var value = line[(separator + 1)..].Trim();
      switch (section) {
        case "Unit" when key == "Description":
          description ??= value;
          break;

        // A unit may set ExecStart more than once; the first is the one that starts the service,
        // and the rest are additional commands for a Type=oneshot unit.
        case "Service" when key == "ExecStart":
          command ??= value;
          break;

        case "Service" when key == "Restart":
          restart ??= value;
          break;

        default: break;
      }
    }

    return (description, command, restart);
  }

  private static IEnumerable<string> Enumerate(string directory, string pattern) {
    if (!Directory.Exists(directory))
      return [];

    try {
      return Directory.EnumerateFiles(directory, pattern);
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    }
  }

  private static string[] ReadLines(string path) {
    try {
      return File.Exists(path) ? File.ReadAllLines(path) : [];
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    }
  }

}
