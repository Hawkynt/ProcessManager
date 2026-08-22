using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The read-only binary inspector (PRD §53) and the strings view inside it (PRD §35).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three kinds of evidence, and they are not equally strong.</strong> The PE pages are held
/// against real images this repository built, on whichever machine the tests run on. The ELF pages
/// are held against a fixture written byte by byte here <em>and</em>, where the machine has one,
/// against a real shared object — and the whole ELF side was additionally checked by hand against
/// <c>readelf -hldSr</c>, <c>objdump -T</c> and <c>nm -D</c> on <c>/usr/bin/ls</c>,
/// <c>/usr/lib/libc.so.6</c> and <c>/usr/lib/libcap.so.2</c>, which is where the symbol-version
/// walk and the relocation counts were confirmed. The Mach-O pages have only the fixture: there is
/// no Darwin binary on the machines this is built on, and that is written down rather than glossed
/// over.
/// </para>
/// <para>
/// The fixtures are built rather than checked in, because a fixture whose bytes nobody can read is a
/// fixture nobody can correct. Every offset below is computed from the structure rather than typed
/// as a number, so a builder that lays the file out differently still produces a valid one.
/// </para>
/// </remarks>
[TestFixture]
public sealed class BinaryInspectorTests {

  #region a fixture ELF

  private const string _Soname = "libinspect.so.1";
  private const string _Needed = "libc.so.6";
  private const string _Interpreter = "/lib64/ld-inspect.so.1";
  private const string _Import = "memcpy";
  private const string _Export = "inspect_entry";

  /// <summary>
  /// A 64-bit little-endian shared object with everything §53's ELF box names in it.
  /// </summary>
  /// <remarks>
  /// Program headers, section headers, a dynamic section, an interpreter, a dynamic symbol table
  /// with one import and one export, and a relocation section. Deliberately not produced by a
  /// linker: a fixture a linker made would test whether this reads that linker's output, and a
  /// fixture written to the specification tests whether it reads the specification.
  /// </remarks>
  private static byte[] BuildElf() {
    // The layout, chosen so that every structure is aligned and nothing overlaps. Written out as
    // constants rather than computed cumulatively because a test whose expectations depend on its
    // own arithmetic proves only that the arithmetic is consistent.
    const int programHeaders = 64;
    const int interpreterAt = 0x100;
    const int dynamicStringsAt = 0x140;
    const int dynamicSymbolsAt = 0x200;
    const int dynamicAt = 0x280;
    const int textAt = 0x300;
    const int relocationsAt = 0x340;
    const int sectionStringsAt = 0x380;
    const int sectionHeadersAt = 0x400;
    const int sections = 7;
    const int total = sectionHeadersAt + (sections * 64);

    var file = new byte[total];

    // The string tables first: everything else points into them.
    var strings = new StringTable();
    strings.Add(string.Empty);
    var neededAt = strings.Add(_Needed);
    var sonameAt = strings.Add(_Soname);
    var importAt = strings.Add(_Import);
    var exportAt = strings.Add(_Export);
    strings.Bytes.CopyTo(file.AsSpan(dynamicStringsAt));

    var names = new StringTable();
    names.Add(string.Empty);
    var dynstrName = names.Add(".dynstr");
    var dynsymName = names.Add(".dynsym");
    var dynamicName = names.Add(".dynamic");
    var textName = names.Add(".text");
    var relaName = names.Add(".rela.dyn");
    var shstrName = names.Add(".shstrtab");
    names.Bytes.CopyTo(file.AsSpan(sectionStringsAt));

    Encoding.ASCII.GetBytes(_Interpreter).CopyTo(file.AsSpan(interpreterAt));

    // e_ident, then the header.
    file[0] = 0x7F;
    file[1] = (byte)'E';
    file[2] = (byte)'L';
    file[3] = (byte)'F';
    file[4] = 2;
    file[5] = 1;
    file[6] = 1;
    Write16(file, 16, 3);
    Write16(file, 18, 62);
    Write32(file, 20, 1);
    Write64(file, 24, 0x1234);
    Write64(file, 32, programHeaders);
    Write64(file, 0x28, sectionHeadersAt);
    Write16(file, 0x34, 64);
    Write16(file, 0x36, 56);
    Write16(file, 0x38, 3);
    Write16(file, 0x3A, 64);
    Write16(file, 0x3C, sections);
    Write16(file, 0x3E, 6);

    // PT_LOAD covering the whole file at address nought, so that an address is a file offset; then
    // the interpreter and the dynamic section.
    Segment(file, programHeaders, type: 1, flags: 5, offset: 0, address: 0, size: total);
    Segment(file, programHeaders + 56, type: 3, flags: 4, offset: interpreterAt, address: interpreterAt, size: _Interpreter.Length + 1);
    Segment(file, programHeaders + 112, type: 2, flags: 6, offset: dynamicAt, address: dynamicAt, size: 5 * 16);

    // Three symbols: the reserved nought, one undefined import and one defined export.
    Symbol(file, dynamicSymbolsAt, 0, 0, 0, 0, 0);
    Symbol(file, dynamicSymbolsAt + 24, importAt, 0, 0, info: 0x12, section: 0);
    Symbol(file, dynamicSymbolsAt + 48, exportAt, 0x1000, 0x20, info: 0x12, section: 4);

    Dynamic(file, dynamicAt, 1, (ulong)neededAt);
    Dynamic(file, dynamicAt + 16, 14, (ulong)sonameAt);
    Dynamic(file, dynamicAt + 32, 5, dynamicStringsAt);
    Dynamic(file, dynamicAt + 48, 10, (ulong)strings.Bytes.Count);
    Dynamic(file, dynamicAt + 64, 0, 0);

    // One R_X86_64_GLOB_DAT against symbol 1, which is what an import's relocation looks like.
    Write64(file, relocationsAt, 0x2000);
    Write64(file, relocationsAt + 8, (1ul << 32) | 6);
    Write64(file, relocationsAt + 16, 0);

    Section(file, sectionHeadersAt, 0, 0, 0, 0, 0, 0, 0, 0);
    Section(file, sectionHeadersAt + 64, dynstrName, type: 3, flags: 2, address: dynamicStringsAt, offset: dynamicStringsAt, size: strings.Bytes.Count, link: 0, entry: 0);
    Section(file, sectionHeadersAt + 128, dynsymName, type: 11, flags: 2, address: dynamicSymbolsAt, offset: dynamicSymbolsAt, size: 72, link: 1, entry: 24);
    Section(file, sectionHeadersAt + 192, dynamicName, type: 6, flags: 3, address: dynamicAt, offset: dynamicAt, size: 5 * 16, link: 1, entry: 16);
    Section(file, sectionHeadersAt + 256, textName, type: 1, flags: 6, address: textAt, offset: textAt, size: 0x40, link: 0, entry: 0);
    Section(file, sectionHeadersAt + 320, relaName, type: 4, flags: 2, address: relocationsAt, offset: relocationsAt, size: 24, link: 2, entry: 24);
    Section(file, sectionHeadersAt + 384, shstrName, type: 3, flags: 0, address: 0, offset: sectionStringsAt, size: names.Bytes.Count, link: 0, entry: 0);
    return file;
  }

