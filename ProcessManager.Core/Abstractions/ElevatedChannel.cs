using System.Diagnostics;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>
/// The unprivileged side of the conversation with <c>procman-helper</c> (PRD §8).
/// </summary>
/// <remarks>
/// <para>
/// Started <em>lazily</em> — the first time an action or a column actually needs it — so that
/// running the program never prompts for a password by itself. The prompt, when it comes, is
/// attached to something the user just asked for.
/// </para>
/// <para>
/// A refused elevation is a normal outcome, not an error: <see cref="Available"/> goes false, the
/// reason is kept, and nothing retries in a loop. The caller reports the affected columns as
/// <see cref="UnknownReason.NotPermitted"/> and carries on (PRD §8.3).
/// </para>
/// </remarks>
public sealed class ElevatedChannel : IDisposable {

  private readonly string _helperPath;
  private readonly bool _useElevation;
  private Process? _helper;
  private bool _attempted;

  public ElevatedChannel(string helperPath, bool useElevation = true) {
    ArgumentNullException.ThrowIfNull(helperPath);
    this._helperPath = helperPath;
    this._useElevation = useElevation;
  }

  /// <summary>True once the helper is running and answering.</summary>
  public bool Available => this._helper is { HasExited: false };

  /// <summary>Why the helper is not available, for the UI to show once rather than repeatedly.</summary>
  public string? Unavailable { get; private set; }

  /// <summary>
  /// Starts the helper if it is not running. Returns false when it will not start — which is a
  /// normal answer, and is remembered so the prompt does not reappear on every sample.
  /// </summary>
  public bool Start() {
    if (this.Available)
      return true;
    if (this._attempted)
      return false;

    this._attempted = true;
    if (!File.Exists(this._helperPath)) {
      this.Unavailable = $"the helper is not installed at {this._helperPath}";
      return false;
    }

    var start = new ProcessStartInfo {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };

    if (!this._useElevation)
      start.FileName = this._helperPath;
    else if (OperatingSystem.IsLinux()) {
      // pkexec passes standard input and output through to the child, which is what makes the
      // anonymous pipe pair of §8.2 work without a socket path anywhere on the file system.
      start.FileName = "pkexec";
      start.ArgumentList.Add(this._helperPath);
    } else if (OperatingSystem.IsWindows()) {
      // Windows cannot both elevate and redirect the standard handles in one call: ShellExecute's
      // "runas" verb is what raises the UAC prompt, and it refuses redirection. Elevation on Windows
      // therefore needs a named pipe the elevated child connects back to, which is not written yet —
      // saying so beats a helper that silently runs unelevated and reports the same refusals.
      this.Unavailable = "elevation on Windows is not implemented yet (PRD §8, milestone M7)";
      return false;
    } else {
      this.Unavailable = $"there is no elevation path for {Environment.OSVersion.Platform}";
      return false;
    }

    try {
      this._helper = Process.Start(start);
    } catch (Exception e) {
      this.Unavailable = $"the helper could not be started: {e.Message}";
      return false;
    }

    if (this._helper is null) {
      this.Unavailable = "the helper could not be started";
      return false;
    }

    return true;
  }

  /// <summary>
  /// Sends one request and waits for its answer. Returns <see cref="ElevatedStatus.Failed"/> with no
  /// payload when the helper has gone, and notices that it has.
  /// </summary>
  public (ElevatedStatus Status, byte[] Payload) Send(ElevatedOpcode opcode, ProcessKey key, long argument = 0) {
    if (!this.Start())
      return (ElevatedStatus.NotPermitted, []);

    var helper = this._helper!;
    try {
      ElevatedProtocol.WriteRequest(helper.StandardInput.BaseStream, new(opcode, key, argument));
      return ElevatedProtocol.TryReadResponse(helper.StandardOutput.BaseStream, out var status, out var payload)
        ? (status, payload)
        : this.Died("the helper stopped answering");
    } catch (IOException e) {
      return this.Died($"the helper's pipe broke: {e.Message}");
    } catch (ObjectDisposedException) {
      return this.Died("the helper has gone");
    }
  }

  private (ElevatedStatus, byte[]) Died(string reason) {
    this.Unavailable = reason;
    this._helper = null;
    return (ElevatedStatus.Failed, []);
  }

  public void Dispose() {
    var helper = this._helper;
    this._helper = null;
    if (helper is null)
      return;

    try {
      // Closing the pipe is how the helper is asked to stop: its read returns end-of-stream and it
      // exits. Killing it would be the fallback, and it has never been needed because the helper has
      // no loop of its own to be stuck in.
      if (!helper.HasExited) {
        helper.StandardInput.Close();
        helper.WaitForExit(2000);
      }
    } catch (IOException) {
      // Already gone.
    } catch (InvalidOperationException) {
      // Already gone.
    } finally {
      helper.Dispose();
    }
  }

}
