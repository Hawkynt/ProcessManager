using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Number-to-text, shared by both front-ends so a value reads the same in the window and in the
/// terminal. Every method has an overload taking a <see cref="Counter"/> or a <see cref="Rate"/>,
/// because rendering the reason a value is missing is the point of §3.4 and doing it at every call
/// site is how it gets forgotten.
/// </summary>
public static class Humanize {

  /// <summary>What the UI shows where a value would be, per <see cref="UnknownReason"/>.</summary>
  public static string Placeholder(UnknownReason reason) => reason switch {
    UnknownReason.NotPermitted => "—",
    UnknownReason.NotSupportedOnPlatform => "n/a",
    UnknownReason.NotImplementedHere => "n/i",
    UnknownReason.ProcessExited => "×",
    UnknownReason.SourceGone => "gone",
    UnknownReason.NotSampledYet => "…",
    UnknownReason.CounterInvalid => "?",
    _ => string.Empty,
  };

  /// <summary>
  /// A controlling terminal, from the device number <c>stat</c> packs it into.
  /// </summary>
  /// <remarks>
  /// Zero means no controlling terminal, which is the answer for every daemon and every service and
  /// so for most of a machine's process table — a dash rather than "0", because 0 is not a device.
  /// <para>
  /// The packing is the awkward part: minor is split across the low eight bits and bits 20–31, with
  /// major in between, which is how Linux has encoded <c>dev_t</c> since it ran out of minor numbers.
  /// Major 136 is the pseudo-terminal range, where the minor is the number after <c>pts/</c>.
  /// </para>
  /// </remarks>
  public static string Terminal(int device) {
    if (device == 0)
      return "—";

    var raw = (uint)device;
    var major = (int)((raw >> 8) & 0xFFF);
    var minor = (int)((raw & 0xFF) | ((raw >> 12) & 0xFFF00));

    return major switch {
      136 => $"pts/{minor.ToString(CultureInfo.InvariantCulture)}",
      4 when minor < 64 => $"tty{minor.ToString(CultureInfo.InvariantCulture)}",
      4 => $"ttyS{(minor - 64).ToString(CultureInfo.InvariantCulture)}",
      _ => $"{major.ToString(CultureInfo.InvariantCulture)}:{minor.ToString(CultureInfo.InvariantCulture)}",
    };
  }

  /// <summary>
  /// The container a cgroup path belongs to, or null for a process that is simply on the machine.
  /// </summary>
  /// <remarks>
  /// Every runtime writes its own shape and they all bury a long hexadecimal id somewhere:
  /// <c>/docker/&lt;64 hex&gt;</c>, <c>/kubepods/.../docker-&lt;64 hex&gt;.scope</c>,
  /// <c>/system.slice/docker-&lt;64 hex&gt;.scope</c>, and podman's <c>libpod-&lt;64 hex&gt;</c>.
  /// Rather than matching each layout — there is always another — this looks for the id itself and
  /// shortens it to the twelve characters every one of those tools prints.
  /// <para>
  /// A systemd slice is not a container and must not be reported as one, which is why a run of hex
  /// has to be long enough to be an id rather than merely present.
  /// </para>
  /// </remarks>
  public static string? ContainerId(string? cgroupPath) {
    if (cgroupPath is not { Length: > 0 } path)
      return null;

    var start = -1;
    for (var i = 0; i <= path.Length; ++i) {
      var isHex = i < path.Length && Uri.IsHexDigit(path[i]);
      if (isHex) {
        if (start < 0)
          start = i;

        continue;
      }

      if (start >= 0 && i - start >= 32)
        return path[start..(start + 12)];

      start = -1;
    }

    return null;
  }

  /// <summary>A one-line explanation of a placeholder, for tooltips and the detail pane.</summary>
  public static string Explain(UnknownReason reason) => reason switch {
    UnknownReason.NotPermitted => "Not readable as this user; start the elevated helper to see it.",
    UnknownReason.NotSupportedOnPlatform => "This operating system does not report this value.",
    UnknownReason.NotImplementedHere => "This operating system reports it; ProcessManager does not read it yet.",
    UnknownReason.ProcessExited => "The process ended while it was being read.",
    UnknownReason.SourceGone => "What this describes no longer exists — a deleted file that is still mapped.",
    UnknownReason.NotSampledYet => "Needs a second sample; wait one interval.",
    UnknownReason.CounterInvalid => "The counter moved backwards or the interval was zero.",
    _ => string.Empty,
  };

  private static readonly string[] _byteUnits = ["B", "K", "M", "G", "T", "P"];

