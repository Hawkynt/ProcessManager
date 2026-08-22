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
/// <param name="Access">
/// What the process may do with the thing that matched: the descriptor's access mode for an open
/// file, and the permission characters of the mapping for a loaded module. Null where the question
/// does not arise — a process name has no access mode — and null on Windows, whose access mask is
/// in the handle table and is not decoded yet (PRD §33).
/// </param>
public readonly record struct ResourceMatch(
  int Pid,
  string ProcessName,
  string? UserName,
  ResourceKind Kind,
  string Detail,
  string? Access
);

/// <summary>One process holding a descriptor on a resource somebody asked about (PRD §32).</summary>
/// <param name="Descriptor">Its number in that process, which is what <c>lsof</c> prints as the FD.</param>
public readonly record struct ResourceHolder(
  int Pid,
  string ProcessName,
  string? UserName,
  ulong Descriptor,
  HandleKind Kind,
  string? Access
);

/// <summary>
/// What a scan of the machine's descriptors found, and how much of the machine it could see.
/// </summary>
/// <remarks>
/// The second half is the point. On a desktop a search from an ordinary account can look inside its
/// own processes and no others, so "nothing else holds this pipe" is very often "nothing we were
/// allowed to ask holds this pipe" — and a result that did not carry the difference would state the
/// first while meaning the second (PRD §72.3).
/// </remarks>
/// <param name="Answered">How many processes listed at least one descriptor.</param>
/// <param name="Total">How many there were.</param>
public readonly record struct HolderScan(IReadOnlyList<ResourceHolder> Holders, int Answered, int Total);

/// <summary>
/// How a pattern is read (PRD §33).
/// </summary>
/// <remarks>
/// Chosen by the shape of the pattern rather than by a control, so that every front-end has all four
/// without any of them growing its own dialect: the window, the terminal and <c>--find</c> take the
/// same string and mean the same thing by it (PRD §58).
/// </remarks>
public enum SearchMode : byte {
  /// <summary>Anywhere in the value. What somebody typing half a file name means.</summary>
  Substring,

  /// <summary><c>*</c> and <c>?</c>, matching the whole value: <c>*.so.6</c>.</summary>
  Wildcard,

  /// <summary>The whole value and nothing else, written <c>"like this"</c>.</summary>
  Exact,

  /// <summary>A regular expression, written <c>/like this/</c>.</summary>
  Regex,
}

/// <summary>
/// A pattern that has already been read, so a scan asks it rather than re-reading it (PRD §33, §35).
/// </summary>
/// <remarks>
/// An interface rather than a concrete type because the shape is the whole contract: something that
/// answers whether one value matches, and says which of the four modes it decided the pattern was in
/// so a front-end can put that in a caption without running a search to find out.
/// </remarks>
public interface ICompiledPattern {

  /// <summary>Which of the four modes the pattern's own shape asked for.</summary>
  SearchMode Mode { get; }

