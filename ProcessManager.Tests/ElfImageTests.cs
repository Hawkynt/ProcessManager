using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The ELF header reader (PRD §31).
/// </summary>
/// <remarks>
/// <para>
/// Built rather than recorded, and on purpose: a recorded shared object is one class, one endianness
/// and one machine, and pins a third-party binary into the repository to test them. Assembling the
/// bytes here covers ELF32 and big-endian too, which no file on any machine this is developed on
/// would have.
/// </para>
/// <para>
/// <see cref="ItReadsTheHeaderOfARealImage"/> then checks the same code against a real file, on the
/// one leg that has any — so a builder that agrees with a parser that is wrong still fails.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ElfImageTests {

  private const string _Soname = "libtest.so.1";
  private const string _Interpreter = "/lib64/ld-linux-x86-64.so.2";

  /// <summary>
  /// A complete, if pointless, ELF image: a header, three program headers, an interpreter string, a
  /// dynamic section naming a SONAME, and the string table it points into.
  /// </summary>
  /// <remarks>
  /// The load segment maps virtual address zero onto file offset zero, so that the address in
  /// <c>DT_STRTAB</c> is also its file offset — which is exactly the translation the reader has to
  /// perform, and would perform correctly by accident if the segment were not there at all. The
  /// second load segment is therefore given a bias, so that "by accident" fails.
  /// </remarks>
  private static byte[] Build(bool is64, bool isLittleEndian, ushort machine, ushort type, ulong entry) {
    const int InterpreterOffset = 256;
    const int DynamicOffset = 320;
    const int StringTableOffset = 384;
    const ulong LoadBias = 0x10000;

    var image = new byte[512];
    var headerSize = is64 ? 64 : 52;
    var programHeaderSize = is64 ? 56 : 32;

    image[0] = 0x7F;
    image[1] = (byte)'E';
    image[2] = (byte)'L';
    image[3] = (byte)'F';
    image[4] = (byte)(is64 ? 2 : 1);
    image[5] = (byte)(isLittleEndian ? 1 : 2);
    image[6] = 1;

    Write16(image, 16, type);
    Write16(image, 18, machine);
    WriteWord(image, 24, entry);
    WriteWord(image, is64 ? 32 : 28, (ulong)headerSize);
    Write16(image, is64 ? 54 : 42, (ushort)programHeaderSize);
    Write16(image, is64 ? 56 : 44, 3);

    // PT_LOAD, biased: virtual address 0x10000 lives at file offset 0.
    WriteProgramHeader(0, 1, 0, LoadBias, (ulong)image.Length);
    WriteProgramHeader(1, 2, DynamicOffset, LoadBias + DynamicOffset, 48);
    WriteProgramHeader(2, 3, InterpreterOffset, LoadBias + InterpreterOffset, (ulong)_Interpreter.Length + 1);

    // DT_SONAME points one byte into the string table, past its leading NUL; DT_STRTAB is where the
    // table is in memory, which is only the file offset once the bias is taken back off.
    WriteDynamic(0, 14, 1);
    WriteDynamic(1, 5, LoadBias + StringTableOffset);
    WriteDynamic(2, 0, 0);

    Encoding.ASCII.GetBytes(_Interpreter).CopyTo(image, InterpreterOffset);
    Encoding.ASCII.GetBytes(_Soname).CopyTo(image, StringTableOffset + 1);
    return image;

    void Write16(byte[] target, int offset, ushort value) {
      if (isLittleEndian)
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset), value);
      else
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset), value);
    }

    void WriteWord(byte[] target, int offset, ulong value) {
      if (is64) {
        if (isLittleEndian)
          BinaryPrimitives.WriteUInt64LittleEndian(target.AsSpan(offset), value);
        else
          BinaryPrimitives.WriteUInt64BigEndian(target.AsSpan(offset), value);

        return;
      }

      if (isLittleEndian)
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), (uint)value);
      else
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset), (uint)value);
    }

    void WriteProgramHeader(int index, uint segment, ulong fileOffset, ulong virtualAddress, ulong size) {
      var at = headerSize + index * programHeaderSize;
      if (isLittleEndian)
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), segment);
      else
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at), segment);

      WriteWord(image, at + (is64 ? 8 : 4), fileOffset);
      WriteWord(image, at + (is64 ? 16 : 8), virtualAddress);
      WriteWord(image, at + (is64 ? 32 : 16), size);
    }

    void WriteDynamic(int index, ulong tag, ulong value) {
      var at = DynamicOffset + index * (is64 ? 16 : 8);
      WriteWord(image, at, tag);
      WriteWord(image, at + (is64 ? 8 : 4), value);
    }
  }

  private static ElfImage.ElfRead Over(byte[] image) => (offset, buffer) => {
    if (offset < 0 || offset >= image.Length)
      return 0;

    var available = (int)Math.Min(buffer.Length, image.Length - offset);
    image.AsSpan((int)offset, available).CopyTo(buffer);
    return available;
  };

  [Test]
  public void ASharedObjectReportsItsNameMachineAndEntryPoint() {
    var image = Build(is64: true, isLittleEndian: true, machine: 62, type: 3, entry: 0x1120);

    Assert.That(ElfImage.TryDescribe(Over(image), out var description), Is.True);
    Assert.Multiple(() => {
      Assert.That(description.Type, Is.EqualTo(ModuleType.SharedObject));
      Assert.That(description.Architecture, Is.EqualTo("x86-64"));
      Assert.That(description.EntryPoint.Value, Is.EqualTo(0x1120ul));
      Assert.That(description.Soname, Is.EqualTo(_Soname));
      Assert.That(description.Interpreter, Is.EqualTo(_Interpreter));
    });
  }

  [Test]
  public void A32BitBigEndianImageIsReadTheSameWay() {
    // Neither half of this combination exists on any machine this is built on, which is the reason to
    // test it: the widths and the byte order are decided by two bytes of the header and by nothing
    // about the reader's own architecture.
    var image = Build(is64: false, isLittleEndian: false, machine: 20, type: 2, entry: 0x8048000);

    Assert.That(ElfImage.TryDescribe(Over(image), out var description), Is.True);
    Assert.Multiple(() => {
      Assert.That(description.Type, Is.EqualTo(ModuleType.Executable));
      Assert.That(description.Architecture, Is.EqualTo("ppc"));
      Assert.That(description.EntryPoint.Value, Is.EqualTo(0x8048000ul));
      Assert.That(description.Soname, Is.EqualTo(_Soname));
    });
  }

  [Test]
  public void AMachineNobodyNamedIsReportedAsItsNumber() {
    // Not null: "the header says 999" is true, and an empty cell would say instead that we were not
    // allowed to look (PRD §72.3).
    var image = Build(is64: true, isLittleEndian: true, machine: 999, type: 3, entry: 0);

    Assert.That(ElfImage.TryDescribe(Over(image), out var description), Is.True);
    Assert.That(description.Architecture, Is.EqualTo("machine 999"));
  }

  [Test]
  public void AMappedFileThatIsNotAnImageIsDataRatherThanAFailure() {
    // A process maps locale archives, fonts and databases. Reporting them as unreadable would blame
    // the permissions for something that is simply not an ELF file.
    var text = new byte[512];
    Encoding.ASCII.GetBytes("this is a locale archive, not a program").CopyTo(text, 0);

    Assert.That(ElfImage.TryDescribe(Over(text), out var description), Is.True);
    Assert.Multiple(() => {
      Assert.That(description.Type, Is.EqualTo(ModuleType.Data));
      Assert.That(description.Soname, Is.Null);
      Assert.That(description.EntryPoint.HasValue, Is.False);
    });
  }

  [Test]
  public void AFileTooShortToHaveAHeaderIsNotDescribed() {
    Assert.That(ElfImage.TryDescribe(Over(new byte[8]), out _), Is.False);
  }

  [Test]
  public void AnImageWithNoSonameSaysNothingRatherThanGuessing() {
    var image = Build(is64: true, isLittleEndian: true, machine: 62, type: 2, entry: 0x401000);
    // Turn DT_SONAME into DT_NULL, which ends the section — the dynamic section of a program usually
    // has no SONAME at all, and the file name is not a substitute for one.
    image[320] = 0;

    Assert.That(ElfImage.TryDescribe(Over(image), out var description), Is.True);
    Assert.That(description.Soname, Is.Null);
    Assert.That(description.Type, Is.EqualTo(ModuleType.Executable));
  }

  [Test]
  [Platform("Linux", Reason = "There is no ELF file to read on the other two legs.")]
  public void ItReadsTheHeaderOfARealImage() {
    // The C library of the machine running the test: a shared object, of this machine's architecture,
    // whose SONAME is by long convention its file name. Everything above is a construction of this
    // test file; this is the one assertion the kernel and the toolchain wrote.
    if (FindRealLibrary() is not { } libc) {
      Assert.Ignore("No libc mapping in this process's own maps file.");
      return;
    }

    using var handle = File.OpenHandle(libc, FileMode.Open, FileAccess.Read);
    Assert.That(
      ElfImage.TryDescribe((offset, buffer) => RandomAccess.Read(handle, buffer, offset), out var description),
      Is.True
    );

    Assert.Multiple(() => {
      Assert.That(description.Type, Is.EqualTo(ModuleType.SharedObject));
      Assert.That(description.Soname, Is.EqualTo(Path.GetFileName(libc)));
      Assert.That(description.Architecture, Is.EqualTo(RuntimeInformation.ProcessArchitecture switch {
        Architecture.X64 => "x86-64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "x86",
        _ => description.Architecture,
      }));
    });
  }

  /// <summary>The path of a mapped <c>libc</c>, straight out of this process's own map file.</summary>
  private static string? FindRealLibrary() {
    foreach (var module in MapsParser.Collect(File.ReadAllBytes("/proc/self/maps"), Counter.NotSupported))
      if (Path.GetFileName(module.Path).StartsWith("libc.so", StringComparison.Ordinal))
        return module.Path;

    return null;
  }

}
