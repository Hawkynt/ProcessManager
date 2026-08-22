using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One icon in the panel per resource somebody asked for (PRD §65).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing appears unless it was asked for.</b> A program that puts icons in somebody's panel
/// without being asked has taken a decision about their screen that is theirs to take, so the
/// setting is a list of names and its default is empty. Each is named rather than counted, because
/// turning one off must not mean turning the tray off.
/// </para>
/// <para>
/// Each icon is the recent history of its resource rather than a number: at sixteen pixels square a
/// number is unreadable and a shape is not, which is why every tray monitor ever written draws the
/// same little graph. The arithmetic is in Core so it can be tested without a panel — a tray icon
/// that is wrong is wrong in a way nobody reports, because it is a smudge in the corner that
/// somebody quietly stops trusting.
/// </para>
/// </remarks>
internal sealed class TrayIndicators : IDisposable {

  /// <summary>How big an icon is drawn, which is what a panel asks for on both platforms.</summary>
  private const int _Size = 22;

  /// <summary>How many samples of history the icon holds — one per column.</summary>
  private const int _Columns = _Size;

  private readonly List<(IndicatorKind Kind, NotifyIcon Icon, HistoryRing<Rate> History)> _indicators = [];

  /// <summary>Somebody clicked an indicator, and wants the page for that resource.</summary>
  public event EventHandler<IndicatorKind>? Chosen;

  /// <summary>Somebody double-clicked, and wants the whole program.</summary>
  public event EventHandler? Opened;

  /// <summary>How many icons are in the panel, which is the detector a picture cannot give.</summary>
  public int Count => this._indicators.Count;

  /// <summary>
  /// Puts one icon in the panel for each resource named, in the order they were named.
  /// </summary>
  /// <remarks>
  /// The order is the setting's order: somebody who wrote "memory,cpu" meant memory first, and a
  /// panel is a place where the order is the whole of the arrangement.
  /// </remarks>
  public TrayIndicators(IReadOnlyList<IndicatorKind> kinds) {
    ArgumentNullException.ThrowIfNull(kinds);
    foreach (var kind in kinds) {
      var icon = new NotifyIcon { Text = IndicatorIcon.Describe(kind) };
      var chosen = kind;
      icon.Click += (_, _) => this.Chosen?.Invoke(this, chosen);
      icon.DoubleClick += (_, _) => this.Opened?.Invoke(this, EventArgs.Empty);
      this._indicators.Add((kind, icon, new(_Columns)));
      icon.Visible = true;
    }
  }

  /// <summary>
  /// Takes this sample's figure for each resource and redraws its icon.
  /// </summary>
  /// <remarks>
  /// A figure that could not be read is pushed as an unknown rather than skipped, so the icon's
  /// column for that moment stays clear. Skipping it would slide the history along and quietly claim
  /// the machine was idle for a second nobody measured (PRD §72.3).
  /// </remarks>
  public void Update(SnapshotDelta? delta, SystemSnapshot snapshot) {
    ArgumentNullException.ThrowIfNull(snapshot);
    foreach (var (kind, icon, history) in this._indicators) {
      history.Add(Reading(kind, delta, snapshot));
      icon.Text = $"{IndicatorIcon.Describe(kind)} — {Humanize.Percent(history[history.Count - 1])}";
      icon.SetIcon(_Size, _Size, IndicatorIcon.Render(_Size, _Size, history, 100, IndicatorIcon.Ink(kind)));
    }
  }

  /// <summary>
  /// This resource's share of itself, as a percentage, or unknown where nobody could read one.
  /// </summary>
  /// <remarks>
  /// Every one of them is scaled to its own whole rather than to a number that would need a legend:
  /// the processor against the machine, memory against what is installed, and the disk and the
  /// adapter against their own busiest moment so far, which is the only scale either of them has —
  /// a disk has no maximum rate it will admit to.
  /// </remarks>
  private static Rate Reading(IndicatorKind kind, SnapshotDelta? delta, SystemSnapshot snapshot) {
    if (delta is null)
      return Rate.NotSampledYet;

    return kind switch {
      IndicatorKind.Cpu => delta.SystemCpuPercent,
      IndicatorKind.Memory => MemoryPercent(snapshot),
      _ => Rate.Unknown(UnknownReason.NotImplementedHere),
    };
  }

  /// <summary>
  /// How much of the machine's memory is in use, from the two figures the snapshot already holds.
  /// </summary>
  /// <remarks>
  /// Total less available, not total less free: free counts only pages holding nothing, and a
  /// healthy machine keeps almost none of those because the cache has the rest. Reading it as
  /// pressure would show every Linux machine at ninety-odd per cent for ever.
  /// </remarks>
  private static Rate MemoryPercent(SystemSnapshot snapshot) {
    var total = snapshot.System.TotalMemoryBytes;
    var available = snapshot.System.AvailableMemoryBytes;
    if (!total.HasValue || !available.HasValue || total.Value == 0)
      return Rate.NotSampledYet;

    var used = total.Value > available.Value ? total.Value - available.Value : 0;
    return Rate.Of(used * 100d / total.Value);
  }

  public void Dispose() {
    foreach (var (_, icon, _) in this._indicators)
      icon.Dispose();

    this._indicators.Clear();
  }

}
