using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>The kind of resource a system-wide reverse lookup found (PRD §33).</summary>
public enum ReverseResourceKind : byte {
  Process,
  CommandLine,
  ImagePath,
  Handle,
  Module,
  MappedFile,
  Socket,
  Service,
}

/// <summary>One concrete reason a process matched a reverse-resource search.</summary>
/// <param name="Key">
/// The process identity rather than only its PID. A PID can be reused while a result window is still
/// open; the start identity makes a later navigation attempt able to tell the replacement apart.
/// </param>
/// <param name="ObjectType">
/// The more specific type where one exists: File, Pipe, SharedObject, Data, Tcp, and so on. This is
/// deliberately text because the underlying type systems differ between handles, mappings and
/// sockets and flattening them into another enum would throw information away.
/// </param>
public readonly record struct ReverseResourceMatch(
  ProcessKey Key,
  string ProcessName,
  string? UserName,
  ReverseResourceKind Kind,
  string ObjectType,
  string Detail,
  string? Access
) {
  public int Pid => this.Key.Pid;
}

/// <summary>
/// Results of an exhaustive reverse lookup and how much of the requested deep scan actually answered.
/// </summary>
/// <remarks>
/// A normal account is not allowed to inspect every process on either supported desktop platform.
/// Reporting the coverage beside the matches is therefore part of correctness: zero matches from
/// 37/214 readable processes is not evidence that nobody on the machine has the file open.
/// </remarks>
public readonly record struct ReverseSearchReport(
  IReadOnlyList<ReverseResourceMatch> Matches,
  int DeepScanned,
  int DeepAttempted
) {
  public bool IsComplete => this.DeepScanned == this.DeepAttempted;
}

/// <summary>
/// Exhaustive, user-triggered reverse lookup for handles, loaded modules, mapped files and sockets.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately separate from <see cref="ResourceSearch.Find"/>'s cheap "find a process"
/// query. That query stops once a process has supplied a useful answer; this one answers a different
/// question — <em>every reason this process refers to the thing I typed</em> — and therefore must not
/// discard a handle merely because the process name happened to match too.
/// </para>
/// <para>
/// It still reuses <see cref="ResourceSearch.Compile"/> so the desktop, terminal and command-line
/// pattern grammar remains one grammar rather than three almost-compatible ones.
/// </para>
/// </remarks>
public static class ResourceReverseSearch {

  public static ReverseSearchReport Find(
    ISystemProbe probe,
    SystemSnapshot snapshot,
    string pattern,
    bool deep = true,
    bool matchCase = false
  ) {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(snapshot);

    var matcher = ResourceSearch.Compile(pattern, matchCase);
    var matches = new List<ReverseResourceMatch>();
    if (matcher is null)
      return new(matches, 0, 0);

    var services = Read(() => probe.GetServices(), out _);
    var servicesByPid = new Dictionary<int, List<ServiceRecord>>();
    foreach (var service in services) {
      if (service.MainPid <= 0)
        continue;

      if (!servicesByPid.TryGetValue(service.MainPid, out var owned)) {
        owned = [];
        servicesByPid.Add(service.MainPid, owned);
      }

      owned.Add(service);
    }

    var deepScanned = 0;
    var deepAttempted = 0;
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      ref readonly var process = ref processes[i];
      AddIdentity(in process, matcher, matches);

      if (servicesByPid.TryGetValue(process.Pid, out var ownedServices))
        foreach (var service in ownedServices)
          if (matcher.Matches(service.Name)
              || service.Description is { } description && matcher.Matches(description))
            Add(matches, in process, ReverseResourceKind.Service, "Service", service.Name, null);

      if (!deep)
        continue;

      ++deepAttempted;
      var complete = true;

      var handles = Read(() => probe.GetHandles(process.Key), out var handlesRead);
      complete &= handlesRead;
      foreach (var handle in handles) {
        if (handle.Name is not { } name || !matcher.Matches(name))
          continue;

        Add(
          matches,
          in process,
          ReverseResourceKind.Handle,
          handle.Kind.ToString(),
          name,
          handle.Access
        );
      }

      var modules = Read(() => probe.GetModules(process.Key), out var modulesRead);
      complete &= modulesRead;
      foreach (var module in modules) {
        if (!matcher.Matches(module.Path))
          continue;

        var kind = IsMappedData(module) ? ReverseResourceKind.MappedFile : ReverseResourceKind.Module;
        Add(
          matches,
          in process,
          kind,
          module.LoadReason == ModuleLoadReason.Data ? "Data" : module.Type.ToString(),
          module.Path,
          module.Permissions.Length > 0 ? module.Permissions : null
        );
      }

      var connections = Read(() => probe.GetConnections(process.Key), out var connectionsRead);
      complete &= connectionsRead;
      foreach (var connection in connections) {
        var endpoint = $"{connection.LocalAddress}:{connection.LocalPort} → {connection.RemoteAddress}:{connection.RemotePort}";
        if (!matcher.Matches(endpoint))
          continue;

        Add(
          matches,
          in process,
          ReverseResourceKind.Socket,
          connection.Protocol.ToString(),
          endpoint,
          null
        );
      }

      if (complete)
        ++deepScanned;
    }

    return new(matches, deepScanned, deepAttempted);
  }

  private static void AddIdentity(
    in ProcessRecord process,
    ICompiledPattern matcher,
    List<ReverseResourceMatch> matches
  ) {
    // These three describe one process identity. Keep only the first reason, otherwise searching for
    // "firefox" usually gives three adjacent copies before the useful resource matches begin.
    if (matcher.Matches(process.Name))
      Add(matches, in process, ReverseResourceKind.Process, "Process", process.Name, null);
    else if (process.CommandLine is { } commandLine && matcher.Matches(commandLine))
      Add(matches, in process, ReverseResourceKind.CommandLine, "Command line", commandLine, null);
    else if (process.ImagePath is { } imagePath && matcher.Matches(imagePath))
      Add(matches, in process, ReverseResourceKind.ImagePath, "Image", imagePath, null);
  }

  private static bool IsMappedData(in ModuleRecord module)
    => module.LoadReason == ModuleLoadReason.Data || module.Runtime == ModuleRuntime.NotCode;

  private static void Add(
    List<ReverseResourceMatch> matches,
    in ProcessRecord process,
    ReverseResourceKind kind,
    string objectType,
    string detail,
    string? access
  ) {
    // Several address ranges of one file are already folded into one ModuleRecord, but descriptors
    // are not. Keep distinct descriptor hits: two opens of the same file are two references and are
    // exactly what a leak investigation is looking for.
    matches.Add(new(process.Key, process.Name, process.UserName, kind, objectType, detail, access));
  }

  private static IReadOnlyList<T> Read<T>(Func<IReadOnlyList<T>> reader, out bool answered) {
    try {
      var result = reader();
      answered = true;
      return result;
    } catch (IOException) {
      answered = false;
      return [];
    } catch (UnauthorizedAccessException) {
      answered = false;
      return [];
    } catch (PlatformNotSupportedException) {
      answered = false;
      return [];
    }
  }

}
