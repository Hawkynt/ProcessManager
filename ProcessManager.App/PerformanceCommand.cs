using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--perf</c>: one resource, watched for a few seconds and plotted (PRD §45, §59, §96).
/// </summary>
/// <remarks>
/// <para>
/// <c>--host</c> is this report with every resource in it and no graph: it takes two samples and
/// prints what they say. That answers "what is this machine", and it cannot answer "is the processor
/// busy <em>now</em>, or was it busy a moment ago" — which is the question a graph exists for and the
/// only thing the window's performance page has that a terminal one-shot did not.
/// </para>
/// <para>
/// The sections come from <see cref="PerformanceReport"/>, the same builder the window's page and
/// <c>--host</c> draw from, so none of the three can disagree about the machine (PRD §58).
/// </para>
/// </remarks>
internal static class PerformanceCommand {

  /// <summary>
  /// How many points the plot has, and so how wide it is drawn.
  /// </summary>
  /// <remarks>
  /// One cell per sample. Fewer would throw readings away; more would need a terminal wider than
  /// eighty columns to show what was collected.
  /// </remarks>
  private const int _Samples = 40;

  /// <summary>
  /// How long between samples when nothing said otherwise.
  /// </summary>
  /// <remarks>
  /// Not <see cref="CommandLineOptions.Interval"/>'s default of a second, which would make this verb
  /// take forty seconds to print anything. A tenth of a second over forty samples is four seconds of
  /// watching, which is long enough for a shape and short enough to type in front of somebody. An
  /// interval given on the command line is honoured, because then the length is what was asked for.
  /// </remarks>
  private static readonly TimeSpan _DefaultSpacing = TimeSpan.FromMilliseconds(100);

  public static int Run(Sampler sampler, ISystemProbe probe, CommandLineOptions options) {
    ArgumentNullException.ThrowIfNull(sampler);
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(options);

    var host = probe.DescribeHost();
    var topology = probe.DescribeTopology();

    // Twice, and only to find out what this machine has. Twice rather than once because half of what
    // it has is only visible through a delta: the per-core sections exist for as many cores as the
    // difference between two samples describes, so a catalogue built from one sample is a machine
    // with no cores in it and --perf cpu would print the total and nothing under it.
    var spacing = options.IntervalWasStated ? options.Interval : _DefaultSpacing;
    sampler.Sample();
    Thread.Sleep(spacing);
    sampler.Sample();
    var catalogue = PerformanceReport.Build(
      host,
      sampler.Current,
      sampler.Delta,
      probe.DescribeDisk,
      probe.DescribeInterface,
      probe.DescribeGpus,
      topology: topology
    );

    var wanted = options.PerformanceResource ?? "cpu";
    var chosen = Select(catalogue, wanted);
    if (chosen.Count == 0) {
      Console.Error.WriteLine($"procman: there is no resource called '{wanted}' on this machine.");
      Console.Error.WriteLine($"It has: {string.Join(", ", Names(catalogue))}");
      return 1;
    }

    // Only the readers the chosen resource actually needs. Asking a driver about every graphics
    // adapter forty times over, to plot a processor, is the sort of thing §5.4 exists to stop.
    var disks = Any(chosen, "Disk") ? probe.DescribeDisk : (Func<string, DiskInfo>?)null;
    var interfaces = Any(chosen, "Net ") ? probe.DescribeInterface : (Func<string, NetworkInterfaceInfo>?)null;
    var adapters = Any(chosen, "GPU") ? probe.DescribeGpus : (Func<IReadOnlyList<GpuInfo>>?)null;

    var series = new Dictionary<(string Section, string Label), HistoryRing<Rate>>();
    var latest = catalogue;
    for (var i = 0; i < _Samples; ++i) {
      Thread.Sleep(spacing);
      sampler.Sample();
      latest = PerformanceReport.Build(
        host,
        sampler.Current,
        sampler.Delta,
        disks,
        interfaces,
        adapters,
        topology: topology
      );

      foreach (var section in latest) {
        if (!chosen.Contains(section.Title))
          continue;

        foreach (var plot in Plots(in section)) {
          var key = (section.Title, plot.Label);
          if (!series.TryGetValue(key, out var ring))
            series[key] = ring = new(_Samples);

          ring.Add(plot.Value);
        }
      }
    }

    Console.WriteLine(
      $"{_Samples.ToString(CultureInfo.InvariantCulture)} samples, "
      + $"{spacing.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms apart — oldest at the left."
    );

    var style = options.AsciiOnly ? GraphStyle.Ascii : options.GraphStyle;
    var first = true;
    foreach (var section in latest) {
      if (!chosen.Contains(section.Title))
        continue;

      if (!first)
        Console.WriteLine();

      first = false;
      Console.WriteLine(section.Title);

      var plots = Plots(in section);
      var label = 0;
      foreach (var plot in plots)
        label = Math.Max(label, plot.Label.Length);

      foreach (var plot in plots) {
        var ring = series.GetValueOrDefault((section.Title, plot.Label));
        Console.WriteLine(
          $"  {plot.Label.PadRight(label)}  {Plot(style, ring, Scale(plot.Maximum, ring))}  "
          + $"{Value(plot.Value, plot.Unit)}"
        );
      }

      if (section.Rows.Count == 0)
        continue;

      Console.WriteLine();
      foreach (var row in section.Rows)
        Console.WriteLine($"  {row.Label,-24} {row.Value}");
    }

    return 0;
  }

  /// <summary>One plotted line: a series, its ceiling, and what it reads now.</summary>
  private readonly record struct Plotted(string Label, Rate Value, double Maximum, PerformanceUnit Unit);

