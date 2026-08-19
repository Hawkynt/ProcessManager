namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Turns a device name's bytes into a string, without allocating one every sample.
/// </summary>
/// <remarks>
/// A machine has a handful of disks and interfaces and they keep their names for as long as it is
/// running, so decoding them on each sample allocated a string per device per second for nothing —
/// which the allocation budget caught (PRD §71). A linear scan is the right shape here: the list is
/// never more than a few dozen entries, and it costs no allocation at all once warm.
/// </remarks>
internal sealed class DeviceNameCache {

  private readonly List<string> _known = [];

  public string Resolve(ReadOnlySpan<byte> utf8) {
    foreach (var name in this._known)
      if (Matches(name, utf8))
        return name;

    var created = System.Text.Encoding.UTF8.GetString(utf8);
    this._known.Add(created);
    return created;
  }

  /// <summary>
  /// Compares byte for byte against a string.
  /// </summary>
  /// <remarks>
  /// Sound only because these names are ASCII — the kernel builds them from a fixed alphabet — so
  /// one byte is one character and the lengths can be compared directly. A name with a multi-byte
  /// character would fail the length check and simply be decoded again, which is a miss rather than
  /// a wrong answer.
  /// </remarks>
  private static bool Matches(string name, ReadOnlySpan<byte> utf8) {
    if (name.Length != utf8.Length)
      return false;

    for (var i = 0; i < utf8.Length; ++i)
      if (name[i] != (char)utf8[i])
        return false;

    return true;
  }

}
