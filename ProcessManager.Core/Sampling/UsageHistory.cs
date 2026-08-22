using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>
/// What one program has cost this machine, across every time it has been run (PRD §44).
/// </summary>
/// <param name="Application">The image path, which is what identifies a program across runs.</param>
/// <param name="CpuTimeNs">Processor time summed over every run, in nanoseconds.</param>
/// <param name="ReadBytes">Bytes read, summed the same way.</param>
/// <param name="WrittenBytes">Bytes written.</param>
/// <param name="PeakWorkingSetBytes">The largest working set any run of it ever held.</param>
/// <param name="MemoryByteSeconds">
/// Working set integrated over time, from which the average follows. Kept as the integral rather
/// than as a running mean, because a mean of means is not a mean: a run lasting a second and one
/// lasting a day would count equally, and the day is what the machine actually spent.
/// </param>
/// <param name="RuntimeSeconds">How long it has been running, summed over every run.</param>
/// <param name="Launches">
/// How many times it has been started. Counted by identity pair rather than by pid, so a program the
/// kernel gave a recycled number to is the same run and not a new one (PRD §8.2).
/// </param>
/// <param name="FirstSeenUtcTicks">When this record began, so a total has a period attached to it.</param>
/// <param name="LastSeenUtcTicks">The most recent sample it appeared in.</param>
public readonly record struct UsageRecord(
  string Application,
  ulong CpuTimeNs,
  ulong ReadBytes,
  ulong WrittenBytes,
  ulong PeakWorkingSetBytes,
  double MemoryByteSeconds,
  double RuntimeSeconds,
  int Launches,
  long FirstSeenUtcTicks,
  long LastSeenUtcTicks
) {

  /// <summary>Mean working set over the time it has been running, or nought where it never ran.</summary>
  public double AverageWorkingSetBytes
    => this.RuntimeSeconds > 0 ? this.MemoryByteSeconds / this.RuntimeSeconds : 0;

}

/// <summary>
/// A running total of what each program has cost, kept across sessions (PRD §44).
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless somebody asks.</b> A file recording which applications a person ran and for how long
/// is surveillance if it appears without being asked for, however useful it is when it is asked for
/// — so nothing here is fed unless the setting says so, and the file is not created until it is.
/// </para>
/// <para>
/// Keyed by the image path rather than by name. A name is what a program calls itself and two
/// programs may share one; the path is what was executed. It is not keyed by a digest either, which
/// would make an upgraded program a different application and lose everything the old one had done —
/// the question this answers is "what has this program cost me", and the answer survives its updates.
/// </para>
/// <para>
/// Fed from the interval rather than from the totals. A process's own <c>CpuTimeNs</c> is cumulative
/// since it started, so adding it every sample would count the same second once per sample; what is
/// added is the difference, which is what the delta already computed for the rate columns.
/// </para>
/// </remarks>
public sealed class UsageHistory {

  private readonly Dictionary<string, UsageRecord> _byApplication = new(StringComparer.Ordinal);

  /// <summary>The processes counted so far, so a second sample adds only the difference.</summary>
  private readonly Dictionary<ProcessKey, Consumed> _seen = [];

  private readonly record struct Consumed(ulong CpuTimeNs, ulong ReadBytes, ulong WrittenBytes);

  /// <summary>Every program this has a total for.</summary>
  public IReadOnlyCollection<UsageRecord> Records => this._byApplication.Values;

  /// <summary>How many programs are on file.</summary>
  public int Count => this._byApplication.Count;

  /// <summary>The total for one program, or null where there is none.</summary>
  public UsageRecord? Find(string application)
    => this._byApplication.TryGetValue(application, out var record) ? record : null;

  /// <summary>Starts again from nothing, which is what "reset" means (PRD §44).</summary>
  public void Clear() {
    this._byApplication.Clear();
    this._seen.Clear();
  }

  /// <summary>Puts back what was read from the file.</summary>
  public void Restore(IEnumerable<UsageRecord> records) {
    ArgumentNullException.ThrowIfNull(records);
    foreach (var record in records)
      if (record.Application is { Length: > 0 })
        this._byApplication[record.Application] = record;
  }

