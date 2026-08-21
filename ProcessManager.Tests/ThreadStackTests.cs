using System.Buffers.Binary;
using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The pieces §30 is built from: which mapping an address is in, which function inside it, and what
/// the viewer says about the part of a stack that is not there.
/// </summary>
/// <remarks>
/// Parsers and text, so this runs on every CI leg. The one test that needs a real ELF file reads this
/// process's own libc and skips where there is none (PRD §9.2).
/// </remarks>
[TestFixture]
public sealed class ThreadStackTests {

  private static ReadOnlySpan<byte> Bytes(string text) => Encoding.UTF8.GetBytes(text);

  private const string _Maps =
    "55c000000000-55c000001000 r--p 00000000 08:02 1234                       /opt/demo/worker\n"
    + "55c000001000-55c000002000 r-xp 00001000 08:02 1234                       /opt/demo/worker\n"
    + "7f1000000000-7f1000200000 r-xp 00000000 08:02 5678                       /usr/lib/libc.so.6\n"
    + "7f1000201000-7f1000210000 rw-p 00000000 00:00 0\n"
    + "7ffd00000000-7ffd00021000 rw-p 00000000 00:00 0                          [stack]\n";

  #region address map

  [Test]
  public void AnAddressIsFoundInTheMappingThatContainsIt() {
    var map = AddressMap.Parse(Bytes(_Maps));

    Assert.That(map.TryFind(0x7f1000012345, out var region), Is.True);
    Assert.That(region.Path, Is.EqualTo("/usr/lib/libc.so.6"));
    Assert.That(region.IsFile, Is.True);
  }

  /// <summary>
  /// The modules view folds one row per image and drops everything that is not one. An address lookup
  /// wants the opposite: a thread's stack is an anonymous mapping with no file behind it, and it is
  /// exactly the mapping that answers "how much stack is this thread using" (PRD §30).
  /// </summary>
  [Test]
  public void TheAnonymousAndPseudoMappingsAreKeptRatherThanFolderAway() {
    var map = AddressMap.Parse(Bytes(_Maps));

    Assert.That(map.TryFind(0x7ffd0001f000, out var stack), Is.True);
    Assert.That(stack.Path, Is.EqualTo("[stack]"));
    Assert.That(stack.IsFile, Is.False, "a name in square brackets is not a file that can be opened");
    Assert.That(stack.End, Is.EqualTo(0x7ffd00021000ul));

    Assert.That(map.TryFind(0x7f1000205000, out var anonymous), Is.True);
    Assert.That(anonymous.Path, Is.Null);
  }

  [Test]
  public void AnAddressInNoMappingIsNotFound() {
    var map = AddressMap.Parse(Bytes(_Maps));

    Assert.That(map.TryFind(0x10, out _), Is.False);
    Assert.That(map.TryFind(0xffff_ffff_ffff_0000, out _), Is.False);
    Assert.That(map.TryFind(0x7f1000200000, out _), Is.False, "the end of a range is not in it");
  }

  /// <summary>
  /// A library's later mappings have to report the address the first one is at, because that is the
  /// load bias — the number that has to come back off before the image's own symbol table can be
  /// asked about an address in it.
  /// </summary>
  [Test]
  public void EveryMappingOfAFileReportsTheAddressTheFirstOneIsAt() {
    var map = AddressMap.Parse(Bytes(_Maps));

    Assert.That(map.TryFind(0x55c000001800, out var second), Is.True);
    Assert.That(second.ModuleBase, Is.EqualTo(0x55c000000000ul), "not the start of this mapping");
    Assert.That(map.TryFindModuleBase("/opt/demo/worker", out var moduleBase), Is.True);
    Assert.That(moduleBase, Is.EqualTo(0x55c000000000ul));
    Assert.That(map.TryFindModuleBase("/usr/lib/libnothing.so", out _), Is.False);
  }

