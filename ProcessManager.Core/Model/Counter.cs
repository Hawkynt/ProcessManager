using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// An absolute reading — bytes, ticks, a count — or the reason there is none (PRD §3.4).
/// </summary>
public readonly struct Counter : IEquatable<Counter> {

  private readonly ulong _value;

  private Counter(ulong value, UnknownReason reason) {
    this._value = value;
    this.Reason = reason;
  }

  /// <summary>Why <see cref="HasValue"/> is false, or <see cref="UnknownReason.None"/>.</summary>
  public UnknownReason Reason { get; }

  public bool HasValue => this.Reason == UnknownReason.None;

  public ulong Value => this.HasValue
    ? this._value
    : throw new InvalidOperationException($"Counter has no value: {this.Reason}.");

  /// <summary>The reading, or <paramref name="fallback"/> when there is none. For rendering only.</summary>
  public ulong GetValueOrDefault(ulong fallback = 0) => this.HasValue ? this._value : fallback;

  public bool TryGetValue(out ulong value) {
    value = this._value;
    return this.HasValue;
  }

  public static Counter Of(ulong value) => new(value, UnknownReason.None);

  public static Counter Of(long value) => value >= 0
    ? new((ulong)value, UnknownReason.None)
    : Unknown(UnknownReason.CounterInvalid);

  public static Counter Unknown(UnknownReason reason) => reason == UnknownReason.None
    ? throw new ArgumentOutOfRangeException(nameof(reason), "UnknownReason.None is not a reason.")
    : new(0, reason);

  public static readonly Counter NotSampledYet = Unknown(UnknownReason.NotSampledYet);
  public static readonly Counter NotSupported = Unknown(UnknownReason.NotSupportedOnPlatform);
  public static readonly Counter NotPermitted = Unknown(UnknownReason.NotPermitted);

  /// <summary>
  /// The increase from <paramref name="previous"/> to this reading. A counter that went backwards
  /// did not go backwards — it was reset, or it wrapped, or the PID was reused — so the answer is
  /// <see cref="UnknownReason.CounterInvalid"/> rather than a number nobody can defend (PRD §3.2).
  /// </summary>
  public Counter Since(Counter previous) {
    if (!this.HasValue)
      return this;
    if (!previous.HasValue)
      return previous.Reason == UnknownReason.None ? NotSampledYet : Unknown(previous.Reason);

    return this._value >= previous._value
      ? Of(this._value - previous._value)
      : Unknown(UnknownReason.CounterInvalid);
  }

  public bool Equals(Counter other) => this.Reason == other.Reason && this._value == other._value;
  public override bool Equals([NotNullWhen(true)] object? obj) => obj is Counter other && this.Equals(other);
  public override int GetHashCode() => HashCode.Combine(this._value, this.Reason);
  public static bool operator ==(Counter left, Counter right) => left.Equals(right);
  public static bool operator !=(Counter left, Counter right) => !left.Equals(right);

  public override string ToString() => this.HasValue
    ? this._value.ToString(CultureInfo.InvariantCulture)
    : this.Reason.ToString();

}
