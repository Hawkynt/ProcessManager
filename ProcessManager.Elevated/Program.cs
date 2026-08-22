using System.Text;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Elevated;

/// <summary>
/// The privileged helper (PRD §8): the only component that ever runs as root or admin.
/// </summary>
/// <remarks>
/// <para>
/// It reads length-prefixed frames from its standard input and writes them to its standard output —
/// an anonymous pipe pair handed to it by the parent at spawn. There is no socket path, no FIFO in
/// <c>/tmp</c> and no port: nothing on the machine can connect to this but the process that started
/// it.
/// </para>
/// <para>
/// What it will do is the <see cref="ElevatedOpcode"/> list and nothing else. It does not take a
/// command to run, a path to read, or a format string. Every request names its target by
/// <see cref="ProcessKey"/> and every one is re-validated against the live system before anything
/// happens, because a pid recycled between the user's click and this syscall is a different program.
/// </para>
/// <para>
/// It has no timers, no polling loop and no state. It answers one request at a time and exits when
/// its input closes — which happens when the parent exits, so it cannot outlive the program that
/// started it.
/// </para>
/// </remarks>
internal static class Program {

  private static int Main(string[] args) {
    if (args.Contains("--version")) {
      Console.WriteLine($"procman-helper {typeof(Program).Assembly.GetName().Version}");
      return 0;
    }

    // A helper started by hand does nothing useful and should say so rather than sitting on a
    // terminal looking hung.
    if (!Console.IsInputRedirected) {
      Console.Error.WriteLine("procman-helper is started by procman, over a pipe. It has no interactive use.");
      return 2;
    }

    using var input = Console.OpenStandardInput();
    using var output = Console.OpenStandardOutput();

    while (ElevatedProtocol.TryReadRequest(input, out var request, out var problem)) {
      if (problem != ElevatedStatus.Ok) {
        // Malformed input ends the conversation. Resynchronising would mean guessing where the next
        // frame starts, and guessing is the thing a privileged parser must never do.
        ElevatedProtocol.WriteResponse(output, problem);
        return 1;
      }

      Handle(output, in request);
    }

    return 0;
  }

  /// <summary>Where Linux publishes the firmware's structure table, root-readable and nowhere else.</summary>
  private const string _SmbiosTable = "/sys/firmware/dmi/tables/DMI";

  private static void Handle(Stream output, in ElevatedProtocol.Request request) {
    // The firmware table names no process, so the recycled-pid check has nothing to check: it is a
    // fact about the machine, the same bytes whoever asks, and the path is the constant above rather
    // than anything the caller sent. Every opcode that does name a process still goes through
    // Identity below — the split is by opcode, so a caller cannot talk its way past the check by
    // leaving a key out (PRD §8.2).
    if (request.Opcode == ElevatedOpcode.ReadSmbios) {
      Respond(output, ReadWholeFile(_SmbiosTable));
      return;
    }

    if (!Identity.Matches(request.Key, out var status)) {
      ElevatedProtocol.WriteResponse(output, status);
      return;
    }

    switch (request.Opcode) {
      case ElevatedOpcode.ReadProcIo:
        Respond(output, ReadFile($"/proc/{request.Key.Pid}/io"));
        break;
      case ElevatedOpcode.ReadCmdline:
        Respond(output, ReadFile($"/proc/{request.Key.Pid}/cmdline"));
        break;
      case ElevatedOpcode.ReadEnviron:
        Respond(output, ReadFile($"/proc/{request.Key.Pid}/environ"));
        break;
      case ElevatedOpcode.ListFds:
        Respond(output, ListDescriptors(request.Key.Pid));
        break;
      case ElevatedOpcode.Terminate:
        Respond(output, Signal(request.Key.Pid, 15));
        break;
      case ElevatedOpcode.Suspend:
        Respond(output, Signal(request.Key.Pid, 19));
        break;
      case ElevatedOpcode.Resume:
        Respond(output, Signal(request.Key.Pid, 18));
        break;
      case ElevatedOpcode.SetPriority:
        Respond(output, SetPriority(request.Key.Pid, (int)request.Argument));
        break;
      case ElevatedOpcode.SetAffinity:
        // A mask with no cores in it would leave the process nothing to run on, and the helper is
        // the last place that can still refuse it.
        Respond(output, request.Argument == 0
          ? new(ElevatedStatus.Malformed, null)
          : SetAffinity(request.Key.Pid, (ulong)request.Argument));
        break;
      default:
        ElevatedProtocol.WriteResponse(output, ElevatedStatus.UnknownOpcode);
        break;
    }
  }