  /// <summary>
  /// Every line a section plots.
  /// </summary>
  /// <remarks>
  /// <see cref="PerformanceSection.Series"/> is the graphs a resource stacks; the kernel share and a
  /// disk's second direction are not in it — they ride on the section and on the graph respectively —
  /// so both are picked up here. Each is read only when its label says there is one, because
  /// <c>default(Rate)</c> is a confident zero and a line drawn along the floor is a graph of something
  /// nobody measured (PRD §5.3, §72.3).
  /// </remarks>
  private static List<Plotted> Plots(in PerformanceSection section) {
    var plots = new List<Plotted>();
    foreach (var graph in section.Series) {
      plots.Add(new(graph.FirstLabel, graph.Value, graph.Maximum, graph.Unit));
      if (graph.HasCompanion)
        plots.Add(new(graph.CompanionLabel, graph.Companion, graph.Maximum, graph.Unit));
    }

    if (section.HasSecondary)
      plots.Add(new(
        section.SecondaryLabel,
        section.Secondary,
        section.PrimaryMaximum,
        // The same rule the section itself uses to label its own primary: a fixed hundred is a
        // percentage, and anything without a ceiling is a throughput.
        section.PrimaryMaximum == 100 ? PerformanceUnit.Percent : PerformanceUnit.BytesPerSecond
      ));

    return plots;
  }

  /// <summary>
  /// Which sections the argument names.
  /// </summary>
  /// <remarks>
  /// The five words people reach for first, and then anything whose name contains what was typed —
  /// so <c>--perf sda</c> and <c>--perf wlan0</c> find one device without a word having to be invented
  /// for it. A processor brings its cores with it: the parts of a resource are sections of their own
  /// and a terminal has no checkbox to reveal them with, so it prints them (PRD §46).
  /// </remarks>
  private static HashSet<string> Select(IReadOnlyList<PerformanceSection> sections, string wanted) {
    var chosen = new HashSet<string>(StringComparer.Ordinal);
    var name = wanted.ToLowerInvariant();
    var prefix = name switch {
      "cpu" or "processor" or "cores" => "Processor",
      "memory" or "mem" or "ram" => "Memory",
      "disk" or "disks" or "io" or "storage" => "Disk",
      "net" or "network" or "nic" => "Net ",
      "gpu" or "graphics" => "GPU",
      _ => null,
    };

    foreach (var section in sections) {
      if (!section.HasPrimary)
        continue;

      var matched = prefix is null
        ? section.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)
          || section.RailName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
        : section.Title.StartsWith(prefix, StringComparison.Ordinal)
          || section.RailName.StartsWith(prefix, StringComparison.Ordinal);

      if (matched)
        chosen.Add(section.Title);
    }

    // The parts of anything chosen, which is what makes --perf cpu show the cores.
    foreach (var section in sections)
      if (section.PartOf.Length > 0 && chosen.Contains(section.PartOf))
        chosen.Add(section.Title);

    return chosen;
  }

  private static bool Any(HashSet<string> chosen, string prefix) {
    foreach (var title in chosen)
      if (title.StartsWith(prefix, StringComparison.Ordinal))
        return true;

    return false;
  }

  /// <summary>What this machine has to watch, for the message a mistyped name gets.</summary>
  private static List<string> Names(IReadOnlyList<PerformanceSection> sections) {
    var names = new List<string>();
    foreach (var section in sections)
      if (section.HasPrimary && section.PartOf.Length == 0)
        names.Add(section.RailName);

    return names;
  }

  /// <summary>
  /// The value that fills a cell.
  /// </summary>
  /// <remarks>
  /// A percentage has a fixed hundred so two machines' plots mean the same thing. A throughput has no
  /// natural ceiling, so it is scaled to the tallest sample there is — and to one where every sample
  /// is unknown or nought, because a scale of zero draws nothing at all.
  /// </remarks>
  private static double Scale(double maximum, HistoryRing<Rate>? history) {
    if (maximum > 0)
      return maximum;

    var top = 0d;
    for (var i = 0; history is not null && i < history.Count; ++i)
      if (history[i].TryGetValue(out var value) && value > top)
        top = value;

    return top > 0 ? top : 1;
  }

  private static string Plot(GraphStyle style, HistoryRing<Rate>? history, double scale) => style switch {
    GraphStyle.Braille => BrailleSparkline.Render(_Samples / 2, history, scale),
    GraphStyle.Ascii => BlockSparkline.Render(_Samples, history, scale, unicode: false),
    _ => BlockSparkline.Render(_Samples, history, scale, unicode: true),
  };

  private static string Value(Rate value, PerformanceUnit unit) => unit switch {
    // The unit rides on the number, as it does in the rows beneath: a bare "39.9" beside a plot is a
    // figure whose scale the reader has to guess at (PRD §76).
    PerformanceUnit.Percent => value.HasValue ? Humanize.Percent(value) + " %" : Humanize.Placeholder(value.Reason),
    PerformanceUnit.BytesPerSecond => Humanize.BytesPerSecond(value),
    PerformanceUnit.Bytes => value.TryGetValue(out var bytes) && bytes >= 0
      ? Humanize.Bytes((ulong)bytes)
      : Humanize.Placeholder(value.Reason),
    PerformanceUnit.Celsius => value.TryGetValue(out var celsius)
      ? $"{celsius.ToString("0.0", CultureInfo.InvariantCulture)} °C"
      : Humanize.Placeholder(value.Reason),
    PerformanceUnit.Watts => value.TryGetValue(out var watts)
      ? $"{watts.ToString("0.0", CultureInfo.InvariantCulture)} W"
      : Humanize.Placeholder(value.Reason),
    _ => Humanize.Rate(value),
  };

}
