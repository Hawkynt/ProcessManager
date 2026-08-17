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
    UnknownReason.ProcessExited => "×",
    UnknownReason.NotSampledYet => "…",
    UnknownReason.CounterInvalid => "?",
    _ => string.Empty,
  };

  /// <summary>A one-line explanation of a placeholder, for tooltips and the detail pane.</summary>
  public static string Explain(UnknownReason reason) => reason switch {
    UnknownReason.NotPermitted => "Not readable as this user; start the elevated helper to see it.",
    UnknownReason.NotSupportedOnPlatform => "This operating system does not report this value.",
    UnknownReason.ProcessExited => "The process ended while it was being read.",
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

  /// <summary>A CPU-time total as <c>h:mm:ss</c>, the way top and Process Explorer show it.</summary>
  public static string Duration(Counter nanoseconds) {
    if (!nanoseconds.HasValue)
      return Placeholder(nanoseconds.Reason);

    var span = TimeSpan.FromSeconds(nanoseconds.Value / 1_000_000_000d);
    return span.TotalHours >= 1
      ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
      : $"{span.Minutes}:{span.Seconds:00}";
  }

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
