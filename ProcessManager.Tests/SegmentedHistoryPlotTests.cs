using System.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>The segmented utilisation plate on processor system-information graphs.</summary>
[TestFixture]
public sealed class SegmentedHistoryPlotTests {

  [TestCase("Processor", true)]
  [TestCase("Core 0", true)]
  [TestCase("Core 127", true)]
  [TestCase("Node 0", false)]
  [TestCase("Memory", false)]
  [TestCase("GPU — card0", false)]
  public void OnlyProcessorAndLogicalProcessorPlotsUseTheMeter(string caption, bool expected) {
    var plot = new HistoryPlot {
      Bounds = new(0, 0, 240, 100),
      Caption = caption,
      Maximum = 100,
      Unit = PerformanceUnit.Percent,
    };

    Assert.That(plot.UsesSegmentedMeter, Is.EqualTo(expected));
  }

  [Test]
  public void APercentageShapedNonPercentageScaleDoesNotBecomeACpuMeter() {
    var plot = new HistoryPlot {
      Bounds = new(0, 0, 240, 100),
      Caption = "Processor",
      Maximum = 100,
      Unit = PerformanceUnit.Celsius,
    };

    Assert.That(plot.UsesSegmentedMeter, Is.False);
  }

  [Test]
  public void TheMeterStripIsNotMistakenForAHistoryCoordinate() {
    var history = new HistoryRing<Rate>(4);
    history.Add(Rate.Of(50));

    var plot = ProcessorPlot(history);
    plot.PointAt(10);
    Assert.That(plot.HoverText, Is.Empty, "the left plate is a meter, not a moment in history");

    plot.PointAt(plot.Width - 1);
    Assert.That(plot.HoverText, Does.StartWith("now"), "the history still ends at the right edge");
  }

  [Test]
  public void PausingMovesTheMeterAndHistoryToTheSameSample() {
    var history = new HistoryRing<Rate>(4);
    history.Add(Rate.Of(20));
    history.Add(Rate.Of(80));

    var plot = ProcessorPlot(history);
    plot.PointAt(plot.Width - 1);
    var live = plot.HoverText;

    plot.SkipNewest = 1;
    plot.PointAt(plot.Width - 1);
    var paused = plot.HoverText;

    Assert.Multiple(() => {
      Assert.That(live, Is.Not.Empty);
      Assert.That(paused, Is.Not.Empty);
      Assert.That(paused, Is.Not.EqualTo(live), "the plot must read the same older sample the meter paints");
    });
  }

  private static HistoryPlot ProcessorPlot(HistoryRing<Rate> history) {
    var plot = new HistoryPlot {
      Bounds = new(0, 0, 240, 100),
      Caption = "Processor",
      Maximum = 100,
      Unit = PerformanceUnit.Percent,
    };
    plot.AddSeries(history, Color.Green, "Processor");
    return plot;
  }

}
