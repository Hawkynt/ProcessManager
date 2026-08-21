using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The graphics band, and the two dialogs that admit to the colours (PRD §23).
/// </summary>
/// <remarks>
/// A colour no dialog explains is decoration (§7.1). The row colours had a legend from the start;
/// the two washes §23 paints over a busy cell did not, which made them exactly the thing that rule
/// forbids — and made the thresholds behind them settable only by finding the file.
/// </remarks>
[TestFixture]
public sealed class HighlightWindowTests {

  #region the graphics band

  private static readonly UsageThresholds _Thresholds = UsageThresholds.Default;

  /// <summary>
  /// A share of the whole adapter, so its numbers are not the CPU's. A band set at a hundred could
  /// only ever fire on a benchmark, because a hundred here means every engine saturated.
  /// </summary>
  [Test]
  public void TheGraphicsBandIsJudgedAgainstTheWholeAdapter() {
    Assert.That(_Thresholds.Gpu(Rate.Of(10)), Is.EqualTo(UsageHeat.None));
    Assert.That(_Thresholds.Gpu(Rate.Of(33)), Is.EqualTo(UsageHeat.Warm));
    Assert.That(_Thresholds.Gpu(Rate.Of(74)), Is.EqualTo(UsageHeat.Warm));
    Assert.That(_Thresholds.Gpu(Rate.Of(75)), Is.EqualTo(UsageHeat.Hot));
  }

  [Test]
  public void AGraphicsReadingThatDoesNotExistIsNeverMarked() {
    foreach (var reason in new[] { UnknownReason.NotPermitted, UnknownReason.NotSampledYet, UnknownReason.NotSupportedOnPlatform })
      Assert.That(_Thresholds.Gpu(Rate.Unknown(reason)), Is.EqualTo(UsageHeat.None), reason.ToString());
  }

