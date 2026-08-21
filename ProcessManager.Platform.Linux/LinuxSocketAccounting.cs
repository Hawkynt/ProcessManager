using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// How many sockets of each kind each process holds (PRD §18, §40).
/// </summary>
/// <remarks>
/// <para>
/// Four counts and no bytes, and the line between them is the point. Linux joins a socket to a
/// process through the descriptor that names its inode, which establishes <em>which</em> connections
/// a process holds; it attributes no traffic to a process at all without packet accounting or eBPF,
/// so the traffic columns of §18 do not exist rather than being filled from something adjacent
/// (PRD §72.3).
/// </para>
/// <para>
/// One machine-wide pass per sample rather than a scan per process. The join needs every process's
/// descriptors read either way, and doing it per process would mean re-reading the five socket
/// tables six hundred times to answer one question. That pass is the expense — a <c>readlink</c> per
/// open descriptor on the machine — which is why none of this happens unless a column asked for it
/// (PRD §5.4).
/// </para>
/// <para>
/// No platform gate, deliberately: everything here goes through the portable file access the rest
/// of the probe uses, so a recorded tree replays through it on every CI leg the same way the
/// descriptor count does (PRD §9.1).
/// </para>
/// </remarks>
internal sealed class LinuxSocketAccounting {

  private readonly ProcFileReader _reader;
  private readonly ProcIo _io;
  private readonly string _procRoot;
  private readonly byte[] _procRootUtf8;

  private readonly Dictionary<ulong, SocketFacts> _sockets = [];
  private readonly Dictionary<int, Tally> _tallies = [];
  // Which processes let their descriptors be listed. Without it a process holding no sockets and a
  // process whose descriptors we may not see are the same empty tally, and reporting the second as
  // nought is the exact defect §72.3 exists to prevent.
  private readonly HashSet<int> _listed = [];
  private readonly List<ConnectionRecord> _scratch = [];
  private readonly List<int> _descriptors = [];
  private readonly byte[] _directoryScratch = new byte[32 * 1024];

  /// <summary>False when the socket tables could not be read at all, which is not the same as empty.</summary>
  private bool _read;

  public LinuxSocketAccounting(ProcFileReader reader, ProcIo io, string procRoot, byte[] procRootUtf8) {
    this._reader = reader;
    this._io = io;
    this._procRoot = procRoot;
    this._procRootUtf8 = procRootUtf8;
  }

  /// <summary>What a row of the socket tables contributes to a count.</summary>
  private readonly record struct SocketFacts(bool IsTcp, bool IsListening, string RemoteAddress, int RemotePort);

  /// <summary>
  /// One process's sockets. The remote endpoints are a set because two connections to one peer are
  /// one correspondent, which is what the column is read for.
  /// </summary>
  private sealed class Tally {
    public int Tcp;
    public int Udp;
    public int Listening;
    public readonly HashSet<(string Address, int Port)> Remotes = [];
  }

  /// <summary>
  /// Reads the socket tables and walks every readable process's descriptors. Once per sample.
  /// </summary>
  public void BeginSample() {
    this._sockets.Clear();
    this._tallies.Clear();
    this._listed.Clear();
    this._read = this.ReadTables();
    if (this._read)
      this.Attribute();
  }

  private bool ReadTables() {
    this._scratch.Clear();
    var read = false;
    read |= this.ReadTable("/net/tcp", ConnectionProtocol.Tcp);
    read |= this.ReadTable("/net/tcp6", ConnectionProtocol.Tcp6);
    read |= this.ReadTable("/net/udp", ConnectionProtocol.Udp);
    read |= this.ReadTable("/net/udp6", ConnectionProtocol.Udp6);
    if (!read)
      return false;

    foreach (var connection in this._scratch) {
      // Inode zero means no descriptor refers to it, so no process can hold it and it would collide
      // with every other such socket on one key.
      if (connection.Inode == 0)
        continue;

      this._sockets[connection.Inode] = new(
        connection.Protocol is ConnectionProtocol.Tcp or ConnectionProtocol.Tcp6,
        string.Equals(connection.State, "LISTEN", StringComparison.Ordinal),
        connection.RemoteAddress,
        connection.RemotePort
      );
    }

    this._scratch.Clear();
    return true;
  }

