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

  private readonly TimeSpan _timeout;
  private readonly SemaphoreSlim _requestReady = new(0, 1);
  private readonly SemaphoreSlim _resultReady = new(0, 1);
  private readonly Lock _gate = new();

  private Thread? _worker;
  private nint _pending;
  private string? _result;
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
      this.EnsureWorker();
      this._pending = handle;
      this._result = null;
      this._requestReady.Release();

      if (this._resultReady.Wait(this._timeout))
        return this._result;

      // The worker is inside a kernel call that will not return. Let it go and build a new one; the
      // semaphores go with it, because the abandoned thread still owns one side of the handshake.
      ++this.TimedOut;
      ++this._abandoned;
      this._worker = null;
      return null;
    }
  }

  private void EnsureWorker() {
    if (this._worker is { IsAlive: true })
      return;

    this._worker = new(this.Pump) {
      IsBackground = true,
      Name = "procman handle-name query",
    };

    this._worker.Start();
  }

  private void Pump() {
    while (!this._disposed) {
      this._requestReady.Wait();
      var handle = this._pending;
      string? name = null;
      try {
        name = QueryName(handle);
      } catch (Exception) {
        // A handle that faults the query is a handle without a name, as far as anyone above cares.
      }

      this._result = name;
      this._resultReady.Release();
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
    this._requestReady.Release();
  }

}