  /// <summary>
  /// A machine whose adapter is saturated by one process, so the delta has a reading to judge.
  /// </summary>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Busy(ulong encodePercent) {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1, 1000);
    records[0].Name = "render";
    // The driver's own sampled figure, which needs no previous sample — see SnapshotDelta.FillGpu.
    records[0].GpuEncodePercent = Counter.Of(encodePercent);
    records[0].GpuGraphicsNs = Counter.NotSupported;
    records[0].GpuComputeNs = Counter.NotSupported;
    records[0].GpuCopyNs = Counter.NotSupported;
    records[0].GpuEncodeNs = Counter.NotSupported;
    records[0].GpuDecodeNs = Counter.NotSupported;
    records[0].GpuDecodePercent = Counter.NotSupported;
    records[0].GpuBusyPercent = Counter.NotSupported;

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    return (snapshot, delta);
  }

  [Test]
  public void TheEngineColumnsAreMarkedFromTheirOwnReading() {
    var (snapshot, delta) = Busy(90);

    Assert.That(_Thresholds.Of(ProcessField.GpuEncodePercent, in snapshot.Processes[0], delta, 0), Is.EqualTo(UsageHeat.Hot));
    Assert.That(
      _Thresholds.Of(ProcessField.GpuPercent, in snapshot.Processes[0], delta, 0),
      Is.EqualTo(UsageHeat.Hot),
      "the summary column is the busiest engine, and the busiest engine is this one"
    );
  }

  /// <summary>
  /// A mark that points at the wrong number is worse than no mark. Graphics memory is bytes, and a
  /// percentage threshold has nothing to say about it.
  /// </summary>
  [Test]
  public void GraphicsMemoryIsNotJudgedByAPercentageThreshold() {
    var (snapshot, delta) = Busy(90);

    foreach (var id in new[] { ProcessField.GpuDedicatedMemory, ProcessField.GpuSharedMemory, ProcessField.GpuTotalMemory })
      Assert.That(_Thresholds.Of(id, in snapshot.Processes[0], delta, 0), Is.EqualTo(UsageHeat.None), id.ToString());
  }

  [Test]
  public void TheGraphicsBandSurvivesTheRoundTrip() {
    var settings = new UserSettings {
      Thresholds = UsageThresholds.Default with { WarmGpuPercent = 12, HotGpuPercent = 88 },
    };

    var reread = UserSettings.Parse(settings.Write());

    Assert.That(reread.Thresholds.WarmGpuPercent, Is.EqualTo(12));
    Assert.That(reread.Thresholds.HotGpuPercent, Is.EqualTo(88));
  }

  /// <summary>
  /// The six that were positional keep working from the outside, which is why the pair was added by
  /// name. A caller that wrote the whole tuple out gets the defaults for the two it never knew of.
  /// </summary>
  [Test]
  public void ATupleWrittenBeforeTheBandExistedStillGetsIt() {
    var older = new UsageThresholds(50, 100, 5, 10, 1024, 2048);

    Assert.That(older.WarmGpuPercent, Is.EqualTo(UsageThresholds.Default.WarmGpuPercent));
    Assert.That(older.HotGpuPercent, Is.EqualTo(UsageThresholds.Default.HotGpuPercent));
  }

  #endregion

  #region the dialogs that explain them

  [Test]
  public void TheLegendSpellsOutBothBandsInTheNumbersItIsJudgingBy() {
    var legend = new LegendWindow(UsageThresholds.Default with { WarmCpuPercent = 42, HotGpuPercent = 88 });

    Assert.That(legend.Description, Does.Contain("Warm"));
    Assert.That(legend.Description, Does.Contain("Hot"));
    Assert.That(legend.Description, Does.Contain("42"), "the band as it stands, not the one it shipped with");
    Assert.That(legend.Description, Does.Contain("88"));
  }

  /// <summary>A band switched off says so rather than printing a nought that reads like a threshold.</summary>
  [Test]
  public void ABandSwitchedOffIsNotABandSetToNought() {
    var legend = new LegendWindow(new UsageThresholds(0, 0, 0, 0, 0, 0) { WarmGpuPercent = 0, HotGpuPercent = 0 });

    Assert.That(legend.Description, Does.Contain("switched off"));
    Assert.That(legend.Description, Does.Not.Contain(" 0 "));
  }

  /// <summary>
  /// A colour no dialog explains is decoration (PRD §7.1), and that is the defect this file's subject
  /// shipped with once already. Walked over the enum rather than written out, so that adding a
  /// category and forgetting the legend fails a build instead of painting an unexplained row.
  /// </summary>
  [Test]
  public void EveryColourTheTableCanPaintIsInTheLegend() {
    var theme = DefaultTheme.Instance;
    foreach (var category in Enum.GetValues<ProcessCategory>()) {
      if (RowPalette.BackColorOf(category, theme) is null)
        continue;

      Assert.That(LegendWindow.Categories, Does.Contain(category), $"{category} is painted and unexplained");
    }
  }

  /// <summary>
  /// The other half of the same requirement: the note names what is deliberately not distinguished,
  /// and it must not still be naming something that now is. Packaged and managed-runtime rows were on
  /// that list until the readings existed to prove them (PRD §23).
  /// </summary>
  [Test]
  public void TheNoteDoesNotStillRefuseWhatTheTableNowPaints() {
    Assert.That(LegendWindow.Note, Does.Contain("unsigned"), "the refusal that still stands");
    Assert.That(LegendWindow.Note, Does.Not.Contain("Not distinguished: packaged"));
  }

  [Test]
  public void TheDialogHandsBackWhatItWasGiven() {
    var thresholds = UsageThresholds.Default with { WarmCpuPercent = 25, HotMemoryPercent = 40 };
    var dialog = new HighlightThresholdsDialog(thresholds);

    Assert.That(dialog.Accepted, Is.False, "until somebody says OK");
    Assert.That(dialog.Thresholds.WarmCpuPercent, Is.EqualTo(25));
    Assert.That(dialog.Thresholds.HotMemoryPercent, Is.EqualTo(40));
  }

  /// <summary>
  /// The rates are entered in megabytes a second and stored in bytes. A spinner counting to a
  /// hundred million is not a control anybody can use, and a unit that changes on the way through is
  /// exactly the kind of quiet factor of 1 048 576 this program exists not to introduce.
  /// </summary>
  [Test]
  public void ByteRatesGoInAsMegabytesAndComeBackOutAsBytes() {
    var dialog = new HighlightThresholdsDialog(UsageThresholds.Default);

    Assert.That(dialog.Thresholds.WarmBytesPerSecond, Is.EqualTo(UsageThresholds.Default.WarmBytesPerSecond));
    Assert.That(dialog.Thresholds.HotBytesPerSecond, Is.EqualTo(UsageThresholds.Default.HotBytesPerSecond));
    Assert.That(dialog.Description, Does.Contain("MB/s"));
  }

  #endregion

}