  private sealed class StringTable {

    public List<byte> Bytes { get; } = [];

    public int Add(string value) {
      var at = this.Bytes.Count;
      this.Bytes.AddRange(Encoding.ASCII.GetBytes(value));
      this.Bytes.Add(0);
      return at;
    }

  }

  private static void Segment(byte[] file, int at, uint type, uint flags, long offset, ulong address, long size) {
    Write32(file, at, type);
    Write32(file, at + 4, flags);
    Write64(file, at + 8, (ulong)offset);
    Write64(file, at + 16, address);
    Write64(file, at + 24, address);
    Write64(file, at + 32, (ulong)size);
    Write64(file, at + 40, (ulong)size);
    Write64(file, at + 48, 0x1000);
  }

  private static void Section(byte[] file, int at, int name, uint type, ulong flags, ulong address, long offset, long size, uint link, ulong entry) {
    Write32(file, at, (uint)name);
    Write32(file, at + 4, type);
    Write64(file, at + 8, flags);
    Write64(file, at + 16, address);
    Write64(file, at + 24, (ulong)offset);
    Write64(file, at + 32, (ulong)size);
    Write32(file, at + 40, link);
    Write32(file, at + 44, 0);
    Write64(file, at + 48, 8);
    Write64(file, at + 56, entry);
  }

  private static void Symbol(byte[] file, int at, int name, ulong value, ulong size, byte info, ushort section) {
    Write32(file, at, (uint)name);
    file[at + 4] = info;
    file[at + 5] = 0;
    Write16(file, at + 6, section);
    Write64(file, at + 8, value);
    Write64(file, at + 16, size);
  }

  private static void Dynamic(byte[] file, int at, ulong tag, ulong value) {
    Write64(file, at, tag);
    Write64(file, at + 8, value);
  }

