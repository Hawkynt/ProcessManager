using System.Buffers.Binary;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Which runtime reads a mapped file (PRD §31).
/// </summary>
/// <remarks>
/// The rows this exists for are the ones the modules view used to call <c>data</c>: a .NET process
/// maps every assembly it loads and not one of them is an ELF, so every one of them landed under
/// the same word as a font. <see cref="ThisTestAssemblyIsAManagedAssembly"/> is the assertion
/// nothing in this file wrote — the build produced the bytes it reads.
/// </remarks>
[TestFixture]
public sealed class ImageFormatTests {

  /// <summary>
  /// A portable executable with a DOS stub, a PE signature, an optional header and a data directory.
  /// </summary>
  /// <param name="plus">
  /// The 64-bit optional header, whose data directory starts sixteen bytes further in because
  /// <c>ImageBase</c> is four bytes wider and two fields vanish. Getting this wrong reads the middle
  /// of the size fields, where a non-zero word is common.
  /// </param>
  /// <param name="cliBytes">
  /// The size of the CLI header the file claims. Nought is a Windows binary; anything else is a
  /// managed assembly.
  /// </param>
  private static byte[] Pe(bool plus, uint cliBytes, int peAt = 0x80) {
    var image = new byte[0x400];
    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), (uint)peAt);

    image[peAt] = (byte)'P';
    image[peAt + 1] = (byte)'E';

    var optional = peAt + 0x18;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), plus ? (ushort)0x20B : (ushort)0x10B);

    // Data directory entry 14, counting from nought, which is the CLI header's.
    var entry = optional + (plus ? 0x70 : 0x60) + (14 * 8);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry), cliBytes == 0 ? 0u : 0x2008u);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 4), cliBytes);
    return image;
  }

  private static ModuleRuntime Identify(byte[] image)
    => ImageFormat.Identify(Over(image), image.AsSpan(0, Math.Min(64, image.Length)));

  private static ElfImage.ElfRead Over(byte[] image) => (offset, buffer) => {
    if (offset < 0 || offset >= image.Length)
      return 0;

    var available = (int)Math.Min(buffer.Length, image.Length - offset);
    image.AsSpan((int)offset, available).CopyTo(buffer);
    return available;
  };

  [Test]
  public void AnElfIsNativeCode() {
    var elf = new byte[64];
    elf[0] = 0x7F;
    elf[1] = (byte)'E';
    elf[2] = (byte)'L';
    elf[3] = (byte)'F';

    Assert.That(Identify(elf), Is.EqualTo(ModuleRuntime.Native));
  }

  [Test]
  public void APortableExecutableWithACliHeaderIsAManagedAssembly() {
    Assert.That(Identify(Pe(plus: true, cliBytes: 0x48)), Is.EqualTo(ModuleRuntime.Managed));
    Assert.That(Identify(Pe(plus: false, cliBytes: 0x48)), Is.EqualTo(ModuleRuntime.Managed));
  }

  [Test]
  public void APortableExecutableWithoutOneIsAWindowsBinary() {
    Assert.That(Identify(Pe(plus: true, cliBytes: 0)), Is.EqualTo(ModuleRuntime.WindowsNative));
    Assert.That(Identify(Pe(plus: false, cliBytes: 0)), Is.EqualTo(ModuleRuntime.WindowsNative));
  }

  /// <summary>
  /// The two optional-header shapes put the data directory at different offsets. Reading a 64-bit
  /// image at the 32-bit one lands in the middle of the size fields, and reports half a Windows
  /// installation as managed.
  /// </summary>
  [Test]
  public void TheDataDirectoryIsFoundAtTheOffsetTheOptionalHeaderShapeGives() {
    var sixtyFour = Pe(plus: true, cliBytes: 0);
    // Fill the whole optional header with a plausible non-zero pattern, then clear only the entry
    // that actually is the CLI header's. A reader looking in the wrong place now finds a number.
    for (var i = 0x18 + 0x80; i < 0x18 + 0x80 + 0x80; ++i)
      sixtyFour[i] = 0x11;

    var entry = 0x80 + 0x18 + 0x70 + (14 * 8);
    sixtyFour.AsSpan(entry, 8).Clear();

    Assert.That(Identify(sixtyFour), Is.EqualTo(ModuleRuntime.WindowsNative));
  }

  /// <summary>
  /// A CLI directory with an address and no bytes describes nothing, and no runtime can load it.
  /// </summary>
  [Test]
  public void ACliHeaderOfNoBytesIsNotAnAssembly() {
    var image = Pe(plus: true, cliBytes: 0x48);
    var entry = 0x80 + 0x18 + 0x70 + (14 * 8);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 4), 0);

    Assert.That(Identify(image), Is.EqualTo(ModuleRuntime.WindowsNative));
  }

  /// <summary>
  /// <c>MZ</c> is the DOS stub and says nothing on its own: a file whose header offset points at
  /// nothing is not a PE, and must come back as data rather than as a Windows binary.
  /// </summary>
  [Test]
  public void AnMzWithNoPeHeaderBehindItIsNotCode() {
    var stub = new byte[0x400];
    stub[0] = (byte)'M';
    stub[1] = (byte)'Z';
    Assert.That(Identify(stub), Is.EqualTo(ModuleRuntime.NotCode), "an offset of nought names no header");

    var wild = Pe(plus: true, cliBytes: 0x48);
    BinaryPrimitives.WriteUInt32LittleEndian(wild.AsSpan(0x3C), 0x4000_0000);
    Assert.That(Identify(wild), Is.EqualTo(ModuleRuntime.NotCode), "and a gigabyte in names none either");

    var misplaced = Pe(plus: true, cliBytes: 0x48);
    misplaced[0x80] = 0;
    Assert.That(Identify(misplaced), Is.EqualTo(ModuleRuntime.NotCode), "nor does an offset pointing at the wrong bytes");
  }

  [Test]
  public void AZipContainerIsAnArchive() {
    var jar = new byte[64];
    jar[0] = (byte)'P';
    jar[1] = (byte)'K';
    jar[2] = 3;
    jar[3] = 4;

    Assert.That(Identify(jar), Is.EqualTo(ModuleRuntime.Archive));
  }

  /// <summary>
  /// A locale archive, a font, an icon cache. "Not code" is an answer and renders as one — the dash
  /// beside it means nobody read the file (PRD §72.3).
  /// </summary>
  [Test]
  public void SomethingThatIsNoneOfThemIsNotCodeAndSaysSo() {
    Assert.Multiple(() => {
      Assert.That(Identify(new byte[64]), Is.EqualTo(ModuleRuntime.NotCode));
      Assert.That(Humanize.ImageRuntime(ModuleRuntime.NotCode), Is.EqualTo("not code"));
      Assert.That(Humanize.ImageRuntime(ModuleRuntime.Unknown), Is.EqualTo("—"));
      Assert.That(Humanize.ImageRuntime(ModuleRuntime.NotCode), Is.Not.EqualTo(Humanize.ImageRuntime(ModuleRuntime.Unknown)));
    });
  }

  /// <summary>
  /// The reader reaching the classifier: <see cref="ElfImage.TryDescribe"/> stops at "not an ELF"
  /// and this is where it goes on.
  /// </summary>
  [Test]
  public void TheElfReaderHandsANonElfOverRatherThanCallingItData() {
    var assembly = Pe(plus: true, cliBytes: 0x48);
    Assert.That(ElfImage.TryDescribe(Over(assembly), out var description), Is.True);

    Assert.Multiple(() => {
      // Both, and they are different statements: the format is not an image this kernel loads, and
      // the runtime that reads it is the one that generates code from it.
      Assert.That(description.Type, Is.EqualTo(ModuleType.Data));
      Assert.That(description.Runtime, Is.EqualTo(ModuleRuntime.Managed));
      Assert.That(description.EntryPoint.HasValue, Is.False);
    });
  }

  [Test]
  public void AnUnreadImageNamesNoRuntime() {
    Assert.That(ElfImage.Unread.Runtime, Is.EqualTo(ModuleRuntime.Unknown));
  }

  /// <summary>
  /// The one assertion this file did not write the bytes for: the test assembly on disk is a real
  /// managed PE, produced by the build.
  /// </summary>
  [Test]
  public void ThisTestAssemblyIsAManagedAssembly() {
    var path = typeof(ImageFormatTests).Assembly.Location;
    if (path is not { Length: > 0 } || !File.Exists(path)) {
      Assert.Ignore("A single-file or trimmed build has no assembly on disk to read.");
      return;
    }

    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
    Assert.That(
      ElfImage.TryDescribe((offset, buffer) => RandomAccess.Read(handle, buffer, offset), out var description),
      Is.True
    );

    Assert.That(description.Runtime, Is.EqualTo(ModuleRuntime.Managed));
  }

}
