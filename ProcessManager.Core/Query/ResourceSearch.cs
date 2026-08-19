using System.Text.RegularExpressions;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>What kind of thing matched.</summary>
public enum ResourceKind : byte {
  Name,
  CommandLine,
  ImagePath,
  OpenFile,
  MappedModule,
  Socket,
  Service,
}

/// <param name="Detail">The thing that matched — a path, an endpoint, a unit name.</param>
public readonly record struct ResourceMatch(
  int Pid,
  string ProcessName,
  string? UserName,
  ResourceKind Kind,
  string Detail
);

/// <summary>
/// "Which process is using this?" (PRD §33).
/// </summary>
/// <remarks>
/// One of the two or three reasons people install Process Explorer at all. The cheap fields are
/// searched first and the expensive ones only for processes the cheap ones did not already answer
/// for, because enumerating every descriptor and every mapping of every process costs far more than
/// the rest of this program does in a second (PRD §5.4).
/// </remarks>
public static class ResourceSearch {

  /// <summary>
  /// Finds everything matching <paramref name="pattern"/>.
  /// </summary>
  /// <param name="pattern">
  /// A substring, or <c>/…/</c> for a regular expression. Substrings are matched without regard to
  /// case, which is what somebody typing half a file name wants.
  /// </param>
  /// <param name="deep">
  /// Search descriptors, mappings and sockets as well. Off makes the search cheap and shallow; on is
  /// what answers the question the search exists for.
  /// </param>
  public static IReadOnlyList<ResourceMatch> Find(
    ISystemProbe probe,
    SystemSnapshot snapshot,
    string pattern,
    bool deep = true
  ) {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(snapshot);

    var matcher = Matcher.For(pattern);
    var matches = new List<ResourceMatch>();
    if (matcher is null)
      return matches;

    // Services are read once and indexed by their main process, so a search for a unit name finds
    // the process behind it rather than nothing at all.
    var servicesByPid = new Dictionary<int, ServiceRecord>();
    foreach (var service in Safe(probe.GetServices))
      if (service.MainPid > 0)
        servicesByPid.TryAdd(service.MainPid, service);

    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      ref readonly var process = ref processes[i];
      var before = matches.Count;

      // Name, command line and image path are three spellings of the same identity, and a pattern
      // matching one usually matches the next: "nginx" is in the name, in the command line and in
      // the path. One reason per process is information; three is noise, so the most specific one
      // that answers wins and the rest are not reported.
      if (matcher.Matches(process.Name))
        matches.Add(Match(in process, ResourceKind.Name, process.Name));
      else if (process.CommandLine is { } commandLine && matcher.Matches(commandLine))
        matches.Add(Match(in process, ResourceKind.CommandLine, commandLine));
      else if (process.ImagePath is { } image && matcher.Matches(image))
        matches.Add(Match(in process, ResourceKind.ImagePath, image));

      if (servicesByPid.TryGetValue(process.Pid, out var owning)
          && (matcher.Matches(owning.Name) || (owning.Description is { } text && matcher.Matches(text))))
        matches.Add(Match(in process, ResourceKind.Service, owning.Name));

      if (!deep || matches.Count > before)
        continue;

      FindDeep(probe, in process, matcher, matches);
    }

    return matches;
  }

  private static void FindDeep(ISystemProbe probe, in ProcessRecord process, Matcher matcher, List<ResourceMatch> matches) {
    var key = process.Key;
    var pid = process.Pid;
    var name = process.Name;
    var user = process.UserName;

    foreach (var handle in Safe(() => probe.GetHandles(key)))
      if (handle.Name is { } text && matcher.Matches(text)) {
        matches.Add(new(pid, name, user, ResourceKind.OpenFile, text));
        return;
      }

    foreach (var module in Safe(() => probe.GetModules(key)))
      if (matcher.Matches(module.Path)) {
        matches.Add(new(pid, name, user, ResourceKind.MappedModule, module.Path));
        return;
      }

    foreach (var connection in Safe(() => probe.GetConnections(key))) {
      // Both ends and the port, because "who is talking to 10.0.0.5" and "who is on 443" are the
      // same question asked two ways.
      var endpoint = $"{connection.LocalAddress}:{connection.LocalPort} → {connection.RemoteAddress}:{connection.RemotePort}";
      if (matcher.Matches(endpoint)) {
        matches.Add(new(pid, name, user, ResourceKind.Socket, endpoint));
        return;
      }
    }
  }

  private static ResourceMatch Match(in ProcessRecord process, ResourceKind kind, string detail)
    => new(process.Pid, process.Name, process.UserName, kind, detail);

  /// <summary>
  /// A process may exit between being listed and being asked about, and a probe may refuse.
  /// Neither is an error worth stopping a search for (PRD §73).
  /// </summary>
  private static IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>> read) {
    try {
      return read();
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    } catch (PlatformNotSupportedException) {
      return [];
    }
  }

  private sealed class Matcher {

    private readonly string? _text;
    private readonly Regex? _pattern;

    private Matcher(string? text, Regex? pattern) {
      this._text = text;
      this._pattern = pattern;
    }

    public static Matcher? For(string pattern) {
      if (string.IsNullOrEmpty(pattern))
        return null;

      if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
        try {
          // Interpreted and time-limited, like the filter's: NativeAOT cannot emit IL at run time,
          // and a search box is no place to hang (PRD §8.3).
          return new(null, new(
            pattern[1..^1],
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50)
          ));
        } catch (ArgumentException) {
          // A pattern that will not compile is searched for literally, which is what somebody
          // looking for a file called "a/b/" meant anyway.
          return new(pattern, null);
        }

      return new(pattern, null);
    }

    public bool Matches(string value) => this._pattern is { } pattern
      ? pattern.IsMatch(value)
      : value.Contains(this._text!, StringComparison.OrdinalIgnoreCase);

  }

}
