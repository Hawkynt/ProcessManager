using System.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>The non-linear time axis that keeps recent graph data wide and older data compact.</summary>
[TestFixture]
public sealed class CompressedHistoryTests {

  [Test]
  public void FifteenTimesHistoryReallyMeansFifteenTimesTheRecentSpan() {
    var axis = new HistoryAxis(pixels: 400, nominalSamples: 60, retainedSamples: 960, requestedMultiplier: 15);

    Assert.Multiple(() => {
      Assert.That(axis.Multiplier, Is.EqualTo(15));
      Assert.That(axis.OldestSampleAge, Is.EqualTo(900));
      Assert.That(axis.VisibleSamples, Is.EqualTo(900));
    });
  }

  [Test]
  public void TheNewestEdgeKeepsTheOrdinaryResolution() {
    var linear = new HistoryAxis(400, 60, 960, 1);
    var compressed = new HistoryAxis(400, 60, 960, 15);

    // The derivative at now is the same. Looking one tiny step into the past must therefore cost
    // essentially the same amount of sample age on both axes.
    Assert.That(
      compressed.AgeAtDistance(0.01),
      Is.EqualTo(linear.AgeAtDistance(0.01)).Within(0.0001)
    );

    Assert.That(compressed.AgeAtDistance(300), Is.GreaterThan(linear.AgeAtDistance(300)));
  }

  [Test]
  public void CompressionNeverPromisesHistoryTheRingCannotRetain() {
    var axis = new HistoryAxis(pixels: 400, nominalSamples: 300, retainedSamples: 960, requestedMultiplier: 15);

    Assert.Multiple(() => {
      Assert.That(axis.Multiplier, Is.EqualTo(3.2).Within(0.000001));
      Assert.That(axis.OldestSampleAge, Is.EqualTo(960).Within(0.000001));
    });
  }

  [Test]
  public void AShortRingDoesNotShrinkTheRequestedRecentSpan() {
    var axis = new HistoryAxis(pixels: 400, nominalSamples: 900, retainedSamples: 120, requestedMultiplier: 15);

    Assert.Multiple(() => {
      Assert.That(axis.Multiplier, Is.EqualTo(1));
      Assert.That(axis.OldestSampleAge, Is.EqualTo(900));
    });
  }

  [Test]
  public void AgeAndPixelMappingsAreInversesAcrossTheCompressedAxis() {
    var axis = new HistoryAxis(711, 60, 960, 15);

    foreach (var age in new[] { 0d, 1, 15, 60, 120, 450, 899, 900 }) {
      var distance = axis.DistanceAtAge(age);
      Assert.That(axis.AgeAtDistance(distance), Is.EqualTo(age).Within(0.000001), $"age {age}");
    }
  }

  [Test]
  public void PixelBucketsGrowOlderAndWiderSmoothly() {
    var axis = new HistoryAxis(400, 60, 960, 15);
    var newest = axis.AgesForPixel(0);
    var middle = axis.AgesForPixel(200);
    var oldest = axis.AgesForPixel(399);

    Assert.Multiple(() => {
      Assert.That(newest.Youngest, Is.Zero);
      Assert.That(middle.Youngest, Is.GreaterThan(newest.Oldest));
      Assert.That(oldest.Youngest, Is.GreaterThan(middle.Oldest));
      Assert.That(oldest.Oldest - oldest.Youngest, Is.GreaterThan(middle.Oldest - middle.Youngest));
      Assert.That(middle.Oldest - middle.Youngest, Is.GreaterThan(newest.Oldest - newest.Youngest));
    });
  }

  [Test]
  public void AnOldSpikeSurvivesWhenSeveralSamplesShareOnePixel() {
    var axis = new HistoryAxis(400, 60, 960, 15);
    var bucket = axis.AgesForPixel(399);
    var spikeAge = (int)Math.Floor((bucket.Youngest + bucket.Oldest) / 2);
    var history = new HistoryRing<Rate>(960);

    for (var i = 0; i < 960; ++i) {
      var age = 959 - i;
      history.Add(Rate.Of(age == spikeAge ? 100 : 1));
    }

    var reading = SeriesPainter.ReadingForPixel(history, history.Count, axis, 399);
    Assert.Multiple(() => {
      Assert.That(bucket.Oldest - bucket.Youngest, Is.GreaterThan(1));
      Assert.That(reading.HasValue, Is.True);
      Assert.That(reading.Value, Is.EqualTo(100), "compression must retain a spike, not average it away");
    });
  }