  private readonly record struct Result(ElevatedStatus Status, byte[]? Payload);

  private static void Respond(Stream output, Result result)
    => ElevatedProtocol.WriteResponse(output, result.Status, result.Payload ?? []);

  private static Result ReadFile(string path) {
    try {
      var bytes = File.ReadAllBytes(path);
      return bytes.Length > ElevatedProtocol.MaxFrameLength - 16
        ? new(ElevatedStatus.Ok, bytes[..(ElevatedProtocol.MaxFrameLength - 16)])
        : new(ElevatedStatus.Ok, bytes);
    } catch (UnauthorizedAccessException) {
      return new(ElevatedStatus.NotPermitted, null);
    } catch (IOException) {
      return new(ElevatedStatus.ProcessExited, null);
    }
  }

  /// <summary>
  /// A file that is worth nothing in part, so a frame too small for it is a refusal rather than a
  /// truncation.
  /// </summary>
  /// <remarks>
  /// <see cref="ReadFile"/> cuts a long file short, which is right for a descriptor listing and wrong
  /// for a structure table: half a table parses cleanly and reports half the memory slots, and
  /// nothing downstream could tell that from a machine with half as many.
  /// </remarks>
  private static Result ReadWholeFile(string path) {
    try {
      var bytes = File.ReadAllBytes(path);
      return bytes.Length > ElevatedProtocol.MaxFrameLength - 16
        ? new(ElevatedStatus.Failed, null)
        : new(ElevatedStatus.Ok, bytes);
    } catch (UnauthorizedAccessException) {
      return new(ElevatedStatus.NotPermitted, null);
    } catch (IOException) {
      return new(ElevatedStatus.Failed, null);
    }
  }

  private static Result ListDescriptors(int pid) {
    try {
      var builder = new StringBuilder();
      foreach (var entry in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd")) {
        var target = ResolveLink(entry);
        builder.Append(Path.GetFileName(entry)).Append('\t').Append(target ?? string.Empty).Append('\n');
        if (builder.Length > ElevatedProtocol.MaxFrameLength - 16)
          break;
      }

      return new(ElevatedStatus.Ok, Encoding.UTF8.GetBytes(builder.ToString()));
    } catch (UnauthorizedAccessException) {
      return new(ElevatedStatus.NotPermitted, null);
    } catch (IOException) {
      return new(ElevatedStatus.ProcessExited, null);
    }
  }

  private static string? ResolveLink(string path) {
    try {
      return File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  private static Result Signal(int pid, int signal)
    => Native.Kill(pid, signal) == 0 ? new(ElevatedStatus.Ok, null) : Translate();

  private static Result SetPriority(int pid, int nice)
    => Native.SetPriority(0, (uint)pid, nice) == 0 ? new(ElevatedStatus.Ok, null) : Translate();

  private static Result SetAffinity(int pid, ulong mask)
    => Native.SchedSetAffinity(pid, sizeof(ulong), ref mask) == 0 ? new(ElevatedStatus.Ok, null) : Translate();

  private static Result Translate() => new(
    System.Runtime.InteropServices.Marshal.GetLastPInvokeError() switch {
      1 or 13 => ElevatedStatus.NotPermitted,
      3 => ElevatedStatus.ProcessExited,
      _ => ElevatedStatus.Failed,
    },
    null
  );

}
