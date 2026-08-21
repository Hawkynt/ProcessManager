using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>resolv.conf</c>: who this machine asks about names (PRD §49).
/// </summary>
/// <remarks>
/// A flat file of directives, of which two are worth reading: <c>nameserver</c> and <c>search</c>.
/// Comments begin with <c>#</c> or <c>;</c>, and the resolver ignores everything past the third
/// nameserver — which is worth knowing, because a file listing five is a file whose last two are
/// decoration.
/// <para>
/// The stub is its own answer. On a systemd-resolved machine the only nameserver here is
/// 127.0.0.53, which is not a DNS server on the network but this machine's own resolver listening on
/// loopback; reporting it as "the DNS server" is true and useless. The caller is told it is a stub
/// so it can go and read the upstream list instead (PRD §5.3).
/// </para>
/// <para>
/// No platform attribute and no file access, so it runs on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ResolverConfigParser {

  /// <summary>The loopback address systemd-resolved's stub listener answers on.</summary>
  public const string StubAddress = "127.0.0.53";

  /// <summary>What the file says.</summary>
  /// <param name="Servers">The nameservers, in the order the resolver will try them.</param>
  /// <param name="SearchDomains">The suffixes an unqualified name is tried with.</param>
  public readonly record struct ResolverConfig(IReadOnlyList<string> Servers, IReadOnlyList<string> SearchDomains) {

    /// <summary>
    /// Whether the only server is this machine's own stub resolver, in which case the file describes
    /// nothing about the network.
    /// </summary>
    public bool IsStubOnly => this.Servers.Count == 1 && this.Servers[0] == StubAddress;

  }

  public static ResolverConfig Parse(ReadOnlySpan<byte> content) {
    var servers = new List<string>();
    var domains = new List<string>();

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty || line[0] is (byte)'#' or (byte)';')
        continue;

      var fields = new AsciiScanner(line);
      var directive = fields.NextField();
      if (Word(directive, "nameserver"u8)) {
        var address = fields.NextField();
        if (!address.IsEmpty)
          servers.Add(Encoding.ASCII.GetString(address));

        continue;
      }

      // "search" takes a list; "domain" takes one and is the older spelling of the same idea.
      if (!Word(directive, "search"u8) && !Word(directive, "domain"u8))
        continue;

      while (true) {
        var domain = fields.NextField();
        if (domain.IsEmpty)
          break;

        domains.Add(Encoding.ASCII.GetString(domain));
      }
    }

    return new(servers, domains);
  }

  private static bool Word(ReadOnlySpan<byte> field, ReadOnlySpan<byte> word)
    => field.Length == word.Length && field.SequenceEqual(word);

}