  [Test]
  public void AnUnlinkedFileKeepsItsPathWithoutTheKernelsSuffix() {
    var map = AddressMap.Parse(Bytes(
      "7f2000000000-7f2000001000 r-xp 00000000 08:02 42   /tmp/old.so (deleted)\n"
    ));

    Assert.That(map.TryFind(0x7f2000000010, out var region), Is.True);
    Assert.That(region.Path, Is.EqualTo("/tmp/old.so"));
  }

  #endregion

  #region symbols

  /// <summary>
  /// A complete, if pointless, ELF image: a string table, a symbol table naming four things, and the
  /// section headers that join them. Built rather than recorded, for the reason
  /// <see cref="ElfImageTests"/> records — a recorded object is one class and one endianness.
  /// </summary>
  private static byte[] BuildImage(bool is64, bool little) {
    var strings = new List<byte> { 0 };
    var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
    foreach (var name in (string[])["_start", "worker", "imported", "sizeless"]) {
      offsets[name] = (uint)strings.Count;
      strings.AddRange(Encoding.ASCII.GetBytes(name));
      strings.Add(0);
    }

    var headerSize = 64;
    var symbolSize = is64 ? 24 : 16;
    var sectionSize = is64 ? 64 : 40;
    var stringOffset = headerSize;
    var symbolOffset = stringOffset + strings.Count;
    // Four symbols: the mandatory null one, two functions with an extent, one import that defines
    // nothing, and one that declares no size at all.
    var symbols = new (string Name, byte Info, ushort Section, ulong Value, ulong Size)[] {
      (string.Empty, 0, 0, 0, 0),
      ("_start", 0x12, 1, 0x1000, 0x40),
      ("worker", 0x12, 1, 0x2000, 0x100),
      ("imported", 0x12, 0, 0x2000, 0x400),
      ("sizeless", 0x12, 1, 0x3000, 0),
    };

    var sectionOffset = symbolOffset + (symbols.Length * symbolSize);
    var image = new byte[sectionOffset + (3 * sectionSize)];

    image[0] = 0x7F;
    image[1] = (byte)'E';
    image[2] = (byte)'L';
    image[3] = (byte)'F';
    image[4] = (byte)(is64 ? 2 : 1);
    image[5] = (byte)(little ? 1 : 2);
    Write16(image.AsSpan(16), little, 3);                                     // ET_DYN
    Write16(image.AsSpan(18), little, 0x3E);                                  // x86-64
    if (is64)
      Write64(image.AsSpan(0x28), little, (ulong)sectionOffset);
    else
      Write32(image.AsSpan(0x20), little, (uint)sectionOffset);

    Write16(image.AsSpan(is64 ? 0x3A : 0x2E), little, (ushort)sectionSize);
    Write16(image.AsSpan(is64 ? 0x3C : 0x30), little, 3);

    strings.CopyTo(image, stringOffset);
    for (var i = 0; i < symbols.Length; ++i) {
      var at = image.AsSpan(symbolOffset + (i * symbolSize));
      var name = symbols[i].Name.Length == 0 ? 0u : offsets[symbols[i].Name];
      Write32(at, little, name);
      if (is64) {
        at[4] = symbols[i].Info;
        Write16(at[6..], little, symbols[i].Section);
        Write64(at[8..], little, symbols[i].Value);
        Write64(at[16..], little, symbols[i].Size);
      } else {
        Write32(at[4..], little, (uint)symbols[i].Value);
        Write32(at[8..], little, (uint)symbols[i].Size);
        at[12] = symbols[i].Info;
        Write16(at[14..], little, symbols[i].Section);
      }
    }

    // Section 0 is the mandatory null one; 1 is the string table; 2 is the symbol table that links
    // to it. The parser matches on type rather than on name, so no name table is needed.
    Section(1, type: 3, offset: stringOffset, size: strings.Count, link: 0, entrySize: 0);
    Section(2, type: 2, offset: symbolOffset, size: symbols.Length * symbolSize, link: 1, entrySize: symbolSize);
    return image;

    void Section(int index, uint type, int offset, int size, uint link, int entrySize) {
      var at = image.AsSpan(sectionOffset + (index * sectionSize));
      Write32(at[4..], little, type);
      if (is64) {
        Write64(at[24..], little, (ulong)offset);
        Write64(at[32..], little, (ulong)size);
        Write32(at[40..], little, link);
        Write64(at[56..], little, (ulong)entrySize);
      } else {
        Write32(at[16..], little, (uint)offset);
        Write32(at[20..], little, (uint)size);
        Write32(at[24..], little, link);
        Write32(at[36..], little, (uint)entrySize);
      }
    }
  }

