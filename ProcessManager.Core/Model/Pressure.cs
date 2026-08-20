namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// One half of a pressure reading: how much of the last ten, sixty and three hundred seconds was
/// spent stalled, and the total stall time since boot (PRD §46).
/// </summary>
/// <param name="TotalMicroseconds">
/// Cumulative stall time. The averages are what a person reads; this is what a rate is computed
/// from when a longer window than five minutes is wanted.
/// </param>
public readonly record struct PressureShare(Rate Average10, Rate Average60, Rate Average300, Counter TotalMicroseconds) {

  /// <summary>
  /// Nothing was reported.
  /// </summary>
  /// <remarks>
  /// Explicit rather than <c>default</c>, because <c>default(Rate)</c> is a confident zero and a
  /// kernel built without <c>CONFIG_PSI</c> would otherwise report a machine under no pressure at
  /// all — which is exactly what a machine being crushed also looks like (PRD §5.3).
  /// </remarks>
  public static readonly PressureShare Unknown = new(
    Rate.Unknown(UnknownReason.NotSupportedOnPlatform),
    Rate.Unknown(UnknownReason.NotSupportedOnPlatform),
    Rate.Unknown(UnknownReason.NotSupportedOnPlatform),
    Counter.NotSupported
  );

  public bool HasValue => this.Average10.HasValue || this.Average60.HasValue || this.Average300.HasValue;

}

/// <summary>
/// How much a resource is stalling the machine (PRD §46).
/// </summary>
/// <param name="Some">Any task was waiting for the resource.</param>
/// <param name="Full">
/// Every task was waiting at once, so nothing ran at all. The serious one: a machine showing more
/// than a few percent of full memory pressure is thrashing rather than busy.
/// </param>
public readonly record struct PressureReading(PressureShare Some, PressureShare Full) {

  public static readonly PressureReading Unknown = new(PressureShare.Unknown, PressureShare.Unknown);

  public bool HasValue => this.Some.HasValue || this.Full.HasValue;

}
