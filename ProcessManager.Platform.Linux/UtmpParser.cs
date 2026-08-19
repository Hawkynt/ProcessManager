using System.Buffers.Binary;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// <c>/run/utmp</c>, which is who is logged in (PRD §43).
/// </summary>
/// <remarks>
/// <para>
/// A flat array of fixed-size C structures rather than text, so this is offset arithmetic against a
/// documented layout — and therefore a span-taking function in a type with no platform attribute,
/// tested on every CI leg against a recorded file (PRD §9.4).
/// </para>
/// <para>
/// The record is 384 bytes on 64-bit Linux and its fields are native-endian. Every platform this
/// runs on is little-endian; a big-endian one would need the byte order flipped rather than the
/// offsets changed.
/// </para>
/// </remarks>
internal static class UtmpParser {

  /// <summary>sizeof(struct utmp) on 64-bit Linux.</summary>
  public const int RecordSize = 384;

  private const int _TypeOffset = 0;
  private const int _PidOffset = 4;
  private const int _LineOffset = 8;
  private const int _LineLength = 32;
  private const int _UserOffset = 44;
  private const int _UserLength = 32;
  private const int _HostOffset = 76;
  private const int _HostLength = 256;
  private const int _TimeOffset = 340;

  // ut_type, from <utmp.h>. The ones not named here are clock changes and init bookkeeping.
  private const short _BootTime = 2;
  private const short _LoginProcess = 6;
  private const short _UserProcess = 7;
  private const short _DeadProcess = 8;

  private static readonly DateTime _Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>
  /// Reads every record. A caller wanting only the people logged in filters on
  /// <see cref="SessionKind.User"/>; the rest are kept because the boot record is how the machine's
  /// start time is known and a dead one explains a slot that looks occupied.
  /// </summary>
  public static int Parse(ReadOnlySpan<byte> content, Span<SessionRecord> destination) {
    var written = 0;
    var offset = 0;
    while (offset + RecordSize <= content.Length && written < destination.Length) {
      var record = content.Slice(offset, RecordSize);
      offset += RecordSize;

      var kind = BinaryPrimitives.ReadInt16LittleEndian(record[_TypeOffset..]) switch {
        _BootTime => SessionKind.Boot,
        _LoginProcess => SessionKind.LoginProcess,
        _UserProcess => SessionKind.User,
        _DeadProcess => SessionKind.Dead,
        _ => SessionKind.Unknown,
      };

      // Type 0 is an empty slot: the file is preallocated and a run of them is normal.
      if (kind == SessionKind.Unknown)
        continue;

      var seconds = BinaryPrimitives.ReadInt32LittleEndian(record[_TimeOffset..]);
      destination[written++] = new(
        NulTerminated(record.Slice(_UserOffset, _UserLength)),
        NulTerminated(record.Slice(_LineOffset, _LineLength)),
        // Empty means a local login rather than an unknown one, and the two deserve different
        // answers: null here, and the UI shows "local" rather than a blank cell.
        NulTerminated(record.Slice(_HostOffset, _HostLength)) is { Length: > 0 } host ? host : null,
        BinaryPrimitives.ReadInt32LittleEndian(record[_PidOffset..]),
        seconds > 0 ? _Epoch.AddSeconds(seconds).Ticks : 0,
        kind
      );
    }

    return written;
  }

  /// <summary>
  /// A fixed-width C string: characters up to the first NUL, or the whole field when it is full.
  /// </summary>
  /// <remarks>
  /// Reading the whole field would drag the padding into the value, and a user name with 31 bytes of
  /// NUL after it would compare equal to nothing at all.
  /// </remarks>
  private static string NulTerminated(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    if (end < 0)
      end = field.Length;

    return end == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(field[..end]);
  }

}
