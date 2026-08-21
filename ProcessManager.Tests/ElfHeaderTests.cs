using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What an executable says it is (PRD §14).
/// </summary>
/// <remarks>
/// A pure function of the bytes, so the whole table runs on every CI leg without a single executable
/// being present — including the architectures nobody here has, which is the point: a machine
/// cannot check its own answer for a binary it cannot run.
/// </remarks>
[TestFixture]
public sealed class ElfHeaderTests {

  /// <summary>
  /// Builds a 64-bit little-endian ELF with one program header.
  /// </summary>
  /// <param name="interpreter">Null for a static binary — no PT_INTERP entry at all.</param>
  private static byte[] Elf(int machine, string? interpreter, int type = 3, bool little = true, int bits = 64) {
    var file = new byte[512];
    file[0] = 0x7F;
    file[1] = (byte)'E';
    file[2] = (byte)'L';
    file[3] = (byte)'F';
    file[4] = (byte)(bits == 64 ? 2 : 1);
    file[5] = (byte)(little ? 1 : 2);

    Write16(file, 16, (ushort)type, little);
    Write16(file, 18, (ushort)machine, little);

    if (interpreter is null) {
      Write16(file, 56, 0, little);
      return file;
    }

    const int HeaderOffset = 64;
    const int EntrySize = 56;
    const int TextOffset = 256;

    Write64(file, 32, HeaderOffset, little);
    Write16(file, 54, EntrySize, little);
    Write16(file, 56, 1, little);

    // One PT_INTERP entry pointing at the string.
    Write32(file, HeaderOffset, 3, little);
    Write64(file, HeaderOffset + 8, TextOffset, little);
    Write64(file, HeaderOffset + 32, (ulong)(interpreter.Length + 1), little);

    var bytes = System.Text.Encoding.UTF8.GetBytes(interpreter);
    bytes.CopyTo(file, TextOffset);
    return file;
  }

  private static void Write16(byte[] to, int at, ushort value, bool little) {
    if (little) {
      to[at] = (byte)value;
      to[at + 1] = (byte)(value >> 8);
    } else {
      to[at] = (byte)(value >> 8);
      to[at + 1] = (byte)value;
    }
  }

  private static void Write32(byte[] to, int at, uint value, bool little) {
    for (var i = 0; i < 4; ++i)
      to[at + i] = (byte)(value >> (8 * (little ? i : 3 - i)));
  }

  private static void Write64(byte[] to, int at, ulong value, bool little) {
    for (var i = 0; i < 8; ++i)
      to[at + i] = (byte)(value >> (8 * (little ? i : 7 - i)));
  }

  [Test]
  public void AnOrdinaryDynamicExecutableIsReadWhole() {
    var image = ElfHeader.Read(Elf(0x3E, "/lib64/ld-linux-x86-64.so.2"));

    Assert.That(image, Is.Not.Null);
    Assert.That(image!.Value.Architecture, Is.EqualTo("x86-64"));
    Assert.That(image.Value.Bits, Is.EqualTo(64));
    Assert.That(image.Value.Interpreter, Is.EqualTo("/lib64/ld-linux-x86-64.so.2"));
    Assert.That(image.Value.IsPositionIndependent, Is.True);
  }

  /// <summary>
  /// The whole reason this field exists: on a machine that runs more than one architecture, the
  /// machine's answer is not the program's. An x86-64 kernel runs 32-bit binaries every day.
  /// </summary>
  [Test]
  public void EachArchitectureIsNamedTheWayItsToolchainNamesIt() {
    Assert.That(ElfHeader.Read(Elf(0x03, null))!.Value.Architecture, Is.EqualTo("x86"));
    Assert.That(ElfHeader.Read(Elf(0xB7, null))!.Value.Architecture, Is.EqualTo("AArch64"));
    Assert.That(ElfHeader.Read(Elf(0x28, null))!.Value.Architecture, Is.EqualTo("ARM"));
    Assert.That(ElfHeader.Read(Elf(0xF3, null))!.Value.Architecture, Is.EqualTo("RISC-V"));
  }

  /// <summary>
  /// A new architecture appears every few years, and calling it the wrong thing is worse than
  /// admitting the number is unfamiliar.
  /// </summary>
  [Test]
  public void AnArchitectureNobodyKnowsIsReportedAsItsNumber() =>
    Assert.That(ElfHeader.Read(Elf(0x5555, null))!.Value.Architecture, Does.Contain("5555"));

