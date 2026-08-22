using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// systemd services, read from the files systemd itself reads (PRD §41).
/// </summary>
/// <remarks>
/// <para>
/// No D-Bus and no <c>systemctl</c>. Everything the columns need is on disk: the unit files say what
/// a service is, the <c>*.wants</c> symlinks say whether it starts at boot, the cgroup says whether it
/// is running and what its main process is, and <c>/run/systemd/units</c> — which is the manager's
/// own runtime directory, not an interface to it — says when the current invocation of a unit began.
/// A D-Bus client would be a substantial piece of machinery, and spawning a process to read state is
/// the kind of thing that stops working on the machine you most need it on.
/// </para>
/// <para>
/// What this cannot see, and does not pretend to: the failure state and the control PID, which the
/// manager keeps in its own memory and writes nowhere, and the sub-states that go with them —
/// <c>failed</c>, <c>auto-restart</c>, <c>start-pre</c>. A unit in any of those looks like
/// <see cref="ServiceSubState.Dead"/> from out here, which is why the enum has three members and not
/// systemd's dozen.
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
  /// <param name="runtimeDirectory">
  /// Usually <c>/run/systemd/units</c>. Absent on a machine whose init is not systemd, and absent
  /// inside a container that was handed a unit tree and no manager — in both cases every unit's
  /// activation time is a refusal carrying the reason rather than a zero (PRD §72.3).
  /// </param>
  public static IReadOnlyList<ServiceRecord> Read(
    IReadOnlyList<string> unitDirectories,
    IReadOnlyList<string> wantsDirectories,
    string cgroupRoot,
    string runtimeDirectory
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
    var invocations = ReadInvocations(runtimeDirectory);
    var linked = ReadLinkDependencies(unitDirectories);
    // An empty runtime directory and a missing one are the two answers this must not merge: the first
    // says no unit is active, the second says nobody here can tell (PRD §72.3).
    var manager = runtimeDirectory.Length > 0 && Directory.Exists(runtimeDirectory);

    var services = new List<ServiceRecord>(byName.Count);
    foreach (var (name, path) in byName)
      services.Add(Describe(name, path, unitDirectories, enabled, running, invocations, linked, manager));

    // A unit with no file of its own is an instance: user@1000.service is started from user@.service.
    // Listing it under the template's name would merge every user's session into one row, and leaving
    // it out loses a service that is genuinely there.
    //
    // Both maps are walked, not just the cgroup one. An instance that finished and stayed active —
    // systemd-pcrlogin@1000.service, user-runtime-dir@1000.service — has an invocation and no
    // processes, so a scan of the cgroup tree alone missed six of the fifty-seven active units on this
    // machine, all of them instances.
    var extra = new HashSet<string>(StringComparer.Ordinal);
    foreach (var name in running.Keys)
      extra.Add(name);

    foreach (var name in invocations.Keys)
      extra.Add(name);

    foreach (var name in extra) {
      if (byName.ContainsKey(name))
        continue;

      var template = TemplateOf(name);
      var path = template is not null && byName.TryGetValue(template, out var found) ? found : string.Empty;
      services.Add(Describe(name, path, unitDirectories, enabled, running, invocations, linked, manager));
    }

    Reverse(services, linked);
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

  /// <summary>
  /// When the manager's current invocation of each unit began.
  /// </summary>
  /// <remarks>
  /// <para>
  /// systemd writes one symlink per running invocation into <c>/run/systemd/units</c>, named
  /// <c>invocation:</c><em>unit</em> and pointing at the invocation id. We want neither the name nor
  /// the target but the symlink's own modification time, which is the moment the manager created it —
  /// the same instant it reports as <c>ActiveEnterTimestamp</c>, checked against
  /// <c>systemctl show</c> on three units of three different types.
  /// </para>
  /// <para>
  /// Its <em>presence</em> is worth as much as its time. A <c>Type=oneshot</c> unit with
  /// <c>RemainAfterExit=yes</c> has no processes and is still active; without this file it is
  /// indistinguishable from one that never ran, and this reader called both of them inactive.
  /// </para>
  /// </remarks>
  private static Dictionary<string, DateTime> ReadInvocations(string runtimeDirectory) {
    var invocations = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    if (runtimeDirectory.Length == 0 || !Directory.Exists(runtimeDirectory))
      return invocations;

    const string Prefix = "invocation:";
    IEnumerable<string> entries;
    try {
      entries = Directory.EnumerateFileSystemEntries(runtimeDirectory, Prefix + "*.service");
    } catch (IOException) {
      return invocations;
    } catch (UnauthorizedAccessException) {
      return invocations;
    }

    foreach (var entry in entries) {
      var name = Path.GetFileName(entry);
      if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        continue;

      try {
        // The link's own time and not whatever it points at: the target is an invocation id rather
        // than a path, so there is nothing at the other end of it to have a time at all.
        invocations[name[Prefix.Length..]] = new FileInfo(entry).LastWriteTimeUtc;
      } catch (IOException) {
        // A unit that stopped between the listing and the stat. Not an entry rather than a wrong one.
      } catch (UnauthorizedAccessException) {
      }
    }

    return invocations;
  }

  /// <summary>
  /// The dependencies expressed as symlinks rather than as settings (PRD §41).
  /// </summary>
  /// <remarks>
  /// <c>multi-user.target.wants/sshd.service</c> is <c>multi-user.target</c> wanting
  /// <c>sshd.service</c>, and it is how nearly every service on a machine is actually pulled in —
  /// <c>systemctl enable</c> writes one of these and nothing else. A dependency list built only from
  /// <c>Wants=</c> lines in unit files would show most services as wanted by nothing at all.
  /// </remarks>
  /// <returns>The edges each owner declares, keyed by the owner — usually a target rather than a service.</returns>
  private static Dictionary<string, List<UnitDependency>> ReadLinkDependencies(IReadOnlyList<string> unitDirectories) {
    var edges = new Dictionary<string, List<UnitDependency>>(StringComparer.Ordinal);
    foreach (var directory in unitDirectories) {
      if (!Directory.Exists(directory))
        continue;

      IEnumerable<string> children;
      try {
        children = Directory.EnumerateDirectories(directory);
      } catch (IOException) {
        continue;
      } catch (UnauthorizedAccessException) {
        continue;
      }

      foreach (var child in children) {
        var folder = Path.GetFileName(child);
        var relation = folder.EndsWith(".wants", StringComparison.Ordinal) ? UnitRelation.Wants
          : folder.EndsWith(".requires", StringComparison.Ordinal) ? UnitRelation.Requires
          : folder.EndsWith(".upholds", StringComparison.Ordinal) ? UnitRelation.Upholds
          : (UnitRelation?)null;

        if (relation is not { } kind)
          continue;

        var owner = folder[..folder.LastIndexOf('.')];
        foreach (var link in Enumerate(child, "*")) {
          if (!edges.TryGetValue(owner, out var list))
            edges[owner] = list = [];

          list.Add(new(kind, Path.GetFileName(link), folder));
        }
      }
    }

    return edges;
  }

  /// <summary>
  /// Fills in every record's <see cref="ServiceRecord.Dependents"/> by walking the edges backwards.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Computed here rather than asked of each unit, because a dependent is not a property of the unit
  /// that has it: nothing in <c>sshd.service</c> mentions the target that pulls it in, and the only
  /// way to the answer is to have read everything else first.
  /// </para>
  /// <para>
  /// The symlink edges are walked separately from the records', and that is the whole reason this
  /// takes two arguments. Their owner is nearly always a <em>target</em> —
  /// <c>multi-user.target.wants/sshd.service</c> — and a target is not a service, so it has no record
  /// in this list to have been read off. Reversing only what the records carry left every service on
  /// the machine looking as though nothing wanted it.
  /// </para>
  /// </remarks>
  private static void Reverse(List<ServiceRecord> services, Dictionary<string, List<UnitDependency>> linked) {
    var dependents = new Dictionary<string, List<UnitDependency>>(StringComparer.Ordinal);

    foreach (var service in services)
      foreach (var edge in service.Dependencies) {
        // Only the ones the unit file itself declared. The symlink edges were copied onto the record
        // that owns them and are reversed below from the one list that holds all of them, owners
        // without records included.
        if (!string.Equals(edge.Source, _FromUnitFile, StringComparison.Ordinal))
          continue;

        Add(edge.Unit, new(edge.Relation, service.Name, edge.Source));
      }

    foreach (var (owner, own) in linked)
      foreach (var edge in own)
        Add(edge.Unit, new(edge.Relation, owner, edge.Source));

    for (var i = 0; i < services.Count; ++i)
      if (dependents.TryGetValue(services[i].Name, out var list)) {
        list.Sort(static (left, right) => string.Compare(left.Unit, right.Unit, StringComparison.Ordinal));
        services[i] = services[i] with { Dependents = list };
      }

    void Add(string unit, UnitDependency dependent) {
      if (!dependents.TryGetValue(unit, out var list))
        dependents[unit] = list = [];

      list.Add(dependent);
    }
  }

  /// <summary>What a dependency read out of the unit file itself records as its source.</summary>
  private const string _FromUnitFile = "the unit file";

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
    IReadOnlyList<string> unitDirectories,
    HashSet<string> enabled,
    Dictionary<string, int> running,
    Dictionary<string, DateTime> invocations,
    Dictionary<string, List<UnitDependency>> linked,
    bool manager
  ) {
    // A unit masked by the administrator is a symlink to /dev/null. It can never run, whatever the
    // rest of the configuration says, and reporting it as merely disabled hides a decision somebody
    // made and has probably forgotten.
    var masked = path.Length > 0 && IsMasked(path);
    var unit = masked || path.Length == 0 ? null : ReadUnit(name, path, unitDirectories);
    var mainPid = running.TryGetValue(name, out var pid) ? pid : 0;
    var activated = invocations.TryGetValue(name, out var when) ? when : (DateTime?)null;

    var state = mainPid > 0
      ? ServiceState.Running
      : activated is not null
        ? ServiceState.Active
        : ServiceState.Inactive;

    var command = unit?.First("Service", "ExecStart");
    var (prefixes, executable, arguments) = command is { Length: > 0 }
      ? UnitFile.SplitCommand(command)
      : (string.Empty, string.Empty, string.Empty);

    var dependencies = unit?.Dependencies(_FromUnitFile) ?? [];
    // The symlinks this unit's own .wants directory holds. Looked up rather than filtered: the whole
    // list is a few thousand edges on an ordinary machine, and scanning it once per unit made this a
    // walk of every symlink on the system for every unit on the system.
    if (linked.TryGetValue(name, out var own))
      dependencies.AddRange(own);

    return new ServiceRecord(
      name,
      unit?.Last("Unit", "Description"),
      state,
      // A unit nothing wants is not necessarily disabled: socket- and timer-activated units are
      // started on demand and appear in no wants directory at all. Saying "no" would be a claim
      // about configuration that was never made (PRD §72.3).
      masked ? false : enabled.Contains(name) ? true : null,
      masked,
      mainPid,
      command,
      path,
      unit?.Last("Service", "Restart")
    ) {
      LoadState = masked ? ServiceLoadState.Masked
        : path.Length > 0 ? ServiceLoadState.Loaded
        : ServiceLoadState.Transient,
      SubState = mainPid > 0 ? ServiceSubState.Running
        : activated is not null ? ServiceSubState.Exited
        : ServiceSubState.Dead,
      Type = unit?.ServiceType(),
      Account = unit?.Last("Service", "User"),
      Executable = executable.Length > 0 ? executable : null,
      Arguments = arguments.Length > 0 ? arguments : null,
      CommandPrefixes = prefixes.Length > 0 ? prefixes : null,
      ActivatedUtcTicks = activated is { } moment
        ? Counter.Of(moment.Ticks)
        : Counter.Unknown(
            manager
              // The manager is here and holds no invocation of this unit: it is not running, which is
              // an answer rather than a gap.
              ? UnknownReason.SourceGone
              // Nothing writes that directory here, so nobody can say when anything started.
              : UnknownReason.NotSupportedOnPlatform
          ),
      Dependencies = dependencies,
    };
  }

  private static UnitFile? ReadUnit(string name, string path, IReadOnlyList<string> unitDirectories) {
    var lines = ReadLines(path);
    if (lines.Length == 0 && !File.Exists(path))
      return null;

    var unit = UnitFile.Parse(lines);

    // The drop-ins, in the same precedence order as the unit files themselves. An instance takes both
    // its own and its template's — systemd applies user@.service.d to user@1000.service — and in that
    // order, so the instance's own has the last word.
    var template = TemplateOf(name);
    foreach (var directory in unitDirectories) {
      if (template is not null)
        Apply(unit, Path.Combine(directory, template + ".d"));

      Apply(unit, Path.Combine(directory, name + ".d"));
    }

    return unit;

    static void Apply(UnitFile unit, string directory) {
      var files = new List<string>(Enumerate(directory, "*.conf"));
      // By name, which is what systemd orders them by — the leading numbers on a drop-in are there
      // precisely so that a later one wins.
      files.Sort(StringComparer.Ordinal);
      foreach (var file in files)
        unit.Merge(ReadLines(file));
    }
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