  private static void Write16(Span<byte> target, bool little, ushort value) {
    if (little)
      BinaryPrimitives.WriteUInt16LittleEndian(target, value);
    else
      BinaryPrimitives.WriteUInt16BigEndian(target, value);
  }

  private static void Write32(Span<byte> target, bool little, uint value) {
    if (little)
      BinaryPrimitives.WriteUInt32LittleEndian(target, value);
    else
      BinaryPrimitives.WriteUInt32BigEndian(target, value);
  }

  private static void Write64(Span<byte> target, bool little, ulong value) {
    if (little)
      BinaryPrimitives.WriteUInt64LittleEndian(target, value);
    else
      BinaryPrimitives.WriteUInt64BigEndian(target, value);
  }

  private static ElfImage.ElfRead Reader(byte[] image) => (offset, buffer) => {
    if (offset < 0 || offset >= image.Length)
      return 0;

    var length = (int)Math.Min(buffer.Length, image.Length - offset);
    image.AsSpan((int)offset, length).CopyTo(buffer);
    return length;
  };

  [TestCase(true, true)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(false, false)]
  public void AnAddressInsideAFunctionResolvesToItsNameAndOffset(bool is64, bool little) {
    var read = Reader(BuildImage(is64, little));

    Assert.That(ElfSymbols.TryResolve(read, 0x1010, out var start), Is.True);
    Assert.That(start.Name, Is.EqualTo("_start"));
    Assert.That(start.Displacement, Is.EqualTo(0x10ul));

    Assert.That(ElfSymbols.TryResolve(read, 0x2000, out var worker), Is.True);
    Assert.That(worker.Name, Is.EqualTo("worker"));
    Assert.That(worker.Displacement, Is.EqualTo(0ul), "the first instruction of a function is not a hole");
  }

  /// <summary>
  /// A symbol with a size says where it ends. Letting it answer for the gap after it is how a stack
  /// viewer names a frame after whatever happens to be linked in front of it.
  /// </summary>
  [Test]
  public void AnAddressPastTheEndOfASizedFunctionIsNotInIt() {
    var read = Reader(BuildImage(is64: true, little: true));

    // worker runs to 0x2100 and _start to 0x1040. Both say where they end, so the gap between the
    // functions belongs to neither and the honest answer is that there is no symbol for it.
    Assert.That(ElfSymbols.TryResolve(read, 0x2100, out _), Is.False);
    Assert.That(ElfSymbols.TryResolve(read, 0x20ff, out var inside), Is.True, "one byte earlier is still inside it");
    Assert.That(inside.Name, Is.EqualTo("worker"));
  }

  /// <summary>
  /// <c>SHN_UNDEF</c>: a symbol this image refers to and does not define. Its value is not an address
  /// in this image, and matching against it names every low address after an import.
  /// </summary>
  [Test]
  public void AnUndefinedImportIsNeverTheAnswer() {
    var read = Reader(BuildImage(is64: true, little: true));

    Assert.That(ElfSymbols.TryResolve(read, 0x2040, out var match), Is.True);
    Assert.That(match.Name, Is.EqualTo("worker"), "and not the import that claims the same address");
  }

  [Test]
  public void ASymbolThatDeclaresNoSizeIsTheNearestPrecedingFallback() {
    var read = Reader(BuildImage(is64: true, little: true));

    Assert.That(ElfSymbols.TryResolve(read, 0x3120, out var match), Is.True);
    Assert.That(match.Name, Is.EqualTo("sizeless"));
    Assert.That(match.Displacement, Is.EqualTo(0x120ul));
  }