  /// <summary>
  /// Adds what this sample's interval cost.
  /// </summary>
  /// <param name="snapshot">The sample just taken.</param>
  /// <param name="elapsedSeconds">
  /// How long the interval was. Nought or less means there is no interval to attribute anything to —
  /// the first sample of a session — and nothing is added rather than a whole process's lifetime
  /// being credited to one tick.
  /// </param>
  /// <param name="nowUtcTicks">
  /// The wall clock, passed in rather than read here so a test can hold it still and so this class
  /// stays a pure accumulator.
  /// </param>
  /// <remarks>
  /// A process seen for the first time is a launch, and its counters are recorded rather than added:
  /// what it did before this program started watching is not this machine's record of it — it is
  /// whatever the process had already accumulated, and adding it would credit an hour of work to the
  /// second somebody opened the window.
  /// </remarks>
  public void Add(SystemSnapshot snapshot, double elapsedSeconds, long nowUtcTicks) {
    ArgumentNullException.ThrowIfNull(snapshot);
    if (elapsedSeconds <= 0)
      return;

    var live = new HashSet<ProcessKey>();
    foreach (var process in snapshot.Processes) {
      live.Add(process.Key);
      if (process.ImagePath is not { Length: > 0 } application)
        continue;

      var now = new Consumed(
        process.CpuTimeNs.HasValue ? process.CpuTimeNs.Value : 0,
        process.ReadBytes.HasValue ? process.ReadBytes.Value : 0,
        process.WriteBytes.HasValue ? process.WriteBytes.Value : 0
      );

      var known = this._byApplication.TryGetValue(application, out var record)
        ? record
        : new UsageRecord(application, 0, 0, 0, 0, 0, 0, 0, nowUtcTicks, nowUtcTicks);

      if (!this._seen.TryGetValue(process.Key, out var before)) {
        // First sight of this process. Its counters are the baseline, not a contribution — what it
        // did before anybody was watching belongs to whoever was watching then.
        this._seen[process.Key] = now;
        this._byApplication[application] = known with {
          Launches = known.Launches + 1,
          LastSeenUtcTicks = nowUtcTicks,
        };
        continue;
      }

      this._seen[process.Key] = now;

      // Never negative. A counter that went backwards is a kernel that wrapped or a reading that
      // failed, and treating it as a huge positive would put a century of processor time on a row.
      var working = process.WorkingSetBytes.HasValue ? process.WorkingSetBytes.Value : 0;
      this._byApplication[application] = known with {
        CpuTimeNs = known.CpuTimeNs + Grew(before.CpuTimeNs, now.CpuTimeNs),
        ReadBytes = known.ReadBytes + Grew(before.ReadBytes, now.ReadBytes),
        WrittenBytes = known.WrittenBytes + Grew(before.WrittenBytes, now.WrittenBytes),
        PeakWorkingSetBytes = Math.Max(known.PeakWorkingSetBytes, working),
        MemoryByteSeconds = known.MemoryByteSeconds + working * elapsedSeconds,
        RuntimeSeconds = known.RuntimeSeconds + elapsedSeconds,
        LastSeenUtcTicks = nowUtcTicks,
      };
    }

    // A process that has gone stops being remembered, or the map grows for the life of the session
    // on a machine that starts and stops a lot of short-lived programs — which is most of them.
    if (this._seen.Count <= live.Count)
      return;

    var gone = new List<ProcessKey>();
    foreach (var key in this._seen.Keys)
      if (!live.Contains(key))
        gone.Add(key);

    foreach (var key in gone)
      this._seen.Remove(key);
  }

  private static ulong Grew(ulong before, ulong now) => now > before ? now - before : 0;

  /// <summary>
  /// The whole file, as text.
  /// </summary>
  /// <remarks>
  /// One line per program, tab-separated, with the path last because it is the only field that can
  /// contain a space. Deliberately the same shape as everything else this program writes: a person
  /// can read it, and can delete a line out of it without a tool.
  /// </remarks>
  public string Write() {
    var text = new System.Text.StringBuilder();
    text.AppendLine("# What each program has cost this machine. Delete a line to forget it,");
    text.AppendLine("# or delete the file. Written only while history.usage is on (PRD §44).");
    text.AppendLine("# cpu.ns\tread\twritten\tpeak.ws\tws.byte.seconds\truntime.s\tlaunches\tfirst\tlast\tpath");

    var ordered = new List<UsageRecord>(this._byApplication.Values);
    ordered.Sort(static (left, right) => string.CompareOrdinal(left.Application, right.Application));
    foreach (var record in ordered)
      text.Append(record.CpuTimeNs.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(record.ReadBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(record.WrittenBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(record.PeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(record.MemoryByteSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
        .Append(record.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
        .Append(record.Launches.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(record.FirstSeenUtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(record.LastSeenUtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .AppendLine(record.Application);

    return text.ToString();
  }

  /// <summary>
  /// Reads it back.
  /// </summary>
  /// <remarks>
  /// A line that cannot be understood is skipped rather than failing the file, the same rule the
  /// settings file follows: a history that refuses to load because one line was corrupted has lost
  /// everything to save nothing.
  /// </remarks>
  public static IReadOnlyList<UsageRecord> Parse(string contents) {
    var records = new List<UsageRecord>();
    if (contents is not { Length: > 0 })
      return records;

    foreach (var line in contents.Split('\n')) {
      var trimmed = line.TrimEnd('\r');
      if (trimmed.Length == 0 || trimmed[0] == '#')
        continue;

      // Nine numbers then the path, which is everything after the ninth tab — so a path containing
      // a tab is the one thing this cannot round-trip, and a path containing a tab is not a thing
      // any packager makes.
      var fields = trimmed.Split('\t', 10);
      if (fields.Length < 10)
        continue;

      if (!ulong.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpu)
        || !ulong.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var read)
        || !ulong.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var written)
        || !ulong.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var peak)
        || !double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var byteSeconds)
        || !double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var runtime)
        || !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var launches)
        || !long.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
        || !long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last))
        continue;

      if (fields[9] is not { Length: > 0 } path)
        continue;

      records.Add(new(path, cpu, read, written, peak, byteSeconds, runtime, launches, first, last));
    }

    return records;
  }

  /// <summary>
  /// Forgets everything last seen before the given moment (PRD §44).
  /// </summary>
  /// <remarks>
  /// By last sighting rather than by first: a program run every day since January is not old, and
  /// dropping it because its record began a long time ago would delete exactly the rows worth
  /// keeping.
  /// </remarks>
  public int Forget(long beforeUtcTicks) {
    var stale = new List<string>();
    foreach (var (application, record) in this._byApplication)
      if (record.LastSeenUtcTicks < beforeUtcTicks)
        stale.Add(application);

    foreach (var application in stale)
      this._byApplication.Remove(application);

    return stale.Count;
  }

}
