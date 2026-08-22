using System.Buffers.Binary;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>
/// Everything the privileged helper is allowed to be asked. This list <em>is</em> the privileged
/// surface, and adding to it is a PRD change (PRD §8.2).
/// </summary>
public enum ElevatedOpcode : byte {
  None = 0,
  ReadProcIo = 1,
  ReadCmdline = 2,
  ReadEnviron = 3,
  ListFds = 4,
  Terminate = 5,
  Suspend = 6,
  Resume = 7,
  SetPriority = 8,
  SetAffinity = 9,

  /// <summary>
  /// The firmware's SMBIOS structure table, which is root-only on every distribution that ships it
  /// (PRD §47).
  /// </summary>
  /// <remarks>
  /// The only opcode that names no process: it is a fact about the machine, the same bytes whoever
  /// asks, and the path is a constant inside the helper rather than anything a caller supplies.
  /// </remarks>
  ReadSmbios = 10,
}

/// <summary>What the helper says back.</summary>
public enum ElevatedStatus : byte {
  Ok = 0,
  NotPermitted = 1,
  ProcessExited = 2,
  IdentityMismatch = 3,
  UnknownOpcode = 4,
  Malformed = 5,
  Failed = 6,
}

/// <summary>
/// The wire format between the unprivileged program and the helper running as root.
/// </summary>
/// <remarks>
/// <para>
/// Length-prefixed binary frames with a fixed opcode set. The helper <em>parses</em>; it never
/// evaluates, never accepts a command string, and never accepts a path it did not construct itself
/// from a pid it validated. Anything malformed is refused and the connection is dropped rather than
/// resynchronised — a parser that tries to recover from a bad frame is a parser an attacker gets to
/// experiment with (PRD §8.2).
/// </para>
/// <para>
/// Every request carries the target's identity as a <see cref="ProcessKey"/>, not a bare pid, and
/// the helper re-validates the pair against the live system before acting. A pid recycled between
/// the user's click and the syscall is a different program.
/// </para>
/// </remarks>
public static class ElevatedProtocol {

  /// <summary>
  /// The largest frame the helper will read. Anything claiming more is refused without allocating —
  /// a length prefix is attacker-controlled and "allocate what it says" is how that becomes a
  /// denial of service.
  /// </summary>
  public const int MaxFrameLength = 64 * 1024;

  /// <summary>Header: 4-byte length (of everything after it), then 1 opcode byte.</summary>
  public const int LengthPrefixBytes = 4;

  /// <summary>A parsed request. <paramref name="Argument"/> means priority, affinity mask, or nothing.</summary>
  public readonly record struct Request(ElevatedOpcode Opcode, ProcessKey Key, long Argument);

  public static void WriteRequest(Stream stream, in Request request) {
    ArgumentNullException.ThrowIfNull(stream);

    Span<byte> frame = stackalloc byte[LengthPrefixBytes + 1 + 8 + 8 + 8];
    var body = frame[LengthPrefixBytes..];
    body[0] = (byte)request.Opcode;
    BinaryPrimitives.WriteInt64LittleEndian(body[1..], request.Key.Pid);
    BinaryPrimitives.WriteUInt64LittleEndian(body[9..], request.Key.StartTicks);
    BinaryPrimitives.WriteInt64LittleEndian(body[17..], request.Argument);
    BinaryPrimitives.WriteInt32LittleEndian(frame, 25);
    stream.Write(frame);
    stream.Flush();
  }

  /// <summary>
  /// Reads one request. Returns false at end of stream; throws nothing on malformed input, but
  /// reports it — the caller's contract is to stop, not to skip.
  /// </summary>
  public static bool TryReadRequest(Stream stream, out Request request, out ElevatedStatus problem) {
    ArgumentNullException.ThrowIfNull(stream);

    request = default;
    problem = ElevatedStatus.Ok;

    Span<byte> header = stackalloc byte[LengthPrefixBytes];
    if (!ReadExactly(stream, header))
      return false;

    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length is < 25 or > MaxFrameLength) {
      problem = ElevatedStatus.Malformed;
      return true;
    }

    Span<byte> body = stackalloc byte[25];
    if (!ReadExactly(stream, body)) {
      // The stream ended in the middle of a frame it promised. Not a request, and not something to
      // wait for more of.
      problem = ElevatedStatus.Malformed;
      return true;
    }

