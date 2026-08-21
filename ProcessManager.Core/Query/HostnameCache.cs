using System.Collections.Concurrent;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Addresses to names, looked up somewhere else and never here (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// A blocking name lookup in a table that refreshes every second is a hang waiting to happen: one
/// unreachable resolver and the whole interface stops for the timeout, once per address, every
/// second. So nothing on this class waits. <see cref="Lookup"/> answers with what is already known
/// and starts a lookup for what is not, and the name appears in a later frame or does not appear.
/// </para>
/// <para>
/// It is also off unless somebody turns it on. On some networks a reverse lookup tells whoever runs
/// the resolver which addresses this machine is talking to, and a process manager should not be
/// making that disclosure on its own initiative (PRD §40).
/// </para>
/// <para>
/// Failures are remembered as firmly as successes. An address with no name is the ordinary case, and
/// retrying every one of them every second is how this becomes the load it was meant to avoid.
/// </para>
/// </remarks>
public sealed class HostnameCache : IDisposable {

  /// <summary>What a lookup returned, or that it returned nothing.</summary>
  private readonly ConcurrentDictionary<string, string?> _known = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
  private readonly BlockingCollection<string> _queue;
  private readonly Func<string, string?> _resolve;
  private readonly Thread _worker;
  private bool _disposed;

  /// <param name="resolve">
  /// How a name is found. Injected so the tests can answer instantly, slowly or not at all without a
  /// network — and so that a machine with no resolver is a case somebody can actually write down.
  /// </param>
  /// <param name="queueDepth">
  /// How many addresses may be waiting. A table of ten thousand sockets must not queue ten thousand
  /// lookups: past this, an address is simply not resolved this time round, and asking again later
  /// costs nothing because the table is redrawn anyway.
  /// </param>
  public HostnameCache(Func<string, string?>? resolve = null, int queueDepth = 256) {
    this._resolve = resolve ?? DefaultResolve;
    this._queue = new(queueDepth);
    this._worker = new(this.Work) {
      IsBackground = true,
      Name = "hostname lookup",
    };
    this._worker.Start();
  }

  /// <summary>
  /// Whether to look anything up at all. Off by default; turning it off again keeps whatever has
  /// already been learnt, because forgetting it would not un-send the queries that found it.
  /// </summary>
  public bool Enabled { get; set; }

  /// <summary>How many addresses have an answer, including the ones whose answer is "no name".</summary>
  public int Count => this._known.Count;

  /// <summary>
  /// The name for an address if it is already known, and null otherwise. Never waits, never throws,
  /// and starts a lookup for anything it could not answer.
  /// </summary>
  public string? Lookup(string? address) {
    if (!this.Enabled || address is not { Length: > 0 } || this._disposed)
      return null;

    if (this._known.TryGetValue(address, out var name))
      // Null here means the lookup happened and found nothing, which is an answer and not a reason
      // to ask again.
      return name;

    // TryAdd, so an address already queued is not queued twice; a table redrawn every second would
    // otherwise ask for the same name sixty times a minute.
    if (this._pending.TryAdd(address, 0) && !this._queue.TryAdd(address))
      // The queue is full. Forget that it was pending so it can be asked for again next time, when
      // there may be room.
      this._pending.TryRemove(address, out _);

    return null;
  }

  /// <summary>
  /// The name if it is known, and the address itself if it is not.
  /// </summary>
  /// <remarks>
  /// What a column shows: an address is always true, where a blank cell would read as though the
  /// machine had no address at all.
  /// </remarks>
  public string Describe(string address) => this.Lookup(address) ?? address;

  /// <summary>
  /// Waits, up to <paramref name="limit"/>, for the lookups already asked for to finish.
  /// </summary>
  /// <remarks>
  /// For a one-shot listing only, where there is no later frame for a name to appear in. Nothing
  /// that redraws should call this — the entire point of the class is that a table never waits for a
  /// resolver — and it gives up at the limit rather than at an answer, so a hung resolver costs that
  /// long once instead of on every line.
  /// </remarks>
  /// <returns>True if everything asked for has been answered.</returns>
  public bool WaitForPending(TimeSpan limit) {
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (!this._pending.IsEmpty && clock.Elapsed < limit)
      Thread.Sleep(10);

    return this._pending.IsEmpty;
  }

  /// <summary>Forgets everything, so the next look asks again.</summary>
  public void Clear() {
    this._known.Clear();
    this._pending.Clear();
  }

  private void Work() {
    foreach (var address in this._queue.GetConsumingEnumerable())
      try {
        // Whatever comes back, including nothing, is remembered: an address with no name is the
        // ordinary case and asking again every second is the load this class exists to avoid.
        this._known[address] = this._resolve(address);
      } catch (Exception) {
        // A resolver that throws is a resolver that answered "no name", as far as a table is
        // concerned. It must not take the thread — and with it every later lookup — down with it.
        this._known[address] = null;
      } finally {
        this._pending.TryRemove(address, out _);
      }
  }

  private static string? DefaultResolve(string address) {
    if (!System.Net.IPAddress.TryParse(address, out var parsed))
      return null;

    var name = System.Net.Dns.GetHostEntry(parsed).HostName;
    // A resolver that cannot answer hands back the address it was given. That is not a name, and
    // showing it in a hostname column would look like a successful lookup.
    return string.Equals(name, address, StringComparison.OrdinalIgnoreCase) ? null : name;
  }

  public void Dispose() {
    if (this._disposed)
      return;

    this._disposed = true;
    this._queue.CompleteAdding();
    // Bounded: a lookup already in flight cannot be cancelled, and a resolver that never answers
    // must not stop the program from closing. The thread is a background one, so it cannot either.
    this._worker.Join(TimeSpan.FromMilliseconds(250));
    this._queue.Dispose();
  }

}
