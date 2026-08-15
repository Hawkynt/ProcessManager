using System.Buffers.Binary;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The frame parser that runs as root (PRD §9.8).
/// </summary>
/// <remarks>
/// Every test here is an attempt to make the parser do something it should not: claim a length it
/// does not deliver, name an opcode that does not exist, end mid-frame, or ask for an allocation the
/// size of a machine. The parser's contract is that all of them are refused and none of them throws,
/// because the process this runs in has root's authority and a crash there is a denial of service on
/// everything the user wanted to do.
/// </remarks>
[TestFixture]
public sealed class ElevatedProtocolTests {

  [Test]
  public void AWellFormedRequestRoundTrips() {
    var stream = new MemoryStream();
    var sent = new ElevatedProtocol.Request(ElevatedOpcode.Terminate, new(1234, 987_654), 0);
    ElevatedProtocol.WriteRequest(stream, in sent);
    stream.Position = 0;

    Assert.That(ElevatedProtocol.TryReadRequest(stream, out var got, out var problem), Is.True);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.Ok));
    Assert.That(got, Is.EqualTo(sent));
  }

  [Test]
  public void AnArgumentSurvivesTheRoundTrip() {
    var stream = new MemoryStream();
    ElevatedProtocol.WriteRequest(stream, new(ElevatedOpcode.SetAffinity, new(9, 9), 0b1011));
    stream.Position = 0;

    ElevatedProtocol.TryReadRequest(stream, out var got, out _);
    Assert.That(got.Argument, Is.EqualTo(0b1011));
  }

  [Test]
  public void AnEmptyStreamIsTheEndOfTheConversationRatherThanAnError() {
    Assert.That(ElevatedProtocol.TryReadRequest(new MemoryStream(), out _, out var problem), Is.False);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.Ok));
  }

  [Test]
  public void ALengthLargerThanTheCeilingIsRefusedWithoutAllocating() {
    // The length prefix is attacker-controlled. "Allocate what it says" would turn a four-byte write
    // into a two-gigabyte allocation in a process running as root.
    var stream = new MemoryStream();
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
    stream.Write(header);
    stream.Position = 0;

    Assert.That(ElevatedProtocol.TryReadRequest(stream, out _, out var problem), Is.True);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.Malformed));
  }

  [Test]
  public void ANegativeLengthIsRefused() {
    var stream = new MemoryStream();
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, -1);
    stream.Write(header);
    stream.Position = 0;

    Assert.That(ElevatedProtocol.TryReadRequest(stream, out _, out var problem), Is.True);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.Malformed));
  }

  [Test]
  public void ALengthTooSmallToHoldARequestIsRefused() {
    var stream = new MemoryStream();
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, 3);
    stream.Write(header);
    stream.Position = 0;

    Assert.That(ElevatedProtocol.TryReadRequest(stream, out _, out var problem), Is.True);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.Malformed));
  }

  [Test]
  public void AStreamThatEndsMidFrameIsRefusedRatherThanWaitedOn() {
    var stream = new MemoryStream();
    ElevatedProtocol.WriteRequest(stream, new(ElevatedOpcode.Terminate, new(1, 1), 0));
    var truncated = new MemoryStream(stream.ToArray()[..10]);

    Assert.That(ElevatedProtocol.TryReadRequest(truncated, out _, out var problem), Is.True);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.Malformed));
  }

  [Test]
  public void AnUnknownOpcodeIsRefused() {
    var stream = WriteRaw(opcode: 200, pid: 1234, startTicks: 1, argument: 0);
    Assert.That(ElevatedProtocol.TryReadRequest(stream, out _, out var problem), Is.True);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.UnknownOpcode));
  }

  [Test]
  public void OpcodeZeroIsRefused() {
    var stream = WriteRaw(opcode: 0, pid: 1234, startTicks: 1, argument: 0);
    Assert.That(ElevatedProtocol.TryReadRequest(stream, out _, out var problem), Is.True);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.UnknownOpcode));
  }

  [TestCase(0L)]
  [TestCase(-1L)]
  [TestCase((long)int.MaxValue + 1)]
  public void APidOutsideTheLegalRangeIsRefused(long pid) {
    var stream = WriteRaw((byte)ElevatedOpcode.Terminate, pid, 1, 0);
    Assert.That(ElevatedProtocol.TryReadRequest(stream, out _, out var problem), Is.True);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.Malformed));
  }

  [Test]
  public void ALongerFrameFromANewerClientIsSkippedRatherThanDesynchronising() {
    // Forwards compatibility, and a resynchronisation hazard if it were got wrong: unknown trailing
    // bytes are consumed so the next frame still starts where the length said it would.
    var stream = new MemoryStream();
    var body = new byte[25 + 40];
    body[0] = (byte)ElevatedOpcode.Suspend;
    BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(1), 4242);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(9), 77);
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
    stream.Write(header);
    stream.Write(body);
    ElevatedProtocol.WriteRequest(stream, new(ElevatedOpcode.Resume, new(7, 7), 0));
    stream.Position = 0;

    Assert.That(ElevatedProtocol.TryReadRequest(stream, out var first, out var firstProblem), Is.True);
    Assert.That(firstProblem, Is.EqualTo(ElevatedStatus.Ok));
    Assert.That(first.Opcode, Is.EqualTo(ElevatedOpcode.Suspend));
    Assert.That(first.Key.Pid, Is.EqualTo(4242));

    Assert.That(ElevatedProtocol.TryReadRequest(stream, out var second, out var secondProblem), Is.True);
    Assert.That(secondProblem, Is.EqualTo(ElevatedStatus.Ok));
    Assert.That(second.Opcode, Is.EqualTo(ElevatedOpcode.Resume), "the stream did not desynchronise");
  }

  [Test]
  public void NoMalformedInputThrows() {
    // Fuzz-ish: every truncation of a valid frame, plus a run of random-looking bytes. A throw here
    // takes down a process running as root.
    var valid = new MemoryStream();
    ElevatedProtocol.WriteRequest(valid, new(ElevatedOpcode.SetPriority, new(1234, 5678), -5));
    var bytes = valid.ToArray();

    for (var length = 0; length <= bytes.Length; ++length) {
      var prefix = new MemoryStream(bytes[..length]);
      Assert.That(() => ElevatedProtocol.TryReadRequest(prefix, out _, out _), Throws.Nothing, $"truncated to {length}");
    }

    var garbage = new byte[512];
    for (var i = 0; i < garbage.Length; ++i)
      garbage[i] = (byte)(i * 37 % 251);

    Assert.That(() => ElevatedProtocol.TryReadRequest(new MemoryStream(garbage), out _, out _), Throws.Nothing);
  }

  [Test]
  public void AResponseRoundTripsWithItsPayload() {
    var stream = new MemoryStream();
    ElevatedProtocol.WriteResponse(stream, ElevatedStatus.Ok, "read_bytes: 4096\n"u8);
    stream.Position = 0;

    Assert.That(ElevatedProtocol.TryReadResponse(stream, out var status, out var payload), Is.True);
    Assert.That(status, Is.EqualTo(ElevatedStatus.Ok));
    Assert.That(ElevatedProtocol.DecodePayload(payload), Is.EqualTo("read_bytes: 4096\n"));
  }

  [Test]
  public void AResponseWithNoPayloadRoundTrips() {
    var stream = new MemoryStream();
    ElevatedProtocol.WriteResponse(stream, ElevatedStatus.IdentityMismatch);
    stream.Position = 0;

    Assert.That(ElevatedProtocol.TryReadResponse(stream, out var status, out var payload), Is.True);
    Assert.That(status, Is.EqualTo(ElevatedStatus.IdentityMismatch));
    Assert.That(payload, Is.Empty);
  }

  [Test]
  public void AResponseClaimingAnAbsurdLengthIsRefused() {
    var stream = new MemoryStream();
    var header = new byte[5];
    BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
    stream.Write(header);
    stream.Position = 0;

    Assert.That(ElevatedProtocol.TryReadResponse(stream, out _, out _), Is.False);
  }

  private static MemoryStream WriteRaw(byte opcode, long pid, ulong startTicks, long argument) {
    var stream = new MemoryStream();
    var body = new byte[25];
    body[0] = opcode;
    BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(1), pid);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(9), startTicks);
    BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(17), argument);

    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
    stream.Write(header);
    stream.Write(body);
    stream.Position = 0;
    return stream;
  }

}

/// <summary>The client side's refusal paths, which must not need a helper to exercise.</summary>
[TestFixture]
public sealed class ElevatedChannelTests {

  [Test]
  public void AMissingHelperIsReportedOnceAndNotRetried() {
    using var channel = new ElevatedChannel("/nonexistent/procman-helper");

    Assert.That(channel.Start(), Is.False);
    Assert.That(channel.Available, Is.False);
    Assert.That(channel.Unavailable, Does.Contain("not installed"));

    // The second call must not try again: a prompt that reappears every second is worse than the
    // missing feature (PRD §8.3).
    Assert.That(channel.Start(), Is.False);
  }

  [Test]
  public void SendingWithoutAHelperIsNotPermittedRatherThanAThrow() {
    using var channel = new ElevatedChannel("/nonexistent/procman-helper");
    var (status, payload) = channel.Send(ElevatedOpcode.ReadProcIo, new(1, 1));

    Assert.That(status, Is.EqualTo(ElevatedStatus.NotPermitted));
    Assert.That(payload, Is.Empty);
  }

}
