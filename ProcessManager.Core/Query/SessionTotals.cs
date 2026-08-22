using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What one account's processes are costing the machine (PRD §43).
/// </summary>
/// <remarks>
/// Every figure is a <see cref="Counter"/> or a <see cref="Rate"/> so that "this user is doing no
/// disk I/O" and "nothing has been measured yet" stay apart. On the first sample of a run every rate
/// on the machine is <see cref="UnknownReason.NotSampledYet"/>, and a sum that reported that as
/// nought would tell somebody an account was idle when nobody had looked (PRD §72.3).
/// </remarks>
/// <param name="Network">
/// Always unknown, and deliberately. §18 refuses per-process network bytes at length: the kernel
/// keeps no such counter, every workaround is wrong in a way nothing on screen would betray, and
/// summing wrong numbers per user would make the error bigger rather than smaller.
/// </param>
public readonly record struct UserTotals(
  int Processes,
  Rate CpuPercent,
  Counter PrivateBytes,
  Rate DiskBytesPerSecond,
  Rate GpuPercent,
  Rate Network
) {

  /// <summary>An account nothing was found for.</summary>
  public static UserTotals None { get; } = new(
    0,
    Rate.NotSampledYet,
    Counter.Unknown(UnknownReason.NotSampledYet),
    Rate.NotSampledYet,
    Rate.NotSampledYet,
    Rate.NotSupported
  );

}

/// <summary>
/// Sums each account's processes, once, for whoever is drawing a table of logins (PRD §43).
/// </summary>
/// <remarks>
/// <para>
/// In Core rather than in the report, because §43 is a view in the window and a report on the
/// command line and both must add up the same numbers. It was only in the report, so the window's
/// own list of logins showed a user, a terminal and a time and nothing at all about what that login
/// was costing — which is the half of the page anybody opens it for (PRD §5.1, §58).
/// </para>
/// <para>
/// By name rather than by uid, because that is what a login record carries. A process whose owner
/// could not be resolved is counted under no account at all rather than under an invented one.
/// </para>
/// </remarks>
public static class SessionTotals {

  /// <summary>Every account with a process, and what its processes cost.</summary>
  public static Dictionary<string, UserTotals> Of(SystemSnapshot snapshot, SnapshotDelta delta) {
    var running = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      if (processes[i].UserName is not { Length: > 0 } user)
        continue;

      running.TryGetValue(user, out var total);
      total.Add(in processes[i], delta, i);
      running[user] = total;
    }

    var totals = new Dictionary<string, UserTotals>(running.Count, StringComparer.Ordinal);
    foreach (var (user, accumulated) in running)
      totals[user] = accumulated.Finish();

    return totals;
  }

  /// <summary>
  /// A running sum that remembers whether anything was actually added to it.
  /// </summary>
  /// <remarks>
  /// The distinction the whole type exists for. A sum of nought over a hundred processes that all
  /// answered "not sampled yet" is not nought — it is the same "not sampled yet", and a
  /// <see cref="double"/> could not say so.
  /// </remarks>
  private struct Accumulator {

    private int _processes;
    private double _cpu;
    private bool _anyCpu;
    private ulong _memory;
    private bool _anyMemory;
    private double _disk;
    private bool _anyDisk;
    private double _gpu;
    private bool _anyGpu;

    public void Add(in ProcessRecord process, SnapshotDelta delta, int index) {
      ++this._processes;

      if (delta.CpuPercent(index) is { HasValue: true } cpu) {
        this._cpu += cpu.Value;
        this._anyCpu = true;
      }

      if (process.PrivateBytes.HasValue) {
        this._memory += process.PrivateBytes.Value;
        this._anyMemory = true;
      }

      // Read and written together: the question a table of logins asks is "how much is this account
      // moving", and which direction it is going in is the process table's business.
      if (delta.IoTotalBytesPerSecond(index) is { HasValue: true } disk) {
        this._disk += disk.Value;
        this._anyDisk = true;
      }

      if (delta.GpuPercent(index) is { HasValue: true } gpu) {
        this._gpu += gpu.Value;
        this._anyGpu = true;
      }
    }

    public readonly UserTotals Finish() => new(
      this._processes,
      this._anyCpu ? Rate.Of(this._cpu) : Rate.NotSampledYet,
      this._anyMemory ? Counter.Of(this._memory) : Counter.Unknown(UnknownReason.NotPermitted),
      this._anyDisk ? Rate.Of(this._disk) : Rate.NotSampledYet,
      this._anyGpu ? Rate.Of(this._gpu) : Rate.NotSupported,
      // §18, in the one place a caller would otherwise be tempted to add something up.
      Rate.NotSupported
    );

  }

}
