using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Asks the kernel for a handle's name, and survives the handles that never answer.
/// </summary>
/// <remarks>
/// <para>
/// <c>NtQueryObject(ObjectNameInformation)</c> blocks forever on a handle to a synchronous named
/// pipe with no reader — the call enters the pipe's device stack and waits. There is no flag, no
/// timeout parameter, and no way to ask in advance whether a given handle will hang. Every tool that
/// enumerates handles has hit this, and it is why a process manager freezes when you open the handle
/// list on the wrong machine. PRD §5.2 names it as a design constraint rather than a defect to find
/// later; this is the design.
/// </para>
/// <para>
/// So the query runs on a worker thread and the caller waits with a timeout. When the timeout wins,
/// the worker is <em>abandoned</em> — not aborted, because there is no safe way to abort a thread
/// stuck in a kernel call, and <c>Thread.Abort</c> does not exist on .NET Core at all. The abandoned
/// thread is a background thread, so it cannot keep the process alive; it simply sits there until
/// the process ends. A fresh worker is started for the next request.
/// </para>
/// <para>
/// Abandoning threads is bounded: after <see cref="_MaxAbandoned"/> of them the resolver stops
/// naming handles entirely and says so, because a machine that hangs on a hundred handles will hang
/// on the next hundred, and leaking a thread each time is worse than an unnamed column.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class HandleNameResolver : IDisposable {

  private const int _MaxAbandoned = 16;

  /// <summary>
  /// One worker and the handshake that belongs to it.
  /// </summary>
  /// <remarks>
  /// Per worker, not per resolver, and that distinction is the whole correctness of this class. When
  /// a query times out the thread is abandoned still inside the kernel call — and it comes back
  /// eventually, and releases the semaphore it was handed. With one shared pair that release lands on
  /// the <em>next</em> worker's handshake, the count goes past its maximum, and
  /// <c>SemaphoreFullException</c> is thrown on a background thread with nobody to catch it: the
  /// whole program dies because one handle was slow. Giving each worker its own pair means a late
  /// release touches only a handshake nothing is listening to any more.
  /// </remarks>
  private sealed class Worker {
    public readonly SemaphoreSlim RequestReady = new(0, 1);
    public readonly SemaphoreSlim ResultReady = new(0, 1);
    public nint Pending;
    public string? Result;
    public volatile bool Abandoned;
  }

  private readonly TimeSpan _timeout;
  private readonly Lock _gate = new();

  private Worker? _worker;
  private int _abandoned;
  private bool _disposed;

  public HandleNameResolver(TimeSpan? timeout = null)
    => this._timeout = timeout ?? TimeSpan.FromMilliseconds(50);

  /// <summary>How many queries had to be given up on. Surfaced so the UI can say why a column is thin.</summary>
  public int TimedOut { get; private set; }

  /// <summary>True once too many handles have hung and naming has been switched off.</summary>
  public bool GaveUp => this._abandoned >= _MaxAbandoned;

  /// <summary>
  /// The handle's name, or null when it has none, cannot be named, or did not answer in time.
  /// </summary>
  public string? TryGetName(nint handle) {
    if (this._disposed || this.GaveUp)
      return null;

    lock (this._gate) {
      var worker = this.EnsureWorker();
      worker.Pending = handle;
      worker.Result = null;
      worker.RequestReady.Release();

      if (worker.ResultReady.Wait(this._timeout))
        return worker.Result;

      // The worker is inside a kernel call that will not return. Let it go — it is a background
      // thread, so it cannot keep the process alive — and build a new one. Its handshake goes with
      // it, which is what makes the release it will eventually perform harmless.
      ++this.TimedOut;
      ++this._abandoned;
      worker.Abandoned = true;
      this._worker = null;
      return null;
    }
  }

  private Worker EnsureWorker() {
    if (this._worker is { } existing)
      return existing;

    var worker = new Worker();
    this._worker = worker;
    var thread = new Thread(() => this.Pump(worker)) {
      IsBackground = true,
      Name = "procman handle-name query",
    };

    thread.Start();
    return worker;
  }

  private void Pump(Worker worker) {
    while (!this._disposed && !worker.Abandoned) {
      worker.RequestReady.Wait();
      if (this._disposed || worker.Abandoned)
        return;

      string? name = null;
      try {
        name = QueryName(worker.Pending);
      } catch (Exception) {
        // A handle that faults the query is a handle without a name, as far as anyone above cares.
      }

      worker.Result = name;
      try {
        worker.ResultReady.Release();
      } catch (SemaphoreFullException) {
        // Belt to the braces above: whatever happens, a slow handle must not end the program.
        return;
      }
    }
  }

  private static string? QueryName(nint handle) {
    // ObjectNameInformation is a UNICODE_STRING followed by its characters, and the size is not
    // knowable in advance — a device path can be long.
    var length = 1024;
    for (var attempt = 0; attempt < 2; ++attempt) {
      var buffer = Marshal.AllocHGlobal(length);
      try {
        var status = Native.NtQueryObject(handle, Native.ObjectNameInformation, buffer, (uint)length, out var needed);
        if (status == NtStructures.STATUS_INFO_LENGTH_MISMATCH || status == 0xC0000023) {
          length = (int)Math.Max(needed, (uint)length * 2);
          continue;
        }

        if (status != NtStructures.STATUS_SUCCESS)
          return null;

        var nameLength = (ushort)Marshal.ReadInt16(buffer);
        var pointer = Marshal.ReadIntPtr(buffer, nint.Size);
        return nameLength == 0 || pointer == 0 ? null : Marshal.PtrToStringUni(pointer, nameLength / sizeof(char));
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
    }

    return null;
  }

  /// <summary>
  /// The handle's <em>type</em> — "File", "Key", "Event". Safe to call inline: unlike the name
  /// query, this one reads a static string off the object's type and cannot block.
  /// </summary>
  public static string? QueryType(nint handle) {
    const int length = 512;
    var buffer = Marshal.AllocHGlobal(length);
    try {
      if (Native.NtQueryObject(handle, Native.ObjectTypeInformation, buffer, length, out _) != NtStructures.STATUS_SUCCESS)
        return null;

      // OBJECT_TYPE_INFORMATION starts with the type name as a UNICODE_STRING.
      var nameLength = (ushort)Marshal.ReadInt16(buffer);
      var pointer = Marshal.ReadIntPtr(buffer, nint.Size);
      return nameLength == 0 || pointer == 0 ? null : Marshal.PtrToStringUni(pointer, nameLength / sizeof(char));
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  public void Dispose() {
    this._disposed = true;
    var worker = this._worker;
    this._worker = null;
    try {
      worker?.RequestReady.Release();
    } catch (SemaphoreFullException) {
      // Already signalled; the worker will see _disposed and stop.
    }
  }

}
