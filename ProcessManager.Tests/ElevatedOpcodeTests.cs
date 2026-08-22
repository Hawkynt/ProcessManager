using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// That every opcode the helper answers is one the parser will accept (PRD §68).
/// </summary>
/// <remarks>
/// <para>
/// The parser used to reject anything above <c>SetAffinity</c>, which was right until an opcode was
/// added after it. The helper grew a case for the firmware table, the parser went on refusing it,
/// and the whole path was dead — with nothing failing, because nothing joined the two ends.
/// </para>
/// <para>
/// This is that join. It walks the enum rather than a list somebody maintains, so an opcode added
/// without being taught to the parser fails here instead of quietly never working.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ElevatedOpcodeTests {

  private static bool RoundTrips(ElevatedOpcode opcode, ProcessKey key, out ElevatedStatus problem) {
    var buffer = new MemoryStream();
    ElevatedProtocol.WriteRequest(buffer, new(opcode, key, 0));
    buffer.Position = 0;

    var read = ElevatedProtocol.TryReadRequest(buffer, out var request, out problem);
    return read && problem == ElevatedStatus.Ok && request.Opcode == opcode;
  }

  /// <summary>
  /// Every opcode there is survives being written and read back.
  /// </summary>
  /// <remarks>
  /// The key is the one the opcode calls for: an opcode about a process carries a real identity, and
  /// the one about the machine carries none — which is the second half of what was broken, because
  /// the parser demanded a pid for a request that has none.
  /// </remarks>
  [Test]
  public void EveryOpcodeTheHelperKnowsIsOneTheParserAccepts() {
    Assert.Multiple(() => {
      foreach (var opcode in Enum.GetValues<ElevatedOpcode>()) {
        if (opcode == ElevatedOpcode.None)
          continue;

        var key = ElevatedProtocol.NamesAProcess(opcode) ? new ProcessKey(1234, 5678) : default;
        Assert.That(
          RoundTrips(opcode, key, out var problem),
          Is.True,
          $"{opcode} was refused as {problem}"
        );
      }
    });
  }

  /// <summary>
  /// A byte that is not an opcode is refused as one, rather than being let through as whatever the
  /// enum happens to have at that number.
  /// </summary>
  [TestCase((byte)0)]
  [TestCase((byte)11)]
  [TestCase((byte)99)]
  [TestCase((byte)255)]
  public void AByteThatIsNotAnOpcodeIsRefused(byte value) {
    var buffer = new MemoryStream();
    ElevatedProtocol.WriteRequest(buffer, new((ElevatedOpcode)value, new(1234, 5678), 0));
    buffer.Position = 0;

    ElevatedProtocol.TryReadRequest(buffer, out _, out var problem);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.UnknownOpcode));
  }

  /// <summary>
  /// An opcode that is about a process still has to carry a usable one. The exemption is by opcode
  /// rather than by whether a key happened to be sent, so a caller cannot talk its way past the
  /// recycled-pid check by leaving one out (PRD §8.2).
  /// </summary>
  [Test]
  public void AnOpcodeAboutAProcessStillNeedsOne() {
    var buffer = new MemoryStream();
    ElevatedProtocol.WriteRequest(buffer, new(ElevatedOpcode.Terminate, default, 0));
    buffer.Position = 0;

    ElevatedProtocol.TryReadRequest(buffer, out _, out var problem);
    Assert.That(problem, Is.EqualTo(ElevatedStatus.Malformed));
  }

  /// <summary>
  /// And exactly one opcode is about the machine rather than a process, so the exemption cannot
  /// quietly widen.
  /// </summary>
  [Test]
  public void ExactlyOneOpcodeIsAboutTheMachine() {
    var exempt = new List<ElevatedOpcode>();
    foreach (var opcode in Enum.GetValues<ElevatedOpcode>())
      if (opcode != ElevatedOpcode.None && !ElevatedProtocol.NamesAProcess(opcode))
        exempt.Add(opcode);

    Assert.That(exempt, Is.EqualTo(new[] { ElevatedOpcode.ReadSmbios }));
  }

}
