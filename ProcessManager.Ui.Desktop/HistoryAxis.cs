namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Maps pixels onto sample age for a graph whose older history is progressively compressed.
/// </summary>
/// <remarks>
/// <para>
/// The newest end keeps the ordinary linear scale: moving one pixel away from now costs exactly as
/// much time as it did without compression. The cost then rises smoothly toward the old end, so a
/// spike from ten minutes ago can share a plot with the last minute without crushing the last minute
/// into a thumbnail.
/// </para>
/// <para>
/// The requested multiplier is a horizon, not a promise to invent storage. It is capped by the
/// backing ring's capacity; a sixty-second plot backed by fifteen minutes can genuinely show 15×,
/// while a fifteen-minute plot on the same ring stays almost linear instead of drawing fourteen
/// empty spans to its left.
/// </para>
/// <para>
/// A quadratic is deliberate. It is continuous in both age and local scale, has unit slope at now,
/// is cheap enough to evaluate once per pixel, and has a closed-form inverse for truthful grid and
/// cursor placement. No resampling table and no allocation is needed while painting.
/// </para>
/// </remarks>
internal readonly record struct HistoryAxis {

  /// <summary>The TMOG-style long-history request used by the desktop graphs.</summary>
  public const double DefaultMultiplier = 15;

  public HistoryAxis(int pixels, int nominalSamples, int retainedSamples, double requestedMultiplier = DefaultMultiplier) {
    this.Pixels = Math.Max(1, pixels);
    this.NominalSamples = Math.Max(1, nominalSamples);

    var availableMultiplier = Math.Max(1d, retainedSamples / (double)this.NominalSamples);
    var requested = double.IsNaN(requestedMultiplier) ? 1d : requestedMultiplier;
    this.Multiplier = Math.Clamp(requested, 1d, availableMultiplier);
  }

  /// <summary>Width of the drawable time axis.</summary>
  public int Pixels { get; }

  /// <summary>How many samples the same span would contain on a linear axis.</summary>
  public int NominalSamples { get; }

  /// <summary>The multiplier the retained data can actually support.</summary>
  public double Multiplier { get; }

  /// <summary>How many samples old the far edge is.</summary>
  public double OldestSampleAge => this.NominalSamples * this.Multiplier;

  /// <summary>How many retained samples a statistic over the visible graph must consider.</summary>
  public int VisibleSamples => Math.Max(this.NominalSamples, (int)Math.Ceiling(this.OldestSampleAge));

  /// <summary>Whether the horizontal axis is non-linear.</summary>
  public bool IsCompressed => this.Multiplier > 1.000001;

  /// <summary>
  /// Sample age at a distance from the newest edge. Nought is now; <see cref="Pixels"/> is the old
  /// boundary just outside the leftmost painted pixel.
  /// </summary>
  public double AgeAtDistance(double pixelsFromNewest) {
    var t = Math.Clamp(pixelsFromNewest / this.Pixels, 0d, 1d);
    return this.NominalSamples * (t + ((this.Multiplier - 1d) * t * t));
  }

  /// <summary>
  /// Distance from now at which an age belongs. The inverse of <see cref="AgeAtDistance"/>.
  /// </summary>
  public double DistanceAtAge(double sampleAge) {
    var y = Math.Clamp(sampleAge / this.NominalSamples, 0d, this.Multiplier);
    var extra = this.Multiplier - 1d;
    if (extra <= 0.000001)
      return y * this.Pixels;

    var t = (Math.Sqrt(1d + (4d * extra * y)) - 1d) / (2d * extra);
    return Math.Clamp(t, 0d, 1d) * this.Pixels;
  }

  /// <summary>The youngest and oldest sample ages covered by one painted pixel column.</summary>
  public (double Youngest, double Oldest) AgesForPixel(int pixelsFromNewest) {
    var distance = Math.Clamp(pixelsFromNewest, 0, this.Pixels - 1);
    return (this.AgeAtDistance(distance), this.AgeAtDistance(distance + 1d));
  }

}
