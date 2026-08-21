using System.Buffers.Binary;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What an executable is, read from its own first bytes (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// The architecture a program was built for, which on a machine that can run more than one is not
/// the machine's architecture — an x86-64 kernel runs 32-bit binaries, an ARM64 one runs ARM32, and
/// a process table that reports the machine's answer for every row is telling you about the machine
/// rather than about the program.
/// </para>
/// <para>
/// A pure function of the bytes, so the whole table is exercised on every CI leg without a single
/// executable being present (PRD §9.2). Only the first page is needed: the header is 64 bytes and
/// the program headers that name the interpreter follow it immediately in every linker's output.
/// </para>
/// </remarks>
public static class ElfHeader {

  /// <summary>What a file turned out to be.</summary>
  /// <param name="Architecture">
  /// The machine the program was built for, named the way the toolchains name it.
  /// </param>
  /// <param name="Bits">32 or 64.</param>
  /// <param name="Interpreter">
  /// The dynamic loader, or the shebang's program for a script. Null for a static binary — which is
  /// a real answer and a different one from "we could not read it".
  /// </param>
  /// <param name="IsPositionIndependent">
  /// Built to be loaded anywhere, which is what makes address-space randomisation useful. A
  /// non-PIE executable lands at the same address every run whatever the kernel does.
  /// </param>
  public readonly record struct Image(string Architecture, int Bits, string? Interpreter, bool IsPositionIndependent);

  private const int _HeaderLength = 64;

  private const int _PtInterp = 3;

  /// <summary>ET_DYN — shared object, which is what a position-independent executable also is.</summary>
  private const int _TypeDynamic = 3;

  /// <summary>
  /// Reads what the first bytes of a file say about it, or null when they say nothing.
  /// </summary>
  /// <remarks>
  /// A script is recognised too. On Linux a shebang is as real a way to start a program as an ELF
  /// header, and reporting "not an executable" for every shell script would be wrong about a large
  /// part of any machine.
  /// </remarks>
  public static Image? Read(ReadOnlySpan<byte> file) {
    if (file.Length >= 2 && file[0] == '#' && file[1] == '!')
      return new("script", 0, Shebang(file), false);

    if (file.Length < _HeaderLength || file[0] != 0x7F || file[1] != 'E' || file[2] != 'L' || file[3] != 'F')
      return null;

    var bits = file[4] switch { 1 => 32, 2 => 64, _ => 0 };
    if (bits == 0)
      return null;

    // Byte order is in the header, so a big-endian binary read on a little-endian machine still
    // decodes — which matters for the MIPS and PowerPC builds that are the whole reason the field
    // exists.
    var little = file[5] != 2;
    var type = Read16(file[16..], little);
    var machine = Read16(file[18..], little);

    return new(
      Machine(machine),
      bits,
      Interpreter(file, bits, little),
      type == _TypeDynamic
    );
  }

  /// <summary>The rest of the shebang line, without its arguments.</summary>
  private static string? Shebang(ReadOnlySpan<byte> file) {
    var line = file[2..];
    var end = line.IndexOfAny((byte)'\n', (byte)'\r');
    if (end >= 0)
      line = line[..end];

    line = line.TrimStart((byte)' ');
    var space = line.IndexOf((byte)' ');
    if (space >= 0)
      line = line[..space];

    return line.IsEmpty ? null : System.Text.Encoding.UTF8.GetString(line);
  }

  /// <summary>
  /// The dynamic loader, from the <c>PT_INTERP</c> program header.
  /// </summary>
  /// <remarks>
  /// Null means statically linked, which is a real answer. It is also what a caller sees when the
  /// program headers lie beyond the bytes we were given — so only a whole first page is worth
  /// passing, and every linker in use puts them within it.
  /// </remarks>
  private static string? Interpreter(ReadOnlySpan<byte> file, int bits, bool little) {
    var wide = bits == 64;
    var headerOffset = wide ? (long)Read64(file[32..], little) : Read32(file[28..], little);
    var entrySize = wide ? Read16(file[54..], little) : Read16(file[42..], little);
    var count = wide ? Read16(file[56..], little) : Read16(file[44..], little);
    if (headerOffset <= 0 || entrySize <= 0 || count <= 0)
      return null;

    for (var i = 0; i < count; ++i) {
      var at = headerOffset + ((long)i * entrySize);
      if (at < 0 || at + entrySize > file.Length)
        return null;

      var entry = file[(int)at..];
      if (Read32(entry, little) != _PtInterp)
        continue;

      var offset = wide ? (long)Read64(entry[8..], little) : Read32(entry[4..], little);
      var size = wide ? (long)Read64(entry[32..], little) : Read32(entry[16..], little);
      if (offset < 0 || size <= 0 || offset + size > file.Length)
        return null;

      var text = file.Slice((int)offset, (int)size);
      var nul = text.IndexOf((byte)0);
      if (nul >= 0)
        text = text[..nul];

      return text.IsEmpty ? null : System.Text.Encoding.UTF8.GetString(text);
    }

    return null;
  }

  private static ushort Read16(ReadOnlySpan<byte> at, bool little)
    => little ? BinaryPrimitives.ReadUInt16LittleEndian(at) : BinaryPrimitives.ReadUInt16BigEndian(at);

  private static uint Read32(ReadOnlySpan<byte> at, bool little)
    => little ? BinaryPrimitives.ReadUInt32LittleEndian(at) : BinaryPrimitives.ReadUInt32BigEndian(at);

  private static ulong Read64(ReadOnlySpan<byte> at, bool little)
    => little ? BinaryPrimitives.ReadUInt64LittleEndian(at) : BinaryPrimitives.ReadUInt64BigEndian(at);

  /// <summary>
  /// The machine numbers worth naming.
  /// </summary>
  /// <remarks>
  /// Named as the toolchains name them rather than as the specification does — somebody reading this
  /// wants "x86-64", not "EM_X86_64". A number nobody knows is reported as its number, because a new
  /// architecture appears every few years and calling it the wrong thing is worse than admitting it.
  /// </remarks>
  private static string Machine(int machine) => machine switch {
    0x03 => "x86",
    0x08 => "MIPS",
    0x14 => "PowerPC",
    0x15 => "PowerPC64",
    0x16 => "S390",
    0x28 => "ARM",
    0x2A => "SuperH",
    0x32 => "IA-64",
    0x3E => "x86-64",
    0xB7 => "AArch64",
    0xF3 => "RISC-V",
    0x102 => "LoongArch",
    _ => $"machine 0x{machine:x}",
  };

}