  bool Matches(string value);

}

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
  /// A substring; <c>*</c> or <c>?</c> anywhere in it makes it a wildcard over the whole value;
  /// <c>"quoted"</c> is an exact match and <c>/slashed/</c> a regular expression. Matching ignores
  /// case unless <paramref name="matchCase"/> says otherwise, which is what somebody typing half a
  /// file name wants.
  /// </param>
  /// <param name="deep">
  /// Search descriptors, mappings and sockets as well. Off makes the search cheap and shallow; on is
  /// what answers the question the search exists for.
  /// </param>
  /// <param name="matchCase">
  /// Distinguish upper from lower case. Off by default and worth turning on for exactly one kind of
  /// question — two files whose names differ only in case, which a case-preserving file system
  /// allows and an insensitive search cannot tell apart.
  /// </param>
  public static IReadOnlyList<ResourceMatch> Find(
    ISystemProbe probe,
    SystemSnapshot snapshot,
    string pattern,
    bool deep = true,
    bool matchCase = false
  ) {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(snapshot);

    var matcher = Matcher.For(pattern, matchCase);
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
        // The access mode travels with the answer: "which process has my file open" is usually
        // asked because the file cannot be replaced, and whether the holder has it open for writing
        // is the next question every time (PRD §33).
        matches.Add(new(pid, name, user, ResourceKind.OpenFile, text, handle.Access));
        return;
      }

    foreach (var module in Safe(() => probe.GetModules(key)))
      if (matcher.Matches(module.Path)) {
        // A mapping's access is its permission characters. Not the same kind of thing as a
        // descriptor's mode, and the honest equivalent: it is what this process may do with those
        // bytes.
        matches.Add(new(
          pid,
          name,
          user,
          ResourceKind.MappedModule,
          module.Path,
          module.Permissions.Length > 0 ? module.Permissions : null
        ));

        return;
      }

    foreach (var connection in Safe(() => probe.GetConnections(key))) {
      // Both ends and the port, because "who is talking to 10.0.0.5" and "who is on 443" are the
      // same question asked two ways.
      var endpoint = $"{connection.LocalAddress}:{connection.LocalPort} → {connection.RemoteAddress}:{connection.RemotePort}";
      if (matcher.Matches(endpoint)) {
        // A socket has no access mode: both directions are open or the connection is not there.
        matches.Add(new(pid, name, user, ResourceKind.Socket, endpoint, null));
        return;
      }
    }
  }

  private static ResourceMatch Match(in ProcessRecord process, ResourceKind kind, string detail)
    => new(process.Pid, process.Name, process.UserName, kind, detail, null);

  /// <summary>
  /// Everything on the machine holding a descriptor on one inode: the other end of a pipe, the other
  /// holders of a shared file (PRD §32).
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is what makes a pipe answerable. A pipe is two descriptors on one inode, and the kernel
  /// says which process holds the far end nowhere at all — the only way to it is to look at every
  /// process's descriptors and find the same inode, which is what <c>lsof</c> does and what this
  /// does.
  /// </para>
  /// <para>
  /// It costs a descriptor scan of the whole machine, so it is never done for a list and never on a
  /// tick: it answers one question about one row when somebody asks it (PRD §5.4). The count of
  /// processes that answered comes back with the holders, because "nothing else holds this" and
  /// "nothing else that would let us look holds this" are different statements and the caller has to
  /// be able to say which one it is showing (PRD §72.3).
  /// </para>
  /// </remarks>
  public static HolderScan FindHolders(ISystemProbe probe, SystemSnapshot snapshot, ulong inode, ProcessKey exclude) {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(snapshot);

    var holders = new List<ResourceHolder>();
    var answered = 0;
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      // Copied out of the array rather than held by reference: the read below goes through a lambda
      // for its error handling, and a ref local cannot be captured by one.
      var key = processes[i].Key;
      var pid = processes[i].Pid;
      var name = processes[i].Name;
      var user = processes[i].UserName;

      var handles = Safe(() => probe.GetHandles(key));
      if (handles.Count == 0)
        continue;

      ++answered;
      if (key == exclude)
        continue;

      for (var h = 0; h < handles.Count; ++h) {
        if (!handles[h].Inode.TryGetValue(out var held) || held != inode)
          continue;

        holders.Add(new(pid, name, user, handles[h].Handle, handles[h].Kind, handles[h].Access));
      }
    }

    return new(holders, answered, processes.Length);
  }

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

  /// <summary>
  /// Whether one value matches one pattern, under the same rules <see cref="Find"/> uses.
  /// </summary>
  /// <remarks>
  /// The rules are worth being able to ask about on their own: a front-end that wants to say which
  /// mode it read out of what somebody typed should not have to run a search to find out, and a test
  /// of the four modes should not need a machine (PRD §9.1).
  /// </remarks>
  public static bool Matches(string pattern, string value, bool matchCase = false) {
    ArgumentNullException.ThrowIfNull(value);

    return Matcher.For(pattern, matchCase) is { } matcher && matcher.Matches(value);
  }

  /// <summary>
  /// The same pattern, read once and then asked about many values (PRD §33, §35).
  /// </summary>
  /// <remarks>
  /// <see cref="Matches"/> reads the pattern on every call, which is right for the handful of values
  /// a search compares and wrong for a scan that has a million of them: a regular expression
  /// recompiled per candidate is the whole cost of the strings view. This hands back the compiled
  /// form so that the grammar stays in one place and nobody writes a second spelling of "contains".
  /// </remarks>
  /// <returns>Null for an empty pattern, which matches everything and is best asked as nothing.</returns>
  public static ICompiledPattern? Compile(string? pattern, bool matchCase = false)
    => pattern is { Length: > 0 } text ? Matcher.For(text, matchCase) : null;

  /// <summary>Which of the four modes a pattern asks for, by its shape (PRD §33).</summary>
  public static SearchMode ModeOf(string? pattern) {
    if (string.IsNullOrEmpty(pattern))
      return SearchMode.Substring;

    if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
      return SearchMode.Regex;

    if (pattern.Length >= 2 && pattern[0] == '"' && pattern[^1] == '"')
      return SearchMode.Exact;

    return pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal)
      ? SearchMode.Wildcard
      : SearchMode.Substring;
  }

  private sealed class Matcher : ICompiledPattern {

    private readonly string _text;
    private readonly Regex? _pattern;
    private readonly SearchMode _mode;
    private readonly bool _matchCase;

    private Matcher(string text, Regex? pattern, SearchMode mode, bool matchCase) {
      this._text = text;
      this._pattern = pattern;
      this._mode = mode;
      this._matchCase = matchCase;
    }

    public static Matcher? For(string pattern, bool matchCase) {
      if (string.IsNullOrEmpty(pattern))
        return null;

      var mode = ModeOf(pattern);
      switch (mode) {
        case SearchMode.Regex:
          try {
            // Interpreted and time-limited, like the filter's: NativeAOT cannot emit IL at run time,
            // and a search box is no place to hang (PRD §8.3).
            return new(pattern, new(
              pattern[1..^1],
              (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
              TimeSpan.FromMilliseconds(50)
            ), mode, matchCase);
          } catch (ArgumentException) {
            // A pattern that will not compile is searched for literally, which is what somebody
            // looking for a file called "a/b/" meant anyway.
            return new(pattern, null, SearchMode.Substring, matchCase);
          }

        case SearchMode.Exact:
          // The quotes are the notation and not part of what is being looked for, which is the same
          // rule the filter language of §56 applies to a quoted value.
          return new(pattern[1..^1], null, mode, matchCase);
        default:
          return new(pattern, null, mode, matchCase);
      }
    }

    public SearchMode Mode => this._mode;

    public bool Matches(string value) {
      var comparison = this._matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
      return this._mode switch {
        SearchMode.Regex => this._pattern!.IsMatch(value),
        SearchMode.Exact => string.Equals(value, this._text, comparison),
        SearchMode.Wildcard => Glob(this._text, value, this._matchCase),
        _ => value.Contains(this._text, comparison),
      };
    }

    /// <summary>
    /// <c>*</c> for any run of characters, <c>?</c> for exactly one, over the whole value.
    /// </summary>
    /// <remarks>
    /// Matched directly rather than by translating into a regular expression. The translation is the
    /// usual way and needs every other character escaped, which is one forgotten metacharacter away
    /// from <c>libc.so.6</c> matching <c>libcXso.6</c> — and a path is made of the characters a
    /// regular expression cares about. This is the classic two-pointer walk: remember where the last
    /// star was, and on a mismatch let it swallow one more character.
    /// </remarks>
    private static bool Glob(string pattern, string value, bool matchCase) {
      int p = 0, v = 0, star = -1, mark = 0;
      while (v < value.Length) {
        if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], value[v], matchCase))) {
          ++p;
          ++v;
          continue;
        }

        if (p < pattern.Length && pattern[p] == '*') {
          star = p++;
          mark = v;
          continue;
        }

        if (star < 0)
          return false;

        // Backtrack: the star takes one more character than it did last time.
        p = star + 1;
        v = ++mark;
      }

      while (p < pattern.Length && pattern[p] == '*')
        ++p;

      return p == pattern.Length;
    }

    private static bool Same(char a, char b, bool matchCase)
      => matchCase ? a == b : char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

  }

}
