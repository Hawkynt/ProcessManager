namespace Hawkynt.ProcessManager.Sampling;

/// <summary>
/// A fixed-capacity series. Allocated once at construction; adding past capacity overwrites the
/// oldest entry, so memory is bounded by the ring rather than by remembering to prune (PRD §3.3).
/// </summary>
/// <typeparam name="T">The sample type; <see cref="Model.Rate"/> in practice, so that a gap in the
/// series is expressible as a value rather than needing a parallel array of flags.</typeparam>
public sealed class HistoryRing<T> where T : struct {

  private readonly T[] _items;
  private int _head;

  public HistoryRing(int capacity) {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    this._items = new T[capacity];
  }

  public int Capacity => this._items.Length;

  /// <summary>How many samples are in the ring, up to <see cref="Capacity"/>.</summary>
  public int Count { get; private set; }

  /// <summary>Oldest first, so index <see cref="Count"/> - 1 is the newest.</summary>
  public T this[int index] {
    get {
      ArgumentOutOfRangeException.ThrowIfNegative(index);
      ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, this.Count);

      var start = this.Count == this._items.Length ? this._head : 0;
      return this._items[(start + index) % this._items.Length];
    }
  }

  public void Add(T value) {
    this._items[this._head] = value;
    this._head = (this._head + 1) % this._items.Length;
    if (this.Count < this._items.Length)
      ++this.Count;
  }

  /// <summary>The newest sample, or <see langword="false"/> when the ring is empty.</summary>
  public bool TryPeekLast(out T value) {
    if (this.Count == 0) {
      value = default;
      return false;
    }

    value = this[this.Count - 1];
    return true;
  }

  public void Clear() {
    this.Count = 0;
    this._head = 0;
  }

  /// <summary>
  /// Copies the newest <paramref name="destination"/>.Length samples into <paramref name="destination"/>,
  /// oldest first, and returns how many were written. For plot code, which wants exactly as many
  /// points as it has pixels and would otherwise allocate a list per frame.
  /// </summary>
  public int CopyNewestTo(Span<T> destination) {
    var take = Math.Min(destination.Length, this.Count);
    var first = this.Count - take;
    for (var i = 0; i < take; ++i)
      destination[i] = this[first + i];

    return take;
  }

}