  private bool ReadTable(string relativePath, ConnectionProtocol protocol) {
    if (!this._reader.TryReadWhole(this._procRoot + relativePath, out var content, out _))
      return false;

    // The interface is not wanted here and finding it costs two more file reads per sample, so the
    // empty map is passed deliberately rather than for lack of one.
    ProcNetParser.ParseInet(
      Encoding.UTF8.GetString(content),
      protocol,
      NetworkInterfaceMap.Empty,
      null,
      this._scratch
    );

    return true;
  }

  /// <summary>
  /// Walks <c>/proc/[pid]/fd</c> for every process and charges each socket to whoever holds it.
  /// </summary>
  /// <remarks>
  /// A socket inherited across a <c>fork</c> is counted for both processes, unlike the owner map the
  /// connection listing builds, which names one. Both are right for what they are asked: "who owns
  /// port 22" wants one answer, and "how many connections is this worker holding" wants the truth
  /// about that worker.
  /// </remarks>
  private void Attribute() {
    string[] processes;
    try {
      processes = Directory.GetDirectories(this._procRoot);
    } catch (IOException) {
      return;
    } catch (UnauthorizedAccessException) {
      return;
    }

    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    foreach (var process in processes) {
      if (!int.TryParse(Path.GetFileName(process), out var pid))
        continue;

      this._descriptors.Clear();
      var fdPath = ProcPath.Build(pathBuffer, this._procRootUtf8, pid, "fd"u8);
      if (!this._io.ListNumericEntries(fdPath, this._directoryScratch, this._descriptors))
        // Somebody else's process, or it exited between the listing and the open. Either way this
        // process gets no tally, and the counters say "not permitted" rather than nought.
        continue;

      this._listed.Add(pid);
      var prefix = process + "/fd/";
      foreach (var descriptor in this._descriptors) {
        if (this._reader.TryReadLink(prefix + descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture)) is not { } target)
          continue;

        if (!ProcNetParser.TryParseSocketInode(target, out var inode))
          continue;

        // A Unix socket has an inode too and is not in these tables. It is not counted, because
        // neither tcp.count nor udp.count is about it and inventing a third bucket here would be
        // answering a question nobody asked (PRD §40).
        if (!this._sockets.TryGetValue(inode, out var facts))
          continue;

        if (!this._tallies.TryGetValue(pid, out var tally))
          this._tallies[pid] = tally = new();

        if (facts.IsTcp)
          ++tally.Tcp;
        else
          ++tally.Udp;

        if (facts.IsListening)
          ++tally.Listening;

        // A socket with no peer — every listener, and every datagram socket that was never
        // connected — is connected to nobody rather than to address zero.
        if (facts.RemotePort != 0 && facts.RemoteAddress.Length > 0)
          tally.Remotes.Add((facts.RemoteAddress, facts.RemotePort));
      }
    }
  }

  /// <summary>Fills one process's four counters from this sample's pass.</summary>
  public void Fill(int pid, ref ProcessRecord record) {
    if (!this._read) {
      // The tables would not open. That is a fact about this run rather than about the process, and
      // it must not read as a process holding no sockets.
      Set(ref record, Counter.NotPermitted);
      return;
    }

    if (!this._tallies.TryGetValue(pid, out var tally)) {
      // Nothing was charged to it, which is either "it holds no sockets" or "we may not see its
      // descriptors" — and those are different statements, so which one it was is remembered rather
      // than guessed at from an empty tally.
      Set(ref record, this._listed.Contains(pid) ? Counter.Of(0ul) : Counter.NotPermitted);
      return;
    }

    record.TcpSocketCount = Counter.Of((ulong)tally.Tcp);
    record.UdpSocketCount = Counter.Of((ulong)tally.Udp);
    record.ListeningSocketCount = Counter.Of((ulong)tally.Listening);
    record.RemoteEndpointCount = Counter.Of((ulong)tally.Remotes.Count);
  }

  private static void Set(ref ProcessRecord record, Counter value) {
    record.TcpSocketCount = value;
    record.UdpSocketCount = value;
    record.ListeningSocketCount = value;
    record.RemoteEndpointCount = value;
  }

}