    // Anything beyond the fields this version knows is skipped rather than trusted, so a newer
    // client talking to an older helper degrades instead of desynchronising the stream.
    //
    // The buffer is allocated once, outside the loop. Inside it, a frame claiming the maximum length
    // would stack-allocate 256 bytes on each of 256 iterations — sixty-four kilobytes of stack, in a
    // parser running as root, at the request of whoever sent the frame. The analyzer caught it
    // (CA2014) and it is exactly the class of thing this component must not contain.
    Span<byte> discard = stackalloc byte[256];
    for (var remaining = length - 25; remaining > 0;) {
      var take = Math.Min(discard.Length, remaining);
      if (!ReadExactly(stream, discard[..take])) {
        problem = ElevatedStatus.Malformed;
        return true;
      }

      remaining -= take;
    }

    var opcode = body[0];
    if (!IsKnown(opcode)) {
      problem = ElevatedStatus.UnknownOpcode;
      return true;
    }

    var pid = BinaryPrimitives.ReadInt64LittleEndian(body[1..]);
    // Only where the opcode is about a process. The firmware table is about the machine and carries
    // no pid, so requiring one would reject the request the helper is written to answer — which is
    // what happened: the range check above stopped at the second-to-last opcode and this one demanded
    // a pid for a request that has none, and between them the whole path was dead while the helper
    // had a case for it.
    if (NamesAProcess((ElevatedOpcode)opcode) && pid is <= 0 or > int.MaxValue) {
      problem = ElevatedStatus.Malformed;
      return true;
    }

    request = new(
      (ElevatedOpcode)opcode,
      new((int)pid, BinaryPrimitives.ReadUInt64LittleEndian(body[9..])),
      BinaryPrimitives.ReadInt64LittleEndian(body[17..])
    );

    return true;
  }

  /// <summary>
  /// Whether this is an opcode the helper knows.
  /// </summary>
  /// <remarks>
  /// Every one named rather than a range ending at whichever member happens to be last. The range
  /// was <c>&gt; SetAffinity</c> and stopped being right the moment an opcode was added after it,
  /// which is exactly what happened — the helper grew a case for the firmware table and the parser
  /// went on rejecting it, so the feature was dead and nothing failed. A list cannot go stale
  /// silently: the test beside it walks the enum and requires every member to be here.
  /// </remarks>
  private static bool IsKnown(byte opcode) => (ElevatedOpcode)opcode switch {
    ElevatedOpcode.ReadProcIo => true,
    ElevatedOpcode.ReadCmdline => true,
    ElevatedOpcode.ReadEnviron => true,
    ElevatedOpcode.ListFds => true,
    ElevatedOpcode.Terminate => true,
    ElevatedOpcode.Suspend => true,
    ElevatedOpcode.Resume => true,
    ElevatedOpcode.SetPriority => true,
    ElevatedOpcode.SetAffinity => true,
    ElevatedOpcode.ReadSmbios => true,
    // Including None, which is what a zeroed byte parses as and must never be a request.
    _ => false,
  };

  /// <summary>
  /// Whether this opcode is about a process, and so has to carry an identity worth checking.
  /// </summary>
  /// <remarks>
  /// The split is by opcode rather than by whether a key happened to be sent, so a caller cannot
  /// talk its way past the recycled-pid check by leaving one out (PRD §8.2). Exactly one opcode is
  /// about the machine instead.
  /// </remarks>
  public static bool NamesAProcess(ElevatedOpcode opcode) => opcode != ElevatedOpcode.ReadSmbios;

  public static void WriteResponse(Stream stream, ElevatedStatus status, ReadOnlySpan<byte> payload = default) {
    ArgumentNullException.ThrowIfNull(stream);

    Span<byte> header = stackalloc byte[LengthPrefixBytes + 1];
    BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length + 1);
    header[LengthPrefixBytes] = (byte)status;
    stream.Write(header);
    if (!payload.IsEmpty)
      stream.Write(payload);

    stream.Flush();
  }

  public static bool TryReadResponse(Stream stream, out ElevatedStatus status, out byte[] payload) {
    ArgumentNullException.ThrowIfNull(stream);

    status = ElevatedStatus.Failed;
    payload = [];

    Span<byte> header = stackalloc byte[LengthPrefixBytes + 1];
    if (!ReadExactly(stream, header))
      return false;

    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length is < 1 or > MaxFrameLength)
      return false;

    status = (ElevatedStatus)header[LengthPrefixBytes];
    var payloadLength = length - 1;
    if (payloadLength == 0)
      return true;

    payload = new byte[payloadLength];
    return ReadExactly(stream, payload);
  }

  public static string DecodePayload(byte[] payload) => Encoding.UTF8.GetString(payload);

  private static bool ReadExactly(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var got = stream.Read(buffer[read..]);
      if (got <= 0)
        return false;

      read += got;
    }

    return true;
  }

}