  private static void Write16(byte[] file, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), value);

  private static void Write32(byte[] file, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);

  private static void Write64(byte[] file, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at), value);

  #endregion

  #region a fixture Mach-O

  /// <summary>
  /// A 64-bit little-endian dylib with the load commands §53's Mach-O box names.
  /// </summary>
  /// <remarks>
  /// A segment with a section in it, a dependency, a build identity, a symbol table with one import
  /// and one export, and a code-signature record. This is the whole of the Mach-O evidence: there
  /// is no Darwin machine here to hold the output against <c>otool</c> on.
  /// </remarks>
  private static byte[] BuildMachO() {
    const string dylib = "/usr/lib/libSystem.B.dylib";
    var commands = new List<byte[]>();

    // LC_SEGMENT_64 __TEXT, with one section __text inside it.
    var segment = new byte[72 + 80];
    Write32(segment, 0, 0x19);
    Write32(segment, 4, (uint)segment.Length);
    Encoding.ASCII.GetBytes("__TEXT").CopyTo(segment.AsSpan(8));
    Write64(segment, 24, 0x100000000);
    Write64(segment, 32, 0x4000);
    Write64(segment, 40, 0);
    Write64(segment, 48, 0x4000);
    Write32(segment, 56, 7);
    Write32(segment, 60, 5);
    Write32(segment, 64, 1);
    Encoding.ASCII.GetBytes("__text").CopyTo(segment.AsSpan(72));
    Encoding.ASCII.GetBytes("__TEXT").CopyTo(segment.AsSpan(88));
    Write64(segment, 104, 0x100001000);
    Write64(segment, 112, 0x200);
    Write32(segment, 120, 0x1000);
    Write32(segment, 124, 4);
    Write32(segment, 136, 0x80000400);
    commands.Add(segment);

    // LC_LOAD_DYLIB, whose name is packed in after the fixed part at the offset the header names.
    var load = new byte[24 + Align(dylib.Length + 1, 8)];
    Write32(load, 0, 0x0C);
    Write32(load, 4, (uint)load.Length);
    Write32(load, 8, 24);
    Write32(load, 16, (1u << 16) | (2 << 8) | 3);
    Write32(load, 20, (1u << 16) | (0 << 8) | 0);
    Encoding.ASCII.GetBytes(dylib).CopyTo(load.AsSpan(24));
    commands.Add(load);

    var uuid = new byte[24];
    Write32(uuid, 0, 0x1B);
    Write32(uuid, 4, 24);
    for (var i = 0; i < 16; ++i)
      uuid[8 + i] = (byte)(i + 1);

    commands.Add(uuid);

    var signature = new byte[16];
    Write32(signature, 0, 0x1D);
    Write32(signature, 4, 16);
    Write32(signature, 8, 0x3000);
    Write32(signature, 12, 0x120);
    commands.Add(signature);

    var symbols = new byte[24];
    Write32(symbols, 0, 0x02);
    Write32(symbols, 4, 24);
    commands.Add(symbols);

    var size = 0;
    foreach (var command in commands)
      size += command.Length;

    // The symbol and string tables go after the commands, so their offsets are known only now.
    var stringTable = new StringTable();
    stringTable.Add(string.Empty);
    var importAt = stringTable.Add("_malloc");
    var exportAt = stringTable.Add("_inspect_entry");

    var symbolsAt = 32 + size;
    var stringsAt = symbolsAt + 32;
    Write32(symbols, 8, (uint)symbolsAt);
    Write32(symbols, 12, 2);
    Write32(symbols, 16, (uint)stringsAt);
    Write32(symbols, 20, (uint)stringTable.Bytes.Count);

    var file = new byte[stringsAt + stringTable.Bytes.Count];
    // The magic is written in the image's own byte order: CF FA ED FE is a little-endian 64-bit one.
    file[0] = 0xCF;
    file[1] = 0xFA;
    file[2] = 0xED;
    file[3] = 0xFE;
    Write32(file, 4, 0x0100000C);
    Write32(file, 8, 0);
    Write32(file, 12, 6);
    Write32(file, 16, (uint)commands.Count);
    Write32(file, 20, (uint)size);
    Write32(file, 24, 0x00200085);

    var at = 32;
    foreach (var command in commands) {
      command.CopyTo(file.AsSpan(at));
      at += command.Length;
    }

    // n_type 0x01 is undefined-and-external; 0x0F is defined-in-a-section-and-external.
    Write32(file, symbolsAt, (uint)importAt);
    file[symbolsAt + 4] = 0x01;
    Write32(file, symbolsAt + 16, (uint)exportAt);
    file[symbolsAt + 16 + 4] = 0x0F;
    file[symbolsAt + 16 + 5] = 1;
    Write64(file, symbolsAt + 16 + 8, 0x100001000);
    stringTable.Bytes.CopyTo(file.AsSpan(stringsAt));
    return file;
  }

  private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

  #endregion

  #region helpers

  /// <summary>Writes a fixture to a file, so the inspector can be pointed at a path as it always is.</summary>
  private static string Written(byte[] bytes, string name) {
    var path = Path.Combine(TestContext.CurrentContext.TestDirectory, name);
    File.WriteAllBytes(path, bytes);
    return path;
  }

  private static string Text(BinaryView view) => view.Describe();

  /// <summary>One real PE image this build produced, which every CI leg has.</summary>
  private static string OwnAssembly() {
    foreach (var path in Directory.EnumerateFiles(TestContext.CurrentContext.TestDirectory, "Hawkynt.ProcessManager.Core.dll"))
      return path;

    Assert.Fail("no assembly of this build was in the test output directory");
    return string.Empty;
  }

  /// <summary>A real shared object, where the machine running the tests has one.</summary>
  private static string? RealLibrary() {
    if (!OperatingSystem.IsLinux())
      return null;

    foreach (var directory in (ReadOnlySpan<string>)["/usr/lib", "/lib/x86_64-linux-gnu", "/usr/lib64", "/lib"]) {
      if (!Directory.Exists(directory))
        continue;

      foreach (var candidate in (ReadOnlySpan<string>)["libc.so.6", "libz.so.1", "libm.so.6"]) {
        var path = Path.Combine(directory, candidate);
        if (File.Exists(path))
          return path;
      }
    }

    return null;
  }

  #endregion

  #region ELF

  [Test]
  public void AnElfIsRecognisedAndItsHeaderRead() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    Assert.That(inspector.Format, Is.EqualTo(BinaryFormat.Elf));

    var headers = Text(inspector.View(BinaryPage.Headers));
    Assert.That(headers, Does.Contain("ELF64"));
    Assert.That(headers, Does.Contain("little endian"));
    Assert.That(headers, Does.Contain("AMD x86-64"));
    Assert.That(headers, Does.Contain("DYN"));
    Assert.That(headers, Does.Contain("0x1234"), "the entry point is what the header says it is");
  }

  [Test]
  public void TheProgramHeadersAreReadWithTheirFlagsInTheRightPlace() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var view = inspector.View(BinaryPage.Segments);

    Assert.That(view.Rows, Has.Count.EqualTo(3));
    Assert.That(view.Rows[0][0], Is.EqualTo("LOAD"));
    // r-x rather than any other permutation: p_flags is the field the two ELF classes disagree about
    // the position of, and reading it at the wrong offset reports the file offset as permissions.
    Assert.That(view.Rows[0][6], Is.EqualTo("r-x"));
    Assert.That(view.Rows[1][0], Is.EqualTo("INTERP"));
    Assert.That(view.Rows[2][0], Is.EqualTo("DYNAMIC"));
    Assert.That(view.Rows[2][6], Is.EqualTo("rw-"));
  }

  [Test]
  public void TheSectionTableIsReadWithItsNamesAndFlags() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var view = inspector.View(BinaryPage.Sections);

    Assert.That(view.Rows, Has.Count.EqualTo(7));
    Assert.That(view.Rows[1][1], Is.EqualTo(".dynstr"));
    Assert.That(view.Rows[2][1], Is.EqualTo(".dynsym"));
    Assert.That(view.Rows[2][2], Is.EqualTo("DYNSYM"));
    Assert.That(view.Rows[4][1], Is.EqualTo(".text"));
    // A, X and nothing else: the letters are readelf's, so an answer here can be held against it.
    Assert.That(view.Rows[4][7], Is.EqualTo("AX"));
  }

  [Test]
  public void TheDynamicSectionResolvesTheNamesItPointsAt() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var view = inspector.View(BinaryPage.Dynamic);

    Assert.That(view.Rows[0][1], Is.EqualTo("NEEDED"));
    Assert.That(view.Rows[0][2], Is.EqualTo(_Needed), "a NEEDED value is an offset into the string table, not a number to print");
    Assert.That(view.Rows[1][1], Is.EqualTo("SONAME"));
    Assert.That(view.Rows[1][2], Is.EqualTo(_Soname));
    Assert.That(view.Rows[^1][1], Is.EqualTo("NULL"));
  }

  [Test]
  public void TheDependenciesAreTheInterpreterTheSonameAndTheNeededLibraries() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var text = Text(inspector.View(BinaryPage.Dependencies));

    Assert.That(text, Does.Contain(_Interpreter));
    Assert.That(text, Does.Contain(_Soname));
    Assert.That(text, Does.Contain(_Needed));
  }

  [Test]
  public void AnUndefinedSymbolIsAnImportAndADefinedOneIsAnExport() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));

    var imports = inspector.View(BinaryPage.Imports);
    Assert.That(imports.Rows, Has.Count.EqualTo(1));
    Assert.That(imports.Rows[0][0], Is.EqualTo(_Import));
    Assert.That(imports.Rows[0][1], Is.EqualTo("func"));

    var exports = inspector.View(BinaryPage.Exports);
    Assert.That(exports.Rows, Has.Count.EqualTo(1));
    Assert.That(exports.Rows[0][^1], Is.EqualTo(_Export));
    Assert.That(exports.Rows[0][0], Is.EqualTo("0x1000"));
  }

  [Test]
  public void RelocationsAreCountedByTypeRatherThanListed() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var view = inspector.View(BinaryPage.Relocations);

    Assert.That(view.Rows, Has.Count.EqualTo(1));
    Assert.That(view.Rows[0][0], Is.EqualTo(".rela.dyn"));
    Assert.That(view.Rows[0][4], Is.EqualTo("1"));
    // The type is the bottom half of r_info and the symbol index the top. Reading the whole word as
    // a type would report relocation 4,294,967,302 rather than a glob-dat.
    Assert.That(view.Rows[0][5], Is.EqualTo("1 GLOB_DAT"));
  }

  [Test]
  public void AnElfHasNoSignatureAndTheSectionSaysSoRatherThanShowingNothing() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var view = inspector.View(BinaryPage.Signature);

    Assert.That(view.Rows, Is.Empty);
    // A page with no rows and no note is indistinguishable from a page nobody could read, which is
    // the confusion §72.3 exists to stop.
    Assert.That(view.Note, Does.Contain("Nothing signs an ELF"));
  }

  [Test]
  public void AnElfHasNoResourcesAndTheReasonIsTheFormatRatherThanThePermissions() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var view = inspector.View(BinaryPage.Resources);

    Assert.That(view.Rows, Is.Empty);
    Assert.That(view.Note, Does.Contain("no resource section"));
  }

  [Test]
  public void TheSecurityPageSaysWhatTheFileAsksForAndNotWhatItGot() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var view = inspector.View(BinaryPage.Security);

    Assert.That(Text(view), Does.Contain("ET_DYN"));
    // The fixture has no PT_GNU_STACK at all, which is a third answer and not either of the two.
    Assert.That(Text(view), Does.Contain("not stated"));
    Assert.That(view.Note, Does.Contain("not what it got"));
  }

  [Test]
  public void ARealSharedObjectIsReadTheSameWay() {
    if (RealLibrary() is not { } path) {
      Assert.Ignore("this machine has no shared object to read");
      return;
    }

    using var inspector = BinaryInspector.Open(path);
    Assert.That(inspector.Format, Is.EqualTo(BinaryFormat.Elf));
    Assert.That(inspector.View(BinaryPage.Segments).Rows, Is.Not.Empty);
    Assert.That(inspector.View(BinaryPage.Sections).Rows, Is.Not.Empty);
    Assert.That(inspector.View(BinaryPage.Exports).Rows, Is.Not.Empty, "a shared object exports something or nothing links against it");
    Assert.That(Text(inspector.View(BinaryPage.Summary)), Does.Contain("ELF"));
  }

  #endregion

  #region PE

  [Test]
  public void AManagedAssemblyIsAPortableExecutableAndSaysWhichRuntimeReadsIt() {
    using var inspector = BinaryInspector.Open(OwnAssembly());
    Assert.That(inspector.Format, Is.EqualTo(BinaryFormat.PortableExecutable));

    var summary = Text(inspector.View(BinaryPage.Summary));
    Assert.That(summary, Does.Contain("Portable Executable"));
    // By its CLI header and never by its extension: .dll names a managed assembly and a Windows
    // library both (PRD §5.3).
    Assert.That(summary, Does.Contain("managed"));
    Assert.That(summary, Does.Contain("dynamic-link library"));
  }

  [Test]
  public void ThePeOptionalHeaderIsReadAtTheOffsetsItsOwnMagicImplies() {
    using var inspector = BinaryInspector.Open(OwnAssembly());
    var headers = Text(inspector.View(BinaryPage.Headers));

    Assert.That(headers, Does.Contain("EXECUTABLE_IMAGE"));
    Assert.That(headers, Does.Contain("DLL"));
    // Every managed assembly this repository builds asks for these three, and reading the optional
    // header at the wrong shape's offsets loses all of them at once.
    Assert.That(headers, Does.Contain("DYNAMIC_BASE"));
    Assert.That(headers, Does.Contain("NX_COMPAT"));
    Assert.That(headers, Does.Contain("data directories"));
  }

  [Test]
  public void TheSixteenDataDirectoriesAreNamedAndTheCertificateOneIsNotAnAddress() {
    using var inspector = BinaryInspector.Open(OwnAssembly());
    var view = inspector.View(BinaryPage.Dynamic);

    Assert.That(view.Rows, Has.Count.EqualTo(16));
    Assert.That(view.Rows[14][1], Is.EqualTo("CLI header"));
    Assert.That(view.Rows[4][1], Is.EqualTo("certificate"));
    // "not written" and "no section covers it" are different findings, and this build's own
    // assemblies are unsigned so it is the first (PRD §72.3).
    Assert.That(view.Rows[4][4], Is.EqualTo("not written"));
    Assert.That(view.Note, Does.Contain("file offset"));
  }

  [Test]
  public void AManagedAssemblyImportsExactlyOneEntryPointFromTheRuntimeShim() {
    using var inspector = BinaryInspector.Open(OwnAssembly());
    var view = inspector.View(BinaryPage.Imports);

    Assert.That(view.Rows, Is.Not.Empty);
    Assert.That(view.Rows[0][0], Does.Contain("mscoree"));
    Assert.That(view.Rows[0][4], Does.Contain("_Cor"));
  }

  /// <summary>
  /// The debug directory, on whichever configuration the tests were built in.
  /// </summary>
  /// <remarks>
  /// A Release build of this repository sets <c>DebugType</c> to none, so its own assemblies carry a
  /// reproducibility record and no CodeView entry at all — which is why the unconditional half of
  /// this is the walk and the record, and the CodeView half is asserted against whichever assembly
  /// in the output directory still has one. Asserting a PDB path unconditionally passed in Debug
  /// and failed in Release, which is a test that was measuring the build rather than the reader.
  /// </remarks>
  [Test]
  public void TheDebugDirectoryIsWalkedAndACodeViewEntryNamesItsPdb() {
    using var inspector = BinaryInspector.Open(OwnAssembly());
    var view = inspector.View(BinaryPage.Debug);

    Assert.That(view.Rows, Is.Not.Empty);
    // Every deterministic build writes one, and it is the record that says the time stamp above is a
    // content hash rather than a date.
    Assert.That(Text(view), Does.Contain("reproducible"));

    foreach (var path in Directory.EnumerateFiles(TestContext.CurrentContext.TestDirectory, "Hawkynt.*.dll")) {
      using var other = BinaryInspector.Open(path);
      var text = Text(other.View(BinaryPage.Debug));
      if (!text.Contains("CodeView", StringComparison.Ordinal))
        continue;

      // The signature is a GUID and an age; a walk that landed anywhere else could not produce a
      // readable path four bytes further on.
      Assert.That(text, Does.Contain("pdb signature"));
      Assert.That(text, Does.Contain("pdb path"));
      Assert.That(text, Does.Contain(".pdb"));
      return;
    }
  }

  [Test]
  public void AnUnsignedPeSaysSoRatherThanShowingAnEmptyTable() {
    using var inspector = BinaryInspector.Open(OwnAssembly());
    var view = inspector.View(BinaryPage.Signature);

    Assert.That(view.Rows, Is.Empty);
    Assert.That(view.Note, Does.Contain("catalogue"), "unsigned and covered-by-a-catalogue are different findings");
  }

  [Test]
  public void PeSecurityIsWhatTheHeaderAsksForAndTheAbsentLoadConfigurationIsSaidAloud() {
    using var inspector = BinaryInspector.Open(OwnAssembly());
    var text = Text(inspector.View(BinaryPage.Security));

    Assert.That(text, Does.Contain("aslr"));
    Assert.That(text, Does.Contain("dep"));
    Assert.That(text, Does.Contain("control flow guard"));
    Assert.That(text, Does.Contain("shadow stack"));
  }

  [Test]
  public void APeHasNoSegmentTableAndTheSectionSaysWhyRatherThanBeingEmpty() {
    using var inspector = BinaryInspector.Open(OwnAssembly());
    var view = inspector.View(BinaryPage.Segments);

    Assert.That(view.Rows, Is.Empty);
    Assert.That(view.Note, Does.Contain("no segment table"));
  }

  #endregion

  /// <summary>
  /// A PE32+ image whose only section is a resource tree with a manifest in it.
  /// </summary>
  /// <remarks>
  /// Built rather than found, because there is no PE with a manifest on the machines this is
  /// developed on — the repository's own assemblies carry a version resource and nothing else, and
  /// the Wine libraries carry cursors. Without this, the manifest half of §53's PE box would be code
  /// nothing had ever run.
  /// </remarks>
  private static byte[] BuildPeWithManifest(string manifest) {
    const int peAt = 0x80;
    const int optionalSize = 112 + (16 * 8);
    const int sectionRva = 0x1000;
    const int sectionAt = 0x400;
    var xml = Encoding.UTF8.GetBytes(manifest);

    // The tree, laid out three levels deep with one entry at each: type 24, name 1, language 1033.
    // Every offset in it is from the start of the resource section, except the leaf's, which is the
    // one place the tree stops being self-relative and gives an address in the loaded image.
    var tree = new byte[112 + xml.Length];
    Entry(tree, 0, id: 24, child: 32, directory: true);
    Entry(tree, 32, id: 1, child: 64, directory: true);
    Entry(tree, 64, id: 1033, child: 96, directory: false);
    Write32(tree, 96, sectionRva + 112);
    Write32(tree, 100, (uint)xml.Length);
    xml.CopyTo(tree.AsSpan(112));

    var file = new byte[sectionAt + Align(tree.Length, 0x200)];
    file[0] = (byte)'M';
    file[1] = (byte)'Z';
    Write32(file, 0x3C, peAt);
    file[peAt] = (byte)'P';
    file[peAt + 1] = (byte)'E';

    var coff = peAt + 4;
    Write16(file, coff, 0x8664);
    Write16(file, coff + 2, 1);
    Write16(file, coff + 16, optionalSize);
    Write16(file, coff + 18, 0x2022);

    var optional = coff + 20;
    Write16(file, optional, 0x020B);
    Write32(file, optional + 56, 0x2000);
    Write32(file, optional + 60, sectionAt);
    Write16(file, optional + 68, 2);
    Write16(file, optional + 70, 0x0140);
    Write32(file, optional + 108, 16);
    // Data directory 2 is the resource tree.
    Write32(file, optional + 112 + (2 * 8), sectionRva);
    Write32(file, optional + 112 + (2 * 8) + 4, (uint)tree.Length);

    var section = optional + optionalSize;
    Encoding.ASCII.GetBytes(".rsrc").CopyTo(file.AsSpan(section));
    Write32(file, section + 8, (uint)tree.Length);
    Write32(file, section + 12, sectionRva);
    Write32(file, section + 16, (uint)Align(tree.Length, 0x200));
    Write32(file, section + 20, sectionAt);
    Write32(file, section + 36, 0x4000_0040);
    tree.CopyTo(file.AsSpan(sectionAt));
    return file;

    static void Entry(byte[] tree, int at, uint id, int child, bool directory) {
      // Characteristics, TimeDateStamp, two versions, then the two counts: no named entries and one
      // numbered one. The high bit of the second word of the entry says the low bits are another
      // directory rather than a leaf, which is the whole of how the tree ends.
      Write16(tree, at + 14, 1);
      Write32(tree, at + 16, id);
      Write32(tree, at + 20, directory ? 0x8000_0000u | (uint)child : (uint)child);
    }
  }

  [Test]
  public void APeManifestIsShownAsTheXmlItIs() {
    const string manifest =
      "<?xml version=\"1.0\"?><assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">"
      + "<trustInfo><security><requestedPrivileges><requestedExecutionLevel level=\"requireAdministrator\" />"
      + "</requestedPrivileges></security></trustInfo></assembly>";

    using var inspector = BinaryInspector.Open(Written(BuildPeWithManifest(manifest), "inspect-fixture-manifest.dll"));
    Assert.That(inspector.Format, Is.EqualTo(BinaryFormat.PortableExecutable));

    var view = inspector.View(BinaryPage.Resources);
    Assert.That(view.Rows, Has.Count.EqualTo(2), "the leaf, and the manifest's own text under it");
    Assert.That(view.Rows[0][0], Is.EqualTo("manifest"));
    Assert.That(view.Rows[0][2], Is.EqualTo("1,033"), "the language is a leaf of the tree and not the type");
    // Shown rather than parsed: an attribute search would be a reader that is right until somebody's
    // resource compiler writes the same thing differently.
    Assert.That(view.Rows[1][1], Does.Contain("requireAdministrator"));
  }

  #region Mach-O

  [Test]
  public void AMachOIsRecognisedFromItsOwnByteOrder() {
    using var inspector = BinaryInspector.Open(Written(BuildMachO(), "inspect-fixture.dylib"));
    Assert.That(inspector.Format, Is.EqualTo(BinaryFormat.MachO));

    var summary = Text(inspector.View(BinaryPage.Summary));
    Assert.That(summary, Does.Contain("Mach-O"));
    Assert.That(summary, Does.Contain("arm64"));
    Assert.That(summary, Does.Contain("MH_DYLIB"));
    Assert.That(summary, Does.Contain("little endian"));
  }

  [Test]
  public void TheLoadCommandsSegmentsAndSectionsAreWalkedInStep() {
    using var inspector = BinaryInspector.Open(Written(BuildMachO(), "inspect-fixture.dylib"));

    var commands = inspector.View(BinaryPage.Dynamic);
    Assert.That(commands.Rows, Has.Count.EqualTo(5));
    Assert.That(commands.Rows[0][0], Is.EqualTo("LC_SEGMENT_64"));
    Assert.That(commands.Rows[1][0], Is.EqualTo("LC_LOAD_DYLIB"));
    Assert.That(commands.Rows[2][0], Is.EqualTo("LC_UUID"));

    var segments = inspector.View(BinaryPage.Segments);
    Assert.That(segments.Rows, Has.Count.EqualTo(1));
    Assert.That(segments.Rows[0][0], Is.EqualTo("__TEXT"));
    Assert.That(segments.Rows[0][5], Is.EqualTo("r-x"));

    var sections = inspector.View(BinaryPage.Sections);
    Assert.That(sections.Rows, Has.Count.EqualTo(1));
    Assert.That(sections.Rows[0][0], Is.EqualTo("__TEXT"));
    Assert.That(sections.Rows[0][1], Is.EqualTo("__text"));
  }

  [Test]
  public void ADylibDependencyIsReadThroughTheOffsetItsCommandCarries() {
    using var inspector = BinaryInspector.Open(Written(BuildMachO(), "inspect-fixture.dylib"));
    var text = Text(inspector.View(BinaryPage.Dependencies));

    Assert.That(text, Does.Contain("libSystem.B.dylib"));
    Assert.That(text, Does.Contain("1.2.3"), "the current version is packed three components to a word");
  }

  [Test]
  public void MachOSymbolsAreSplitByWhetherTheyAreDefined() {
    using var inspector = BinaryInspector.Open(Written(BuildMachO(), "inspect-fixture.dylib"));

    var imports = inspector.View(BinaryPage.Imports);
    Assert.That(imports.Rows, Has.Count.EqualTo(1));
    Assert.That(imports.Rows[0][0], Is.EqualTo("_malloc"));

    var exports = inspector.View(BinaryPage.Exports);
    Assert.That(exports.Rows, Has.Count.EqualTo(1));
    Assert.That(exports.Rows[0][^1], Is.EqualTo("_inspect_entry"));
  }

  [Test]
  public void TheCodeSignatureRecordAndTheBuildIdentityAreRead() {
    using var inspector = BinaryInspector.Open(Written(BuildMachO(), "inspect-fixture.dylib"));

    Assert.That(Text(inspector.View(BinaryPage.Debug)), Does.Contain("01020304-0506-0708"));
    var signature = Text(inspector.View(BinaryPage.Signature));
    Assert.That(signature, Does.Contain("code signature at"));
    // The fixture's blob is not there, and saying so beats inventing a verdict about bytes nobody
    // read (PRD §70, §72.3).
    Assert.That(signature, Does.Contain("nothing further was read"));
  }

  /// <summary>
  /// A universal binary: the same image twice, behind a table saying where each begins.
  /// </summary>
  /// <remarks>
  /// The fat header is big-endian whatever the images inside it are, and every offset each image
  /// carries is relative to its own start rather than to the file. A reader that assumed nought
  /// would describe the fat header as a Mach-O and get every field of it wrong.
  /// </remarks>
  private static byte[] BuildUniversal(byte[] image) {
    const int width = 20;
    var start = Align(8 + (2 * width), 0x1000);
    var file = new byte[start + (2 * 0x1000)];
    WriteBig32(file, 0, 0xCAFEBABE);
    WriteBig32(file, 4, 2);
    for (var i = 0; i < 2; ++i) {
      var at = 8 + (i * width);
      WriteBig32(file, at, i == 0 ? 0x0100_0007u : 0x0100_000Cu);
      WriteBig32(file, at + 8, (uint)(start + (i * 0x1000)));
      WriteBig32(file, at + 12, (uint)image.Length);
      image.CopyTo(file.AsSpan(start + (i * 0x1000)));
    }

    return file;
  }

  private static void WriteBig32(byte[] file, int at, uint value)
    => BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(at), value);

  [Test]
  public void AUniversalBinaryIsItsImagesRatherThanTheTableAtTheFront() {
    var path = Written(BuildUniversal(BuildMachO()), "inspect-fixture-fat.dylib");
    using var first = BinaryInspector.Open(path);

    Assert.That(first.Format, Is.EqualTo(BinaryFormat.UniversalBinary));
    Assert.That(first.Slices, Has.Count.EqualTo(2));
    Assert.That(first.Slices[0].Architecture, Is.EqualTo("x86_64"));
    Assert.That(first.Slices[1].Architecture, Is.EqualTo("arm64"));

    // Every other page describes one of the images, at its own offset — the segments below are the
    // slice's, and a reader that had not moved to it would have found none at all.
    var summary = Text(first.View(BinaryPage.Summary));
    Assert.That(summary, Does.Contain("Mach-O"));
    Assert.That(first.View(BinaryPage.Summary).Note, Does.Contain("universal binary"));
    Assert.That(first.View(BinaryPage.Segments).Rows, Has.Count.EqualTo(1));

    using var second = BinaryInspector.Open(path, slice: 1);
    Assert.That(second.View(BinaryPage.Dependencies).Rows, Is.Not.Empty);
  }

  [Test]
  public void MachOSecurityReadsTheHeaderFlags() {
    using var inspector = BinaryInspector.Open(Written(BuildMachO(), "inspect-fixture.dylib"));
    var text = Text(inspector.View(BinaryPage.Security));

    Assert.That(text, Does.Contain("MH_PIE"));
    Assert.That(text, Does.Contain("two level namespace"));
  }

  #endregion

  #region what is not an image

  [Test]
  public void AFileThatIsNoneOfTheThreeIsDescribedRatherThanRefused() {
    var path = Written("just some bytes that are not a program at all"u8.ToArray(), "inspect-fixture.dat");
    using var inspector = BinaryInspector.Open(path);

    Assert.That(inspector.Format, Is.EqualTo(BinaryFormat.Unknown));
    Assert.That(inspector.Reason, Is.Null, "readable and not an image is not a permission problem");
    Assert.That(Text(inspector.View(BinaryPage.Summary)), Does.Contain("not an executable image"));
  }

  [Test]
  public void AScriptIsRecognisedBecauseOnUnixAShebangIsAWayToStartAProgram() {
    var path = Written("#!/bin/sh\necho hello\n"u8.ToArray(), "inspect-fixture.sh");
    using var inspector = BinaryInspector.Open(path);

    Assert.That(inspector.Format, Is.EqualTo(BinaryFormat.Script));
    Assert.That(Text(inspector.View(BinaryPage.Summary)), Does.Contain("shebang"));
  }

  [Test]
  public void AFileThatIsNotThereIsAReasonRatherThanAnEmptyReport() {
    using var inspector = BinaryInspector.Open("/no/such/binary/anywhere");

    Assert.That(inspector.Format, Is.EqualTo(BinaryFormat.Unreadable));
    Assert.That(inspector.Reason, Is.Not.Null.And.Not.Empty);
    // Every page, and not only the first: a caller that dispatched on a page name must not find one
    // of the sixteen silently empty (PRD §72.3).
    foreach (var page in BinaryInspector.Pages) {
      var view = inspector.View(page);
      Assert.That(view.Rows, Is.Empty);
      Assert.That(view.Note, Is.Not.Null.And.Not.Empty, $"the {page} page says nothing about why it is empty");
    }
  }

  [Test]
  public void EveryPageNameCanBeTypedAndTheVocabularyListsThemAll() {
    foreach (var page in BinaryInspector.Pages) {
      var name = BinaryInspector.Title(page).ToLowerInvariant().Split(' ')[0];
      Assert.That(BinaryInspector.TryParsePage(name, out var parsed), Is.True, $"'{name}' is not a page name");
      Assert.That(parsed, Is.EqualTo(page));
      Assert.That(BinaryInspector.PageVocabulary, Does.Contain(name), $"'{name}' is missing from the vocabulary");
    }

    Assert.That(BinaryInspector.TryParsePage("nonsense", out _), Is.False);
  }

  #endregion

  #region strings

  private static TextScanResult Scan(byte[] bytes, TextScanOptions options)
    => BinaryStrings.Scan((offset, buffer) => {
      if (offset >= bytes.Length)
        return 0;

      var got = (int)Math.Min(buffer.Length, bytes.Length - offset);
      bytes.AsSpan((int)offset, got).CopyTo(buffer);
      return got;
    }, bytes.Length, in options);

  [Test]
  public void APrintableRunIsFoundWithItsOffset() {
    var bytes = new byte[] { 0, 0, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0, 1, 2 };
    var result = Scan(bytes, TextScanOptions.Default);

    Assert.That(result.Runs, Has.Count.EqualTo(1));
    Assert.That(result.Runs[0].Text, Is.EqualTo("hello"));
    Assert.That(result.Runs[0].Offset, Is.EqualTo(2), "the offset is what makes a hit checkable against a hex dump");
    Assert.That(result.Runs[0].Encoding, Is.EqualTo(TextEncodingKind.Ascii));
  }

  [Test]
  public void TheMinimumLengthIsWhatDecidesWhetherARunCounts() {
    var bytes = Encoding.ASCII.GetBytes("\0abc\0abcdef\0");

    Assert.That(Scan(bytes, TextScanOptions.Default).Runs, Has.Count.EqualTo(1), "abc is three characters and the default is four");
    Assert.That(Scan(bytes, TextScanOptions.Default with { MinimumLength = 3 }).Runs, Has.Count.EqualTo(2));
    Assert.That(Scan(bytes, TextScanOptions.Default with { MinimumLength = 7 }).Runs, Is.Empty);
  }

  [Test]
  public void ARunAtTheVeryEndOfTheFileIsStillARun() {
    var bytes = Encoding.ASCII.GetBytes("\0trailing");
    Assert.That(Scan(bytes, TextScanOptions.Default).Runs[0].Text, Is.EqualTo("trailing"));
  }

  [Test]
  public void AMultiByteSequenceMakesTheRunUtf8AndItIsNotAlsoReportedAsAscii() {
    var bytes = Encoding.UTF8.GetBytes("\0Grüße dich\0");
    var result = Scan(bytes, TextScanOptions.Default);

    Assert.That(result.Runs, Has.Count.EqualTo(1), "one run, not one per encoding: the same bytes must not be counted twice");
    Assert.That(result.Runs[0].Encoding, Is.EqualTo(TextEncodingKind.Utf8));
    Assert.That(result.Runs[0].Text, Is.EqualTo("Grüße dich"));
  }

  [Test]
  public void AnOverlongSequenceIsNotDecodedBecauseNoDecoderOnTheMachineAgreesItExists() {
    // C0 AF is an overlong "/" — the classic way to smuggle one past a comparison.
    var bytes = new byte[] { 0, (byte)'a', (byte)'b', 0xC0, 0xAF, (byte)'c', (byte)'d', 0 };
    var result = Scan(bytes, TextScanOptions.Default);

    Assert.That(result.Runs, Is.Empty, "the run is broken at the invalid sequence rather than decoded through it");
  }

  [Test]
  public void WideTextIsFoundInEitherByteOrderAndNamedAsSuch() {
    var little = new List<byte> { 0, 0 };
    var big = new List<byte>();
    foreach (var c in "widestring") {
      little.Add((byte)c);
      little.Add(0);
      big.Add(0);
      big.Add((byte)c);
    }

    var littleResult = Scan([.. little], TextScanOptions.Default);
    Assert.That(littleResult.Runs.Any(r => r.Encoding == TextEncodingKind.Utf16LittleEndian && r.Text == "widestring"), Is.True);

    var bigResult = Scan([.. big], TextScanOptions.Default);
    Assert.That(bigResult.Runs.Any(r => r.Encoding == TextEncodingKind.Utf16BigEndian && r.Text == "widestring"), Is.True);
  }

  [Test]
  public void CompiledCodeIsNotReportedAsARunOfIdeographs() {
    // Sixty bytes of plausible x86-64. Under a wide rule that accepted the whole plane, nearly every
    // pair of these is a graphic CJK character and this file would be full of runs that are not text
    // at all — which is why the wide pass stops at Latin-1.
    var code = new byte[60];
    for (var i = 0; i < code.Length; ++i)
      code[i] = (byte)(0x48 + ((i * 37) % 0x60));

    var result = Scan(code, TextScanOptions.Default);
    foreach (var run in result.Runs)
      Assert.That(run.Encoding, Is.Not.EqualTo(TextEncodingKind.Utf16LittleEndian).And.Not.EqualTo(TextEncodingKind.Utf16BigEndian));
  }

  [Test]
  public void TheFilterUsesTheSameGrammarTheResourceSearchDoes() {
    var bytes = Encoding.ASCII.GetBytes("\0libcap.so.2\0libc.so.6\0hello world\0");

    Assert.That(Scan(bytes, TextScanOptions.Default with { Pattern = "libc" }).Runs, Has.Count.EqualTo(2));
    Assert.That(Scan(bytes, TextScanOptions.Default with { Pattern = "\"libc.so.6\"" }).Runs, Has.Count.EqualTo(1));
    Assert.That(Scan(bytes, TextScanOptions.Default with { Pattern = "*.so.*" }).Runs, Has.Count.EqualTo(2));
    Assert.That(Scan(bytes, TextScanOptions.Default with { Pattern = "/so\\.[0-9]/" }).Runs, Has.Count.EqualTo(2));
  }

  [Test]
  public void ARegionRestrictsWhichBytesAreRead() {
    var bytes = Encoding.ASCII.GetBytes("firstpart\0secondpart\0");
    var result = Scan(bytes, TextScanOptions.Default with { From = 10, Length = 10 });

    Assert.That(result.Runs, Has.Count.EqualTo(1));
    Assert.That(result.Runs[0].Text, Is.EqualTo("secondpart"));
    Assert.That(result.BytesScanned, Is.EqualTo(10));
  }

  [Test]
  public void TheDefaultOptionsAreAskedForByNameBecauseTheStructsZeroFindsNothing() {
    var bytes = Encoding.ASCII.GetBytes("\0findable text\0");

    // default(TextScanOptions) has no encodings selected and a cap of nought runs. That is a
    // confident zero of exactly the kind §72.3 forbids, and the named default is the guard.
    Assert.That(Scan(bytes, default).Runs, Is.Empty);
    Assert.That(Scan(bytes, TextScanOptions.Default).Runs, Is.Not.Empty);
  }

  [Test]
  public void AScanThatHitItsCapSaysSoRatherThanStoppingQuietly() {
    var bytes = Encoding.ASCII.GetBytes(string.Join('\0', Enumerable.Repeat("wordy", 50)));
    var result = Scan(bytes, TextScanOptions.Default with { MaximumRuns = 5 });

    Assert.That(result.Runs, Has.Count.EqualTo(5));
    Assert.That(result.Truncated, Is.True);
  }

  [Test]
  public void TheStringsPageOfARealBinaryFindsTheNamesItsHeaderAlreadyGaveUs() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var view = inspector.Strings(TextScanOptions.Default);

    Assert.That(Text(view), Does.Contain(_Interpreter));
    Assert.That(Text(view), Does.Contain(_Soname));
    Assert.That(view.Note, Does.Contain("runs of at least 4"));
  }

  [Test]
  public void ScanningOnlyTheCodeIsAScanOfFewerBytes() {
    using var inspector = BinaryInspector.Open(Written(BuildElf(), "inspect-fixture.so"));
    var regions = inspector.ExecutableRegions;

    Assert.That(regions, Is.Not.Empty, "§35's executable-image-only filter needs somewhere to point");
    long bytes = 0;
    foreach (var region in regions)
      bytes += region.Length;

    Assert.That(bytes, Is.LessThan(inspector.ScanCost));
  }

  #endregion

  #region the front-ends

  private static Hawkynt.ProcessManager.App.CommandLineOptions Parse(params string[] args)
    => Hawkynt.ProcessManager.App.CommandLineOptions.Parse(args, null);

  [Test]
  public void TheCommandLineTakesAFileAndOptionallyAPage() {
    var summary = Parse("--inspect", "/usr/bin/env");
    Assert.That(summary.Error, Is.Null);
    Assert.That(summary.InspectPath, Is.EqualTo("/usr/bin/env"));
    Assert.That(summary.InspectPage, Is.EqualTo(BinaryPage.Summary));

    var symbols = Parse("--inspect", "/usr/bin/env", "symbols");
    Assert.That(symbols.InspectPage, Is.EqualTo(BinaryPage.Symbols));

    // A switch after the file is a switch and not a page name, or --inspect X --json would look for
    // a page called "--json".
    var json = Parse("--inspect", "/usr/bin/env", "--json");
    Assert.That(json.Error, Is.Null);
    Assert.That(json.InspectPage, Is.EqualTo(BinaryPage.Summary));

    Assert.That(Parse("--inspect").Error, Is.Not.Null);
    Assert.That(Parse("--inspect", "/usr/bin/env", "nonsense").Error, Is.Not.Null);
  }

  [Test]
  public void TheStringsSwitchesAreReadAndTheHelpNamesThem() {
    var options = Parse("--inspect", "/usr/bin/env", "strings", "--min-length", "8", "--match", "/lib/", "--code-only");

    Assert.That(options.MinimumTextLength, Is.EqualTo(8));
    Assert.That(options.TextPattern, Is.EqualTo("/lib/"));
    Assert.That(options.TextCodeOnly, Is.True);
    Assert.That(Parse("--min-length", "0").Error, Is.Not.Null);
    Assert.That(Hawkynt.ProcessManager.App.CommandLineOptions.HelpText, Does.Contain("--inspect"));
    Assert.That(Hawkynt.ProcessManager.App.CommandLineOptions.HelpText, Does.Contain("--min-length"));
  }

  [Test]
  public void TheWindowOpensOnTheSummaryAndDoesNotScanUntilItIsAsked() {
    var window = new Ui.Desktop.BinaryInspectorWindow(Written(BuildElf(), "inspect-fixture.so"));

    Assert.That(window.Page, Is.EqualTo(BinaryPage.Summary));
    Assert.That(window.Description, Does.Contain("ELF"));

    // §35's requirement: the warning arrives before the scan, not after. Selecting the page must
    // cost nothing at all.
    window.ShowPage(BinaryPage.Strings);
    Assert.That(window.Description, Does.Contain("Nothing has been scanned"));
    // And the cost is named in it, so the number is on screen before anybody presses anything.
    Assert.That(window.Description, Does.Contain("kB of it"));
  }

  /// <summary>
  /// The window can be measured before it has read anything.
  /// </summary>
  /// <remarks>
  /// It lays itself out once in its own constructor, before the first page exists, and the layout
  /// asks the page how many rows it has. A <c>default</c> view has no rows list at all rather than
  /// an empty one — so this took the capture leg down with a null reference while every other test
  /// here passed, because none of them measured a window that had not been shown a page yet.
  /// </remarks>
  [Test]
  public void TheWindowCanBeLaidOutBeforeAndAfterEveryPage() {
    var window = new Ui.Desktop.BinaryInspectorWindow(Written(BuildElf(), "inspect-fixture.so"));
    Assert.That(window.ApplyLayout, Throws.Nothing);

    foreach (var page in BinaryInspector.Pages) {
      window.ShowPage(page);
      Assert.That(window.ApplyLayout, Throws.Nothing, $"the {page} page cannot be laid out");
      Assert.That(window.Description, Is.Not.Null.And.Not.Empty);
    }
  }

  [Test]
  public void TheWindowRebuildsItsColumnsForEveryPage() {
    var window = new Ui.Desktop.BinaryInspectorWindow(Written(BuildElf(), "inspect-fixture.so"));

    window.ShowPage(BinaryPage.Sections);
    var sections = window.Description;
    window.ShowPage(BinaryPage.Symbols);

    // A grid that kept the previous page's headers would label a symbol table with a section
    // table's words, which is a wrong reading that looks entirely plausible.
    Assert.That(sections, Does.Contain("Align"));
    Assert.That(window.Description, Does.Not.Contain("Align"));
    Assert.That(window.Description, Does.Contain("Binding"));
  }

  #endregion

  #region read-only

  /// <summary>
  /// §53's last line, enforced rather than asserted.
  /// </summary>
  /// <remarks>
  /// A viewer and not a patcher. Nothing in the inspector's public surface takes a byte to write, and
  /// nothing opens a file for anything but reading — so the day somebody adds a "patch" in good
  /// faith, this fails rather than the feature quietly appearing (PRD §53, §4).
  /// </remarks>
  [Test]
  public void TheInspectorsSurfaceOffersNoWayToChangeAFile() {
    foreach (var type in (ReadOnlySpan<Type>)[typeof(BinaryInspector), typeof(BinaryStrings), typeof(ImageBytes), typeof(BinaryView)]) {
      foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
        var name = member.Name;
        Assert.That(
          name.StartsWith("Write", StringComparison.Ordinal)
          || name.StartsWith("Patch", StringComparison.Ordinal)
          || name.StartsWith("Set", StringComparison.Ordinal) && member is MethodInfo
          || name.StartsWith("Edit", StringComparison.Ordinal),
          Is.False,
          $"{type.Name}.{name} looks like a way to change a file; §53 says this is a viewer"
        );
      }
    }
  }

  [Test]
  public void TheFileIsOpenedForReadingAndSharedWithWhoeverIsReplacingIt() {
    var path = Written(BuildElf(), "inspect-fixture-shared.so");
    using var inspector = BinaryInspector.Open(path);

    // A package manager replacing an image while somebody looks at it is the ordinary case on the
    // machines worth inspecting, and a viewer that took an exclusive handle would fail on exactly
    // those.
    Assert.That(() => {
      using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
    }, Throws.Nothing);

    Assert.That(inspector.View(BinaryPage.Summary).Rows, Is.Not.Empty);
  }

  #endregion

}