  /// <summary>Bytes as a short, column-friendly string: <c>4096</c> becomes <c>4.0K</c>.</summary>
  public static string Bytes(ulong value) {
    if (value < 1024)
      return value.ToString(CultureInfo.InvariantCulture) + " B";

    double scaled = value;
    var unit = 0;
    while (scaled >= 1024 && unit < _byteUnits.Length - 1) {
      scaled /= 1024;
      ++unit;
    }

    return scaled.ToString(scaled >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + _byteUnits[unit];
  }

  public static string Bytes(Counter counter)
    => counter.HasValue ? Bytes(counter.Value) : Placeholder(counter.Reason);

  /// <summary>Bytes per second, as a rate rather than a size: <c>1536</c> becomes <c>1.5K/s</c>.</summary>
  public static string BytesPerSecond(Rate rate) {
    if (!rate.HasValue)
      return Placeholder(rate.Reason);

    var value = rate.Value;
    // Anything under a byte per second rounds to nothing; showing "0.4 B/s" is noise, and showing
    // "0" where nothing happened is the truth.
    return value < 1 ? "0" : Bytes((ulong)value) + "/s";
  }

  /// <summary>A plain per-second count — page faults, context switches, cycles.</summary>
  public static string Rate(Rate rate) {
    if (!rate.HasValue)
      return Placeholder(rate.Reason);

    var value = rate.Value;
    if (value < 1000)
      return value.ToString("0", CultureInfo.InvariantCulture);

    // Counts, not bytes, so the steps are thousands rather than 1024s — a cycles-per-second figure
    // in kibicycles would be nobody's idea of a reading.
    foreach (var (limit, suffix) in (ReadOnlySpan<(double, string)>)[
      (1e12, "T"), (1e9, "G"), (1e6, "M"), (1e3, "k"),
    ])
      if (value >= limit)
        return (value / limit).ToString(value / limit >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + suffix;

    return value.ToString("0", CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// A byte rate that may be negative, for the columns whose fall is the interesting half.
  /// </summary>
  public static string SignedBytesPerSecond(Rate rate) {
    if (!rate.HasValue)
      return Placeholder(rate.Reason);

    var value = rate.Value;
    if (Math.Abs(value) < 1)
      return "0";

    var magnitude = Bytes((ulong)Math.Abs(value));
    return (value < 0 ? "−" : "+") + magnitude + "/s";
  }

  /// <summary>A percentage with one decimal, or the placeholder.</summary>
  public static string Percent(Rate rate) => rate.HasValue
    ? rate.Value.ToString(rate.Value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)
    : Placeholder(rate.Reason);

  public static string Count(Counter counter) => counter.HasValue
    ? counter.Value.ToString(CultureInfo.InvariantCulture)
    : Placeholder(counter.Reason);

  /// <summary>
  /// Two halves of one reading in a single cell, as <c>a / b</c>.
  /// </summary>
  /// <remarks>
  /// Each half keeps its own placeholder rather than the pair collapsing to one: a platform that
  /// counts context switches but does not split them has a total and no halves, and "n/a / n/a"
  /// beside a real total is the honest way to say so (PRD §72.3).
  /// </remarks>
  public static string Pair(Counter first, Counter second) => Count(first) + " / " + Count(second);

  /// <summary>A CPU-time total as <c>h:mm:ss</c>, the way top and Process Explorer show it.</summary>
  public static string Duration(Counter nanoseconds) {
    if (!nanoseconds.HasValue)
      return Placeholder(nanoseconds.Reason);

    var span = TimeSpan.FromSeconds(nanoseconds.Value / 1_000_000_000d);
    return span.TotalHours >= 1
      ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
      : $"{span.Minutes}:{span.Seconds:00}";
  }

  /// <summary>An address in the notation every debugger and every map file writes it in.</summary>
  public static string Address(ulong value)
    => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

  public static string Address(Counter counter) => counter.HasValue
    ? Address(counter.Value)
    : Placeholder(counter.Reason);

  /// <summary>What an image says it is, in words rather than in an enumeration member's spelling.</summary>
  public static string ImageType(ModuleType type) => type switch {
    ModuleType.Executable => "executable",
    ModuleType.SharedObject => "shared object",
    ModuleType.Relocatable => "relocatable",
    ModuleType.CoreDump => "core dump",
    ModuleType.Data => "data",
    _ => "—",
  };

  /// <summary>
  /// A point in time as local <c>yyyy-MM-dd HH:mm:ss</c>, or the em dash when there is none.
  /// </summary>
  /// <remarks>
  /// Sortable rather than pretty, and local rather than UTC: the reader is comparing a start time
  /// against their own log files, which are in their own timezone.
  /// </remarks>
  public static string Timestamp(long utcTicks) => utcTicks > 0
    ? new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime()
      .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
    : "—";

  /// <summary>
  /// A scheduler class under the kernel's own name, because that is what a person will search for.
  /// </summary>
  /// <remarks>
  /// "SCHED_FIFO" is greppable, matches <c>chrt</c> and matches every manual page; "First in, first
  /// out" is none of those things (PRD §5.3).
  /// </remarks>
  public static string SchedulingPolicy(Model.SchedulingPolicy policy) => policy switch {
    Model.SchedulingPolicy.Other => "SCHED_OTHER",
    Model.SchedulingPolicy.Fifo => "SCHED_FIFO",
    Model.SchedulingPolicy.RoundRobin => "SCHED_RR",
    Model.SchedulingPolicy.Batch => "SCHED_BATCH",
    Model.SchedulingPolicy.Idle => "SCHED_IDLE",
    Model.SchedulingPolicy.Deadline => "SCHED_DEADLINE",
    Model.SchedulingPolicy.Extensible => "SCHED_EXT",
    _ => "—",
  };

  /// <summary>
  /// The end a socket is bound to (PRD §40).
  /// </summary>
  /// <remarks>
  /// A Unix socket is named by a path and has no port, so it is not given a <c>:0</c> that would
  /// read as one. An unnamed one is what both halves of a <c>socketpair</c> look like: real,
  /// connected, and bound to nothing anybody can name.
  /// </remarks>
  public static string LocalEndpoint(in ConnectionRecord connection)
    => connection.Protocol != ConnectionProtocol.Unix
      ? Endpoint(connection.LocalAddress, connection.LocalPort)
      : connection.LocalAddress.Length > 0 ? connection.LocalAddress : "<unnamed>";

  /// <summary>
  /// The end a socket is talking to, where there is one.
  /// </summary>
  /// <remarks>
  /// The kernel keeps a Unix socket's peer but does not publish it in the table this is read from,
  /// so that column says "no such thing here" rather than "not connected" — one is a fact about the
  /// interface, the other a claim about the socket (PRD §5.3).
  /// </remarks>
  public static string RemoteEndpoint(in ConnectionRecord connection)
    => connection.Protocol == ConnectionProtocol.Unix
      ? "n/a"
      : connection.RemotePort == 0 ? "—" : Endpoint(connection.RemoteAddress, connection.RemotePort);

  /// <summary>
  /// An address and a port.
  /// </summary>
  /// <remarks>
  /// The brackets are not decoration: an IPv6 address is full of colons, so <c>fe80::1:22</c> could
  /// be port 22 on <c>fe80::1</c> or no port at all on <c>fe80::1:22</c>. Everything that writes
  /// these — the URL syntax, ss, netstat — brackets the address for that reason.
  /// </remarks>
  private static string Endpoint(string address, int port)
    => address.Contains(':', StringComparison.Ordinal)
      ? $"[{address}]:{port}"
      : $"{address}:{port}";

  /// <summary>Who the kernel charges a socket to, by name where the machine knows one.</summary>
  public static string SocketUser(in ConnectionRecord connection)
    => connection.UserName
      ?? (connection.UserId >= 0 ? connection.UserId.ToString(CultureInfo.InvariantCulture) : "n/a");

  /// <summary>What a socket delivers, or a dash where the platform did not say.</summary>
  public static string SocketKindName(SocketKind kind) => kind == SocketKind.Unknown ? "—" : kind.ToString();

  /// <summary>
  /// The kind of a handle or descriptor, in the platform's own vocabulary (PRD §5.3).
  /// </summary>
  /// <remarks>
  /// Not <c>Kind.ToString()</c>: the enumeration is shared across platforms and its member names are
  /// a compromise between them, whereas the reader is looking at one machine and already knows what
  /// the thing in front of them is called there.
  /// </remarks>
  public static string ResourceKind(HandleKind kind) => kind switch {
    HandleKind.File => "file",
    HandleKind.Directory => "directory",
    HandleKind.Socket => "socket",
    HandleKind.Pipe => "pipe",
    HandleKind.Event => "eventfd",
    HandleKind.EventPoll => "epoll",
    HandleKind.Timer => "timerfd",
    HandleKind.Signal => "signalfd",
    HandleKind.Notify => "notify",
    HandleKind.SharedMemory => "shared memory",
    HandleKind.Device => "device",
    HandleKind.Process => "process",
    HandleKind.Thread => "thread",
    HandleKind.Mutex => "mutex",
    HandleKind.Section => "section",
    HandleKind.Key => "key",
    HandleKind.AnonInode => "kernel object",
    _ => "—",
  };

  public static string State(ProcessState state) => state switch {
    ProcessState.Running => "run",
    ProcessState.Sleeping => "sleep",
    ProcessState.DiskSleep => "disk",
    ProcessState.Stopped => "stop",
    ProcessState.Zombie => "zombie",
    ProcessState.Traced => "traced",
    ProcessState.Idle => "idle",
    ProcessState.Dead => "dead",
    _ => "?",
  };

}
