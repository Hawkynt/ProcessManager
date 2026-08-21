using System.Globalization;
using System.Text;
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
    UnknownReason.NoLimit => "∞",
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

  /// <summary>
  /// How many decimals a percentage is written with (PRD §15, §67).
  /// </summary>
  /// <remarks>
  /// <para>
  /// A setting rather than a constant because the right answer depends on what is being watched: one
  /// decimal is right for a table of processes, and nought is right for anybody who wants the column
  /// to stop flickering, and two is right for somebody chasing a process that uses a twentieth of a
  /// core. Held here rather than passed to every call because both front-ends, the performance page
  /// and the exporter all render through this class, and a parameter would be a parameter to forget
  /// at one of them — which is how a program comes to print two conventions.
  /// </para>
  /// <para>
  /// It governs <em>every</em> percentage and not only the processor's, deliberately: a window that
  /// wrote CPU to two decimals and memory to one would be stating a precision about one of them that
  /// it does not have about the other.
  /// </para>
  /// <para>
  /// Nought to three. Beyond three the digits are noise — the counters underneath are sampled a
  /// second apart — and a negative is a typo rather than a preference.
  /// </para>
  /// </remarks>
  public static int PercentDecimals {
    get;
    set => field = Math.Clamp(value, 0, MaximumPercentDecimals);
  } = DefaultPercentDecimals;

  public const int DefaultPercentDecimals = 1;
  public const int MaximumPercentDecimals = 3;

  /// <summary>
  /// The format a percentage of this magnitude is written in.
  /// </summary>
  /// <remarks>
  /// One digit fewer once the value reaches a hundred, so the column keeps its width where it is
  /// widest — which is what this has always done at the default, where the rule reads as "100" and
  /// "99.9". A per-core figure is the one that routinely passes a hundred, and it is also the one
  /// whose third significant digit nobody is reading.
  /// </remarks>
  private static string PercentFormat(double value) {
    var decimals = PercentDecimals;
    if (Math.Abs(value) >= 100 && decimals > 0)
      --decimals;

    return decimals switch { 0 => "0", 1 => "0.0", 2 => "0.00", _ => "0.000" };
  }

  /// <summary>A percentage at the configured precision, or the placeholder.</summary>
  public static string Percent(Rate rate) => rate.HasValue
    ? rate.Value.ToString(PercentFormat(rate.Value), CultureInfo.InvariantCulture)
    : Placeholder(rate.Reason);

  /// <summary>
  /// A change in percentage points, with its sign — <c>+3.4</c>, <c>−1.0</c>, or <c>0</c>.
  /// </summary>
  /// <remarks>
  /// Signed like <see cref="SignedBytesPerSecond"/> and for the same reason: the reading that
  /// matters is as often the fall as the rise, and a magnitude with no sign turns a process that
  /// has just stopped working into one that has just started. Anything that would round to nothing at
  /// the configured precision is written as a plain nought, so a table of idle processes is not a
  /// column of <c>+0.0</c>.
  /// </remarks>
  public static string SignedPercent(Rate rate) {
    if (!rate.HasValue)
      return Placeholder(rate.Reason);

    var value = rate.Value;
    var magnitude = Math.Abs(value);
    // Half of the last digit the format will print — so the threshold follows the precision rather
    // than staying at the tenth it was fixed at, which at two decimals would have written "0" over
    // a change the column had room to show.
    if (magnitude < 0.5 * Math.Pow(10, -PercentDecimals))
      return "0";

    return (value < 0 ? "−" : "+") + magnitude.ToString(PercentFormat(magnitude), CultureInfo.InvariantCulture);
  }

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
  /// Why an image is in the process, in three or four characters of table (PRD §31).
  /// </summary>
  /// <remarks>
  /// "run time" rather than "dlopen" on purpose. All the graph knows is that nothing it could read
  /// names this library; <c>dlopen</c> is the usual explanation and <c>LD_PRELOAD</c> is another,
  /// and naming the mechanism would claim to have seen the call (PRD §5.3, §72.3).
  /// </remarks>
  public static string LoadReason(ModuleLoadReason reason) => reason switch {
    ModuleLoadReason.Image => "image",
    ModuleLoadReason.Interpreter => "loader",
    ModuleLoadReason.Direct => "linked",
    ModuleLoadReason.Dependency => "indirect",
    ModuleLoadReason.RunTime => "run time",
    ModuleLoadReason.Data => "data",
    _ => "—",
  };

  /// <summary>
  /// Which execution engine reads a mapped file (PRD §31).
  /// </summary>
  /// <remarks>
  /// "not code" rather than an empty cell, because it is an answer: the file was read and it is a
  /// font, a locale archive or a database. The dash above it is the other case — nobody read it —
  /// and the two must not look the same (PRD §72.3).
  /// </remarks>
  public static string ImageRuntime(ModuleRuntime runtime) => runtime switch {
    ModuleRuntime.Native => "native",
    ModuleRuntime.Managed => ".NET",
    ModuleRuntime.WindowsNative => "windows",
    ModuleRuntime.Archive => "archive",
    ModuleRuntime.NotCode => "not code",
    _ => "—",
  };

  /// <summary>
  /// How many times one file is loaded into one process (PRD §31).
  /// </summary>
  /// <remarks>
  /// A row exists because a mapping named the file, so the smallest true answer is one and a nought
  /// means the pass that counts them never ran — which renders as the reason and not as a zero
  /// somebody could read as "not loaded" (PRD §72.3).
  /// </remarks>
  public static string LoadCount(int loads) => loads > 0
    ? loads.ToString(CultureInfo.InvariantCulture)
    : Placeholder(UnknownReason.NotSampledYet);

  /// <summary>
  /// The hardening an image asks for, as the short names the tools that check it use (PRD §31).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The vocabulary is <c>checksec</c>'s and <c>readelf</c>'s, because that is what somebody will
  /// compare this against: <c>PIE</c>, <c>NX</c>, <c>RELRO</c>, and <c>CET</c> for the pair of x86
  /// features that are always turned on together. An executable stack is named rather than left out,
  /// since it is the one value here that is a finding.
  /// </para>
  /// <para>
  /// An image whose headers were never read renders as the ellipsis and not as an empty list: "this
  /// file asks for nothing" is a serious claim and is not what an unread file supports (PRD §72.3).
  /// </para>
  /// </remarks>
  public static string Mitigations(ImageMitigations mitigations) {
    if ((mitigations & ImageMitigations.Read) == 0)
      return Placeholder(UnknownReason.NotSampledYet);

    var text = new System.Text.StringBuilder();
    Append((mitigations & ImageMitigations.PositionIndependent) != 0, "PIE");
    Append((mitigations & ImageMitigations.ExecutableStack) != 0, "X-STACK");
    Append((mitigations & ImageMitigations.NonExecutableStack) != 0, "NX");
    Append(
      (mitigations & ImageMitigations.Relro) != 0,
      (mitigations & ImageMitigations.BindNow) != 0 ? "RELRO+NOW" : "RELRO"
    );

    // Only when the relocations are not protected is eager binding worth naming on its own: it is a
    // request for the whole table to be resolved before main, which without RELRO leaves it writable
    // afterwards.
    Append((mitigations & (ImageMitigations.Relro | ImageMitigations.BindNow)) == ImageMitigations.BindNow, "NOW");
    Append(
      (mitigations & (ImageMitigations.IndirectBranchTracking | ImageMitigations.ShadowStack))
        == (ImageMitigations.IndirectBranchTracking | ImageMitigations.ShadowStack),
      "CET"
    );

    // And each half on its own, for the image that has one and not the other — which is what a
    // library built by an older toolchain into a CET program looks like.
    Append(
      (mitigations & ImageMitigations.IndirectBranchTracking) != 0 && (mitigations & ImageMitigations.ShadowStack) == 0,
      "IBT"
    );

    Append(
      (mitigations & ImageMitigations.ShadowStack) != 0 && (mitigations & ImageMitigations.IndirectBranchTracking) == 0,
      "SHSTK"
    );

    Append((mitigations & ImageMitigations.BranchTargetIdentification) != 0, "BTI");
    Append((mitigations & ImageMitigations.PointerAuthentication) != 0, "PAC");
    // Read, and asking for nothing at all. A real answer about a real file, and a rare one.
    return text.Length > 0 ? text.ToString() : "none";

    void Append(bool present, string name) {
      if (!present)
        return;

      if (text.Length > 0)
        text.Append(' ');

      text.Append(name);
    }
  }

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
  /// The same two ends, with the port named and the address resolved where that is possible.
  /// </summary>
  /// <remarks>
  /// <para>
  /// "443" and "https" are the same fact and only one of them can be read at a glance, so a port is
  /// named whenever the machine's own <c>/etc/services</c> names it — the default, and what
  /// <c>ss</c> and <c>netstat</c> both do.
  /// </para>
  /// <para>
  /// An address is a different matter: resolving it asks somebody else a question, so it happens
  /// only when a name is already known, and <see cref="HostnameCache"/> knows nothing until it is
  /// switched on. Either way the number and the address remain available in the record itself —
  /// what is shown is a nicety, and what is exported stays exact (PRD §76).
  /// </para>
  /// </remarks>
  public static string LocalEndpoint(in ConnectionRecord connection, ServiceNames? services, HostnameCache? hosts)
    => connection.Protocol == ConnectionProtocol.Unix
      ? LocalEndpoint(in connection)
      : Endpoint(
          hosts?.Lookup(connection.LocalAddress) ?? connection.LocalAddress,
          Port(connection.LocalPort, in connection, services)
        );

  /// <inheritdoc cref="LocalEndpoint(in ConnectionRecord, ServiceNames?, HostnameCache?)"/>
  public static string RemoteEndpoint(in ConnectionRecord connection, ServiceNames? services, HostnameCache? hosts)
    => connection.Protocol == ConnectionProtocol.Unix || connection.RemotePort == 0
      ? RemoteEndpoint(in connection)
      : Endpoint(
          hosts?.Lookup(connection.RemoteAddress) ?? connection.RemoteAddress,
          Port(connection.RemotePort, in connection, services)
        );

  /// <summary>
  /// A port as its name, where the machine declares one for that protocol.
  /// </summary>
  /// <remarks>
  /// Port zero is not a port. A socket bound to it has asked the kernel to choose, and on a listening
  /// socket that has not happened yet — naming it would be naming nothing.
  /// </remarks>
  private static string Port(int port, in ConnectionRecord connection, ServiceNames? services) {
    if (port == 0 || services is null)
      return port.ToString(CultureInfo.InvariantCulture);

    var datagram = connection.Protocol is ConnectionProtocol.Udp or ConnectionProtocol.Udp6;
    return services.Describe(port, datagram);
  }

  /// <summary>
  /// An address and a port.
  /// </summary>
  /// <remarks>
  /// The brackets are not decoration: an IPv6 address is full of colons, so <c>fe80::1:22</c> could
  /// be port 22 on <c>fe80::1</c> or no port at all on <c>fe80::1:22</c>. Everything that writes
  /// these — the URL syntax, ss, netstat — brackets the address for that reason.
  /// </remarks>
  private static string Endpoint(string address, string port)
    => address.Contains(':', StringComparison.Ordinal)
      ? $"[{address}]:{port}"
      : $"{address}:{port}";

  private static string Endpoint(string address, int port)
    => address.Contains(':', StringComparison.Ordinal)
      ? $"[{address}]:{port}"
      : $"{address}:{port}";

  /// <summary>
  /// The host out of an endpoint this class drew, without its port, or null where the endpoint names
  /// no host at all (PRD §40).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Deliberately the drawn cell rather than the record it came from, and deliberately in the same
  /// file as the code that wrote it. A connection can close between a right-click and a menu choice,
  /// so what somebody asked to search for has to be what they were looking at — the same reason
  /// "copy endpoint" takes its text from the table. Writer and reader sit together, with one test
  /// holding them to each other.
  /// </para>
  /// <para>
  /// The bracket is what makes this possible at all: <c>fe80::1:22</c> is ambiguous and
  /// <c>[fe80::1]:22</c> is not, which is why every tool that writes these brackets an IPv6 address.
  /// </para>
  /// <para>
  /// The placeholders come back as null rather than as themselves. <c>—</c> is a listening socket
  /// with no peer, <c>n/a</c> is a Unix socket, which has no address of the kind a search means, and
  /// neither is a term anybody wants a browser opened for.
  /// </para>
  /// <para>
  /// A Unix socket's <em>local</em> end is a filesystem path with no port on it, and comes back
  /// whole. That is the right answer to "which part of this is not the port" and is not a host; the
  /// remote column is the one this is used on, and for a Unix socket that column is a placeholder.
  /// </para>
  /// </remarks>
  public static string? EndpointHost(string? endpoint) {
    if (endpoint is not { Length: > 0 } text)
      return null;

    string host;
    if (text[0] == '[') {
      var close = text.IndexOf(']', StringComparison.Ordinal);
      if (close < 0)
        return null;

      host = text[1..close];
    } else {
      var colon = text.LastIndexOf(':');
      host = colon < 0 ? text : text[..colon];
    }

    return host.Length > 0 && host is not ("—" or "n/a" or "*" or "<unnamed>") ? host : null;
  }

  /// <summary>Who the kernel charges a socket to, by name where the machine knows one.</summary>
  public static string SocketUser(in ConnectionRecord connection)
    => connection.UserName
      ?? (connection.UserId >= 0 ? connection.UserId.ToString(CultureInfo.InvariantCulture) : "n/a");

  /// <summary>What a socket delivers, or a dash where the platform did not say.</summary>
  public static string SocketKindName(SocketKind kind) => kind == SocketKind.Unknown ? "—" : kind.ToString();

  /// <summary>
  /// A connection's round-trip time, in the milliseconds everybody discusses latency in (PRD §40).
  /// </summary>
  /// <remarks>
  /// The kernel keeps it in microseconds and <c>ss</c> prints milliseconds, so this does too — a
  /// figure that can be read straight against a <c>ping</c> is worth more than one that is a
  /// thousand times larger for no reason the reader can see. Three decimals, because a loopback
  /// connection genuinely runs at eight microseconds and rounding it to zero would report the
  /// fastest path on the machine as no path at all.
  /// </remarks>
  public static string RoundTrip(Counter microseconds) => microseconds.HasValue
    ? (microseconds.Value / 1000d).ToString(microseconds.Value >= 100_000 ? "0" : "0.###", CultureInfo.InvariantCulture) + "ms"
    : Placeholder(microseconds.Reason);

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

  /// <summary>
  /// What the kernel says a descriptor's target is, with the device it is when it is one (PRD §32).
  /// </summary>
  /// <remarks>
  /// "no type" for an anonymous inode rather than a dash. The stat succeeded and the type bits were
  /// clear, which is a fact about eventfds and not about our access to them — and it is the answer a
  /// reader comparing this against <c>stat</c> will see there too (PRD §72.3).
  /// </remarks>
  public static string FileNode(FileNodeType type, string? device = null) => type switch {
    FileNodeType.Regular => "regular",
    FileNodeType.Directory => "directory",
    FileNodeType.CharacterDevice => device is { Length: > 0 } ? "character " + device : "character",
    FileNodeType.BlockDevice => device is { Length: > 0 } ? "block " + device : "block",
    FileNodeType.Fifo => "fifo",
    FileNodeType.Socket => "socket",
    FileNodeType.SymbolicLink => "symlink",
    FileNodeType.None => "no type",
    _ => "—",
  };

  /// <summary>
  /// A speculation mitigation state in the kernel's own words (PRD §21).
  /// </summary>
  /// <remarks>
  /// Not paraphrased. "thread vulnerable" is what <c>status</c> writes and what every write-up of
  /// the mitigation calls it, so it is what somebody will search the machine for; "unmitigated"
  /// would be this program's word and nobody else's (PRD §5.3).
  /// </remarks>
  public static string SpeculationState(Model.SpeculationState state) => state switch {
    Model.SpeculationState.Unknown => "unknown",
    Model.SpeculationState.NotVulnerable => "not vulnerable",
    Model.SpeculationState.GloballyMitigated => "globally mitigated",
    Model.SpeculationState.ThreadForceMitigated => "thread force mitigated",
    Model.SpeculationState.ThreadMitigated => "thread mitigated",
    Model.SpeculationState.ThreadVulnerable => "thread vulnerable",
    Model.SpeculationState.Vulnerable => "vulnerable",
    // Never rounded to the nearest word we happen to know: a mitigation state this build cannot
    // name might be anything, and towards safe is the one direction it must not be guessed.
    _ => "unrecognised",
  };

  /// <summary>The indirect-branch line, the same way and for the same reason.</summary>
  public static string IndirectBranchState(Model.IndirectBranchState state) => state switch {
    Model.IndirectBranchState.Unknown => "unknown",
    Model.IndirectBranchState.Unsupported => "unsupported",
    Model.IndirectBranchState.NotAffected => "not affected",
    Model.IndirectBranchState.AlwaysDisabled => "always disabled",
    Model.IndirectBranchState.ConditionalForceDisabled => "conditional force disabled",
    Model.IndirectBranchState.ConditionalDisabled => "conditional disabled",
    Model.IndirectBranchState.ConditionalEnabled => "conditional enabled",
    Model.IndirectBranchState.AlwaysEnabled => "always enabled",
    _ => "unrecognised",
  };

  /// <summary>
  /// The hardware protections switched on for a task, by the kernel's own token (PRD §21).
  /// </summary>
  /// <remarks>
  /// "none" is a real answer and the ordinary one — most processes on most machines have no shadow
  /// stack — and reads as a finding rather than as a hole, the same way the group list does. A
  /// feature the kernel named and this build cannot is reported as being there, because dropping it
  /// would under-report a protection.
  /// </remarks>
  public static string ThreadFeatures(ThreadSecurityFeatures features) {
    if (features == ThreadSecurityFeatures.None)
      return "none";

    var text = new StringBuilder();
    if ((features & ThreadSecurityFeatures.ShadowStack) != 0)
      text.Append("shstk");

    if ((features & ThreadSecurityFeatures.WriteableShadowStack) != 0)
      text.Append(text.Length > 0 ? ",wrss" : "wrss");

    if ((features & ThreadSecurityFeatures.Unnamed) != 0)
      text.Append(text.Length > 0 ? ",unnamed" : "unnamed");

    return text.ToString();
  }

  /// <summary>How hard a security module is applying a profile (PRD §21).</summary>
  /// <remarks>
  /// AppArmor's own vocabulary, so that a reader can put the word straight into <c>aa-complain</c>
  /// or search the machine's logs for it.
  /// </remarks>
  public static string ConfinementMode(LsmConfinementMode mode) => mode switch {
    LsmConfinementMode.Unconfined => "unconfined",
    LsmConfinementMode.Complain => "complain",
    LsmConfinementMode.Prompt => "prompt",
    LsmConfinementMode.Enforce => "enforce",
    LsmConfinementMode.Kill => "kill",
    _ => "unrecognised",
  };

  /// <summary>
  /// A file-creation mask the way it is written everywhere else: four octal digits (PRD §21).
  /// </summary>
  /// <remarks>
  /// Base eight, with the leading zero, because that is the form <c>umask</c> prints, the form every
  /// shell profile sets and the only form in which the bits are readable at a glance. The decimal
  /// eighteen is the same number and says nothing.
  /// </remarks>
  public static string Umask(Counter mask) => mask.HasValue
    ? "0" + Convert.ToString((long)mask.Value, 8).PadLeft(3, '0')
    : Placeholder(mask.Reason);

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
