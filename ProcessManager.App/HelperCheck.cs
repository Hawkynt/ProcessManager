using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// Talks to the privileged helper over its real pipe, without elevating it (PRD §8, §9.8).
/// </summary>
/// <remarks>
/// <para>
/// The protocol tests exercise the parser against every malformed frame anyone could think of. This
/// exercises the other half — that the helper starts, that the pipe carries frames in both
/// directions, that a well-formed request produces the file it names, and that the identity check
/// refuses a request for the same pid with the wrong start time.
/// </para>
/// <para>
/// It runs the helper <em>unelevated</em> on purpose. Everything above is true whether or not the
/// process is root, and a check that needs a password prompt is a check nobody runs and CI cannot
/// run at all. What it deliberately does not prove is that <c>pkexec</c> is configured — that needs
/// a polkit policy installed on the machine, and its absence is reported by the channel rather than
/// hidden.
/// </para>
/// </remarks>
internal static class HelperCheck {

  public static int Run() {
    if (!OperatingSystem.IsLinux()) {
      Console.WriteLine("the helper is Linux-only so far (PRD §8, milestone M7); nothing to check here.");
      return 0;
    }

    var path = FindHelper();
    if (path is null) {
      Console.Error.WriteLine("procman: procman-helper was not found next to procman.");
      return 1;
    }

    Console.WriteLine($"helper: {path} (started unelevated)");
    using var channel = new ElevatedChannel(path, useElevation: false);
    if (!channel.Start()) {
      Console.Error.WriteLine($"procman: {channel.Unavailable}");
      return 1;
    }

    var failures = 0;
    var self = ReadOwnKey();
    Console.WriteLine($"target: pid {self.Pid}, start {self.StartTicks}");
    Console.WriteLine();

    // The happy path: a file only the helper is asked for, about a process it just validated.
    var (status, payload) = channel.Send(ElevatedOpcode.ReadProcIo, self);
    Check("ReadProcIo", status == ElevatedStatus.Ok && payload.Length > 0,
      status == ElevatedStatus.Ok ? $"{payload.Length} bytes" : status.ToString(), ref failures);

    (status, payload) = channel.Send(ElevatedOpcode.ReadCmdline, self);
    Check("ReadCmdline", status == ElevatedStatus.Ok && payload.Length > 0,
      status == ElevatedStatus.Ok ? ElevatedProtocol.DecodePayload(payload).Replace('\0', ' ').Trim() : status.ToString(),
      ref failures);

    (status, payload) = channel.Send(ElevatedOpcode.ListFds, self);
    Check("ListFds", status == ElevatedStatus.Ok && payload.Length > 0,
      status == ElevatedStatus.Ok ? $"{ElevatedProtocol.DecodePayload(payload).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} descriptors" : status.ToString(),
      ref failures);

    // The check that matters: the same pid with the wrong start time is a different process, and the
    // helper must refuse it rather than act with whatever authority it has.
    (status, _) = channel.Send(ElevatedOpcode.Terminate, new(self.Pid, self.StartTicks + 1));
    Check("identity mismatch refused", status == ElevatedStatus.IdentityMismatch, status.ToString(), ref failures);

    // A pid that is not there.
    (status, _) = channel.Send(ElevatedOpcode.ReadProcIo, new(0x7FFF_FFFE, 1));
    Check("missing process refused", status is ElevatedStatus.ProcessExited or ElevatedStatus.NotPermitted,
      status.ToString(), ref failures);

    // An affinity mask with no cores in it, refused at the last place that can refuse it.
    (status, _) = channel.Send(ElevatedOpcode.SetAffinity, self, 0);
    Check("empty affinity refused", status == ElevatedStatus.Malformed, status.ToString(), ref failures);

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "OK: the helper answers and refuses what it should." : $"{failures} check(s) failed.");
    return failures == 0 ? 0 : 1;
  }

  private static void Check(string name, bool ok, string detail, ref int failures) {
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name,-26} {detail}");
    if (!ok)
      ++failures;
  }

  private static ProcessKey ReadOwnKey() {
    var stat = File.ReadAllText("/proc/self/stat");
    var close = stat.LastIndexOf(')');
    var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return new(Environment.ProcessId, ulong.Parse(fields[19]));
  }

  private static string? FindHelper() {
    var directory = AppContext.BaseDirectory;
    foreach (var name in (ReadOnlySpan<string>)["procman-helper", "procman-helper.exe"]) {
      var candidate = Path.Combine(directory, name);
      if (File.Exists(candidate))
        return candidate;
    }

    // The development layout, where each project builds into its own bin.
    var sibling = Path.GetFullPath(Path.Combine(
      directory, "..", "..", "..", "..", "ProcessManager.Elevated", "bin", "Release", "net10.0", "procman-helper"
    ));

    return File.Exists(sibling) ? sibling : null;
  }

}