  [Test]
  public void AnAddressBelowEverySymbolResolvesToNothing() {
    var read = Reader(BuildImage(is64: true, little: true));

    Assert.That(ElfSymbols.TryResolve(read, 0x10, out _), Is.False);
  }

  [Test]
  public void SomethingThatIsNotAnElfImageResolvesToNothing() {
    Assert.That(ElfSymbols.TryResolve(Reader(new byte[256]), 0x1000, out _), Is.False);
  }

  /// <summary>
  /// The same code against a real file, on the one leg that has any — so a builder that agrees with
  /// a parser that is wrong still fails.
  /// </summary>
  [Test]
  [Platform("Linux")]
  public void ItResolvesASymbolInThisProcessesOwnLibc() {
    string? libc = null;
    foreach (var line in File.ReadLines("/proc/self/maps"))
      if (line.Contains("/libc.so.6", StringComparison.Ordinal) && line.Contains("r-xp", StringComparison.Ordinal)) {
        libc = line[line.IndexOf('/')..];
        break;
      }

    if (libc is null) {
      Assert.Ignore("No executable libc mapping in this process's own maps file.");
      return;
    }

    using var handle = File.OpenHandle(libc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var found = 0;
    // Sampled across the text segment rather than asserting one name: which symbols a distribution's
    // libc exports, and where, is not this test's business — that the reader finds real ones is.
    for (var address = 0x1000ul; address < 0x200000 && found < 4; address += 0x2000)
      if (ElfSymbols.TryResolve((offset, buffer) => Read(handle, buffer, offset), address, out var match)) {
        Assert.That(match.Name, Is.Not.Empty);
        ++found;
      }

    Assert.That(found, Is.GreaterThan(0), "libc names nothing anywhere in its first two megabytes");

    static int Read(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> buffer, long offset) {
      var total = 0;
      while (total < buffer.Length) {
        var read = RandomAccess.Read(handle, buffer[total..], offset + total);
        if (read <= 0)
          break;

        total += read;
      }

      return total;
    }
  }

  #endregion

  #region what the viewer says

  /// <summary>
  /// The sentence above the list is the difference between "that is the whole stack" and "that is
  /// what the kernel would say". A viewer that dropped it would be lying by omission (PRD §30).
  /// </summary>
  [Test]
  public void TheViewerSaysWhyTheUserFramesAreMissing() {
    var stack = new ThreadStack(7, [], UnknownReason.NotPermitted, UnknownReason.NotSupportedOnPlatform);
    var text = Ui.Desktop.StackWindow.Summarize(in stack, 7, "worker", resolved: false);

    Assert.That(text, Does.Contain("thread 7 (worker)"));
    Assert.That(text, Does.Contain("CAP_SYS_ADMIN"), "what to do about the refusal");
    Assert.That(text, Does.Contain("not unwound"), "and that the program's own frames are absent");
    Assert.That(text, Does.Contain("DWARF"), "and why there are no source lines");
  }

  [Test]
  public void AReadKernelStackSaysHowManyFramesItGot() {
    var frames = KernelStackParser.Parse(Bytes("[<0>] a+0x1/0x2\n[<0>] b+0x1/0x2\n"));
    var stack = new ThreadStack(7, frames, UnknownReason.None, UnknownReason.NotSupportedOnPlatform);

    Assert.That(stack.KernelFrameCount, Is.EqualTo(2));
    Assert.That(Ui.Desktop.StackWindow.Summarize(in stack, 7, null, resolved: true), Does.Contain("2 frame(s) read"));
  }

  [Test]
  public void TheSavedTextCarriesTheFramesAndTheExplanation() {
    var frames = KernelStackParser.Parse(Bytes("[<0>] futex_wait+0x1e0/0x2c0\n"));
    var stack = new ThreadStack(7, frames, UnknownReason.None, UnknownReason.NotSupportedOnPlatform);
    var text = Ui.Desktop.StackWindow.Describe(in stack, "worker");

    Assert.That(text, Does.Contain("futex_wait+0x1e0"));
    Assert.That(text, Does.Contain("not unwound"));
  }

  #endregion

}
