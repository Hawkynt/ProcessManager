namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Port numbers to the names people actually use for them, from <c>/etc/services</c> (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// "443" and "https" are the same fact, and only one of them can be read at a glance. The file is
/// the system's own answer rather than a table compiled in here: a machine that calls 8080
/// <c>http-alt</c> and one that calls it something else are both right about themselves, and a
/// built-in list would be wrong about both eventually.
/// </para>
/// <para>
/// Parsed here, with no platform attribute, so the parsing is tested on every leg. Windows ships the
/// same format at <c>%SystemRoot%\System32\drivers\etc\services</c>, which is why this reads content
/// rather than opening anything itself.
/// </para>
/// </remarks>
public sealed class ServiceNames {

  /// <summary>A port, and whether it was claimed for a stream or a datagram.</summary>
  /// <remarks>
  /// Both, because they are genuinely different registrations. 546 is <c>dhcpv6-client</c> over UDP
  /// and nothing over TCP on most machines, and answering with the wrong one is worse than not
  /// answering.
  /// </remarks>
  private readonly Dictionary<int, string> _tcp = [];
  private readonly Dictionary<int, string> _udp = [];

  /// <summary>Nothing at all, for a machine with no such file.</summary>
  public static ServiceNames Empty { get; } = new();

  public int Count => this._tcp.Count + this._udp.Count;

  /// <summary>
  /// Reads the file's content. Malformed lines are skipped rather than thrown over: this file is
  /// edited by hand on plenty of machines, and one bad line must not cost every other name.
  /// </summary>
  public static ServiceNames Parse(ReadOnlySpan<char> content) {
    var names = new ServiceNames();
    foreach (var lineRange in content.Split('\n')) {
      var line = content[lineRange];

      // Everything from a hash is a comment, including the aliases after one.
      var hash = line.IndexOf('#');
      if (hash >= 0)
        line = line[..hash];

      var name = NextWord(ref line);
      if (name.IsEmpty)
        continue;

      var port = NextWord(ref line);
      var slash = port.IndexOf('/');
      if (slash <= 0)
        continue;

      if (!int.TryParse(port[..slash], out var number) || number is <= 0 or > 65535)
        continue;

      var protocol = port[(slash + 1)..];
      // The first registration for a port wins. The file lists the canonical name first and its
      // aliases on later lines, and "http" is a better answer than "www".
      if (protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        names._tcp.TryAdd(number, name.ToString());
      else if (protocol.Equals("udp", StringComparison.OrdinalIgnoreCase))
        names._udp.TryAdd(number, name.ToString());
    }

    return names;
  }

  /// <summary>The name for a port, or null where the file does not name one.</summary>
  /// <remarks>
  /// Null rather than the number as a string: the caller already has the number, and a name it
  /// invented would be indistinguishable from one the machine actually declares (PRD §5.3).
  /// </remarks>
  public string? Find(int port, bool datagram) {
    var table = datagram ? this._udp : this._tcp;
    return table.TryGetValue(port, out var found) ? found : null;
  }

  /// <summary>
  /// An endpoint as somebody would say it: the name where there is one, the number where there is
  /// not.
  /// </summary>
  public string Describe(int port, bool datagram)
    => this.Find(port, datagram) ?? port.ToString(System.Globalization.CultureInfo.InvariantCulture);

  private static ReadOnlySpan<char> NextWord(ref ReadOnlySpan<char> line) {
    var start = 0;
    while (start < line.Length && char.IsWhiteSpace(line[start]))
      ++start;

    var end = start;
    while (end < line.Length && !char.IsWhiteSpace(line[end]))
      ++end;

    var word = line[start..end];
    line = line[end..];
    return word;
  }

}
