using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// A per-second figure or a percentage — always the result of dividing one <see cref="Counter"/>
/// difference by an interval — or the reason there is none (PRD §3.2, §3.4).
/// </summary>
public readonly struct Rate : IEquatable<Rate> {

  private readonly double _value;

  private Rate(double value, UnknownReason reason) {
    this._value = value;
    this.Reason = reason;
  }

  public UnknownReason Reason { get; }

  public bool HasValue => this.Reason == UnknownReason.None;

  public double Value => this.HasValue
    ? this._value
    : throw new InvalidOperationException($"Rate has no value: {this.Reason}.");

  public double GetValueOrDefault(double fallback = 0d) => this.HasValue ? this._value : fallback;

  public bool TryGetValue(out double value) {
    value = this._value;
    return this.HasValue;
  }

  public static Rate Of(double value) => double.IsFinite(value)
    ? new(value, UnknownReason.None)
    : Unknown(UnknownReason.CounterInvalid);

  public static Rate Unknown(UnknownReason reason) => reason == UnknownReason.None
    ? throw new ArgumentOutOfRangeException(nameof(reason), "UnknownReason.None is not a reason.")
    : new(double.NaN, reason);

  public static readonly Rate NotSampledYet = Unknown(UnknownReason.NotSampledYet);
  public static readonly Rate NotSupported = Unknown(UnknownReason.NotSupportedOnPlatform);

  /// <summary>
  /// A hole in the series: the interval was missed, or the machine was asleep. Plots break their
  /// line across one of these rather than drawing through it (PRD §3.3).
  /// </summary>
  public static readonly Rate Gap = Unknown(UnknownReason.CounterInvalid);

  public bool Equals(Rate other)
    => this.Reason == other.Reason && (!this.HasValue || this._value.Equals(other._value));

  public override bool Equals([NotNullWhen(true)] object? obj) => obj is Rate other && this.Equals(other);
  public override int GetHashCode() => HashCode.Combine(this._value, this.Reason);
  public static bool operator ==(Rate left, Rate right) => left.Equals(right);
  public static bool operator !=(Rate left, Rate right) => !left.Equals(right);

  public override string ToString() => this.HasValue
    ? this._value.ToString("0.###", CultureInfo.InvariantCulture)
    : this.Reason.ToString();

}