  [Test]
  public void ThePlotReportsTheRealCompressedHorizon() {
    var history = new HistoryRing<Rate>(960);
    history.Add(Rate.Of(10));
    var plot = new HistoryPlot {
      Bounds = new(0, 0, 500, 160),
      SpanSeconds = 60,
      SecondsPerSample = 1,
      HistoryMultiplier = 15,
    };
    plot.AddSeries(history, Color.Green, "CPU");

    Assert.Multiple(() => {
      Assert.That(plot.EffectiveHistoryMultiplier, Is.EqualTo(15));
      Assert.That(plot.VisibleSpanSeconds, Is.EqualTo(900));
    });
  }

  [Test]
  public void StatisticsCoverWhatTheCompressedPlotActuallyShows() {
    var history = new HistoryRing<Rate>(960);
    for (var i = 0; i < 400; ++i)
      history.Add(Rate.Of(i == 99 ? 100 : 1));

    // The spike is about five minutes old. A linear sixty-second statistic would miss it; a 15x
    // compressed sixty-second graph includes it, so its inspection result has to include it too.
    var plot = new HistoryPlot {
      Bounds = new(0, 0, 500, 160),
      SpanSeconds = 60,
      SecondsPerSample = 1,
      HistoryMultiplier = 15,
    };
    plot.AddSeries(history, Color.Green, "CPU");

    Assert.That(plot.Statistics(), Does.Contain("maximum 100"));
  }

  [Test]
  public void SettingTheMultiplierToOneRestoresTheLinearHorizon() {
    var history = new HistoryRing<Rate>(960);
    history.Add(Rate.Of(10));
    var plot = new HistoryPlot {
      Bounds = new(0, 0, 500, 160),
      SpanSeconds = 60,
      SecondsPerSample = 1,
      HistoryMultiplier = 1,
    };
    plot.AddSeries(history, Color.Green, "CPU");

    Assert.Multiple(() => {
      Assert.That(plot.EffectiveHistoryMultiplier, Is.EqualTo(1));
      Assert.That(plot.VisibleSpanSeconds, Is.EqualTo(60));
    });
  }

  [Test]
  public void PointedSampleAgeUsesTheSameCompressedAxisAsTheReading() {
    var history = new HistoryRing<Rate>(960);
    for (var i = 0; i < 960; ++i)
      history.Add(Rate.Of(i));

    var plot = new HistoryPlot {
      Bounds = new(0, 0, 500, 160),
      SpanSeconds = 60,
      SecondsPerSample = 1,
      HistoryMultiplier = 15,
    };
    plot.AddSeries(history, Color.Green, "CPU");

    plot.PointAt(499);
    Assert.That(plot.PointedSampleAge, Is.EqualTo(0));

    plot.PointAt(250);
    Assert.That(plot.PointedSampleAge, Is.GreaterThan(60), "the middle of a compressed 15x axis is older than the recent span");
  }

  [Test]
  public void PointedSampleAgeIncludesPauseAndRejectsTheSegmentedMeter() {
    var history = new HistoryRing<Rate>(960);
    for (var i = 0; i < 20; ++i)
      history.Add(Rate.Of(i));

    var plot = new HistoryPlot {
      Bounds = new(0, 0, 500, 160),
      Caption = "Processor",
      Unit = Hawkynt.ProcessManager.Query.PerformanceUnit.Percent,
      Maximum = 100,
      SkipNewest = 7,
    };
    plot.AddSeries(history, Color.Green, "CPU");

    plot.PointAt(499);
    Assert.That(plot.PointedSampleAge, Is.EqualTo(7), "paused now is seven samples behind the ring's real now");

    plot.PointAt(10);
    Assert.That(plot.PointedSampleAge, Is.Null, "the segmented meter has no time coordinate");
  }

}
