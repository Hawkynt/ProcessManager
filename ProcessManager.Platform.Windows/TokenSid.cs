using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Reading a security identifier's own bytes.
/// </summary>
/// <remarks>
/// Deliberately not marked as Windows-only, and deliberately taking a span rather than a pointer:
/// it is pure arithmetic over a documented layout, so it is tested on every CI leg against
/// hand-built structures rather than only on a machine that has tokens (PRD §9.4). The same reason
/// the bulk-query parser is a separate type.
/// </remarks>
internal static class TokenSid {

  /// <summary>
  /// The integrity level out of a mandatory-label SID's own bytes.
  /// </summary>
  /// <remarks>
  /// A SID is one revision byte, one sub-authority count, six identifier-authority bytes, then that
  /// many little-endian 32-bit sub-authorities. The level is the <em>last</em> of them, so the count
  /// is what says where to look; assuming a fixed position reads the wrong number on any label that
  /// ever gains one. Written against a span so it can be tested on a machine with no tokens at all,
  /// which is the same reason the bulk-query parser takes one (PRD §9.4).
  /// </remarks>
  public static Counter IntegrityFromSid(ReadOnlySpan<byte> sid) {
    if (sid.Length < 8)
      return Counter.Unknown(UnknownReason.CounterInvalid);

    var subAuthorityCount = sid[1];
    if (subAuthorityCount == 0)
      return Counter.Unknown(UnknownReason.CounterInvalid);

    var offset = 8 + ((subAuthorityCount - 1) * 4);
    if (offset + 4 > sid.Length)
      return Counter.Unknown(UnknownReason.CounterInvalid);

    return Counter.Of(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sid[offset..]));
  }

}