  /// <summary>
  /// Byte order is in the header, so a big-endian binary decodes on a little-endian machine — which
  /// is the whole reason the field is there, and the case nobody's laptop can produce.
  /// </summary>
  [Test]
  public void ABigEndianBinaryIsReadOnALittleEndianMachine() {
    var image = ElfHeader.Read(Elf(0x15, "/lib64/ld64.so.2", little: false));

    Assert.That(image!.Value.Architecture, Is.EqualTo("PowerPC64"));
    Assert.That(image.Value.Interpreter, Is.EqualTo("/lib64/ld64.so.2"));
  }

  [Test]
  public void AThirtyTwoBitBinaryIsReadAsOne() =>
    Assert.That(ElfHeader.Read(Elf(0x03, null, bits: 32))!.Value.Bits, Is.EqualTo(32));

  /// <summary>
  /// No interpreter is a real answer — the program is statically linked — and a different one from
  /// a header nobody could read. The caller can only tell them apart because one of them is null.
  /// </summary>
  [Test]
  public void AStaticBinaryHasNoInterpreterAndIsStillReadable() {
    var image = ElfHeader.Read(Elf(0x3E, null));

    Assert.That(image, Is.Not.Null, "the header was read");
    Assert.That(image!.Value.Interpreter, Is.Null, "and there is genuinely no interpreter");
  }

  /// <summary>
  /// A non-PIE executable lands at the same address every run whatever the kernel does, which is
  /// what makes the distinction worth reporting.
  /// </summary>
  [Test]
  public void APositionDependentExecutableIsToldApartFromAPie() {
    // ET_EXEC is 2; ET_DYN is 3 and is what a PIE is built as.
    Assert.That(ElfHeader.Read(Elf(0x3E, null, type: 2))!.Value.IsPositionIndependent, Is.False);
    Assert.That(ElfHeader.Read(Elf(0x3E, null, type: 3))!.Value.IsPositionIndependent, Is.True);
  }

  #region scripts

  /// <summary>
  /// A shebang is as real a way to start a program on Linux as an ELF header, and reporting "not an
  /// executable" for every shell script would be wrong about a large part of any machine.
  /// </summary>
  [Test]
  public void AScriptIsRecognisedByItsShebang() {
    var image = ElfHeader.Read("#!/bin/bash\necho hello\n"u8);

    Assert.That(image, Is.Not.Null);
    Assert.That(image!.Value.Architecture, Is.EqualTo("script"));
    Assert.That(image.Value.Interpreter, Is.EqualTo("/bin/bash"));
  }

  /// <summary>The interpreter is the program, not the program plus its switches.</summary>
  [Test]
  public void AShebangsArgumentsAreNotPartOfItsProgram() {
    Assert.That(ElfHeader.Read("#!/usr/bin/env python3\n"u8)!.Value.Interpreter, Is.EqualTo("/usr/bin/env"));
    Assert.That(ElfHeader.Read("#! /bin/sh -e\n"u8)!.Value.Interpreter, Is.EqualTo("/bin/sh"));
  }

  #endregion

  #region what is not an executable

  [Test]
  public void SomethingThatIsNotAnExecutableIsNull() {
    Assert.That(ElfHeader.Read("just some text"u8), Is.Null);
    Assert.That(ElfHeader.Read([]), Is.Null);
    Assert.That(ElfHeader.Read([0x7F, (byte)'E']), Is.Null, "the magic alone is not a header");
  }

  /// <summary>
  /// Only a page is read, so a program header table beyond it cannot be followed. That yields no
  /// interpreter — the same answer a static binary gives — rather than reading past the buffer.
  /// </summary>
  [Test]
  public void AProgramHeaderBeyondWhatWeWereGivenIsNotChasedOffTheEnd() {
    var file = Elf(0x3E, "/lib/ld.so");
    Write64(file, 32, 100_000, little: true);

    Assert.That(() => ElfHeader.Read(file), Throws.Nothing);
    Assert.That(ElfHeader.Read(file)!.Value.Interpreter, Is.Null);
  }

  /// <summary>A corrupt file must not be able to walk the reader off the end of the buffer.</summary>
  [Test]
  public void AnInterpreterStringClaimingToBeHugeIsRefused() {
    var file = Elf(0x3E, "/lib/ld.so");
    Write64(file, 64 + 32, ulong.MaxValue, little: true);

    Assert.That(() => ElfHeader.Read(file), Throws.Nothing);
    Assert.That(ElfHeader.Read(file)!.Value.Interpreter, Is.Null);
  }

  #endregion

}
