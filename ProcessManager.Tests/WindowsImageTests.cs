using System.Buffers.Binary;
using System.Text;
using Hawkynt.ProcessManager.Platform.Windows;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The PE reader (PRD §14), run against real PE images on whatever OS the tests are running on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this one is not the usual "synthesised from the same struct" test.</strong> The
/// process-information replay of <see cref="WindowsProcessInformationReplayTests"/> builds its buffer
/// from the very struct definition the parser reads, so a layout misunderstanding is invisible to it.
/// Here there is no such circle: every assembly this repository builds <em>is</em> a PE image with a
/// version resource, written by a compiler nobody here controls, and it is present in the test
/// output directory on the Linux and macOS legs exactly as it is on the Windows one. The parser is
/// therefore held against files it did not produce, on every leg.
/// </para>
/// <para>
/// The strongest check in here is <see cref="TheTwoFileVersionsInOneImageAgree"/>. A PE writes its
/// file version twice — once as the four numbers of <c>VS_FIXEDFILEINFO</c> and once as a string in
/// a string table, reached by a different path through the same resource tree — and a walk that
/// lands in the wrong place cannot make the two agree. The header fields were separately held
/// against <c>objdump -x</c>, which is an implementation of this format that has nothing to do with
/// this one.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WindowsImageTests {

  /// <summary>Every managed assembly in the test output, which are all real PE images.</summary>
  private static IEnumerable<string> Assemblies() {
    var directory = TestContext.CurrentContext.TestDirectory;
    foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
      // Only the ones this repository builds: the runtime's own assemblies are PE images too, but
      // whether they are beside the tests depends on how the run was published.
      if (Path.GetFileName(path).StartsWith("Hawkynt.", StringComparison.Ordinal))
        yield return path;
  }

  [Test]
  public void EveryAssemblyThisBuildProducedIsReadAsThePeImageItIs() {
    var read = 0;
    foreach (var path in Assemblies()) {
      Assert.That(
        PortableExecutable.TryRead(File.ReadAllBytes(path), out var facts),
        Is.True,
        $"{Path.GetFileName(path)} is a PE image and did not parse"
      );

      // Roslyn writes the company, the description and the product out of the assembly attributes
      // this repository sets, so these are not "some string was found" — they are the strings the
      // build was told to write.
      Assert.That(facts.Company, Is.EqualTo("Hawkynt"), Path.GetFileName(path));
      Assert.That(facts.Description, Is.Not.Null.And.Not.Empty, Path.GetFileName(path));
      Assert.That(facts.FileVersion, Is.Not.Null.And.Not.Empty, Path.GetFileName(path));
      Assert.That(facts.ProductVersion, Is.Not.Null.And.Not.Empty, Path.GetFileName(path));
      ++read;
    }

    // A test that silently read nothing would pass for ever. There are more than a dozen of these.
    Assert.That(read, Is.GreaterThan(5), "no assemblies were found to read");
  }

  /// <summary>
  /// The same version, written twice in two encodings and reached by two different paths.
  /// </summary>
  /// <remarks>
  /// This is what makes the walk checkable without a Windows machine. The fixed part is a binary
  /// quadruple at the front of the block; the string is in a string table three levels down. A parser
  /// whose alignment, whose value-length units or whose child bounds are wrong lands on other bytes
  /// for one of them, and the two stop agreeing.
  /// <para>
  /// Compared on the leading components rather than character for character: a publisher may write
  /// "1.2.3.4+abcdef" or "1.2.3 beta" in the string while the fixed part still says 1.2.3.4, and both
  /// halves are true. What cannot happen is the string not starting with the numbers.
  /// </para>
  /// </remarks>
  [Test]
  public void TheTwoFileVersionsInOneImageAgree() {
    var compared = 0;
    foreach (var path in Assemblies()) {
      if (!PortableExecutable.TryRead(File.ReadAllBytes(path), out var facts))
        continue;
      if (facts.FileVersion is not { Length: > 0 } text || facts.FixedFileVersion is not { Length: > 0 } numbers)
        continue;

      var expected = numbers.Split('.');
      var actual = text.Split('.', '+', ' ', '-');
      Assert.That(actual.Length, Is.GreaterThanOrEqualTo(3), $"{Path.GetFileName(path)}: '{text}'");
      for (var i = 0; i < 3; ++i)
        Assert.That(
          actual[i],
          Is.EqualTo(expected[i]),
          $"{Path.GetFileName(path)}: the string says '{text}' and the fixed part says '{numbers}'"
        );

      ++compared;
    }

    Assert.That(compared, Is.GreaterThan(5), "no image carried both forms of its version");
  }

  /// <summary>
  /// The header fields, against what <c>objdump -x</c> says about the same file.
  /// </summary>
  /// <remarks>
  /// A managed assembly targeting AnyCPU is written as a PE32 image whose COFF machine is I386 and
  /// whose subsystem is the console one, whatever the machine that built it — which is why these are
  /// constants here rather than a reading of the running platform. Held against binutils rather than
  /// against this parser's own idea of the format.
  /// </remarks>
  [Test]
  public void TheMachineAndSubsystemAreTheOnesBinutilsReports() {
    var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Hawkynt.ProcessManager.Core.dll");
    Assert.That(File.Exists(path), Is.True, path);
    Assert.That(PortableExecutable.TryRead(File.ReadAllBytes(path), out var facts), Is.True);

    Assert.That(facts.Machine, Is.EqualTo((ushort)0x014C), "IMAGE_FILE_MACHINE_I386");
    Assert.That(facts.Subsystem, Is.EqualTo((ushort)3), "IMAGE_SUBSYSTEM_WINDOWS_CUI");
    Assert.That(facts.Is64Bit, Is.False, "PE32, not PE32+");
  }

  #region what a file that is not one of these looks like

  [Test]
  public void SomethingThatIsNotAPeImageIsRefusedRatherThanRead() {
    Assert.That(PortableExecutable.TryRead([], out _), Is.False, "empty");
    Assert.That(PortableExecutable.TryRead(new byte[4096], out _), Is.False, "zeroes");
    Assert.That(PortableExecutable.TryRead(Encoding.ASCII.GetBytes("#!/bin/sh\nexit 0\n"), out _), Is.False, "a script");

    // An ELF, which is what every executable on the machine running this test actually is.
    var elf = new byte[4096];
    elf[0] = 0x7F;
    elf[1] = (byte)'E';
    elf[2] = (byte)'L';
    elf[3] = (byte)'F';
    Assert.That(PortableExecutable.TryRead(elf, out _), Is.False, "an ELF");
  }

  /// <summary>
  /// A PE whose headers point outside itself must be refused rather than followed.
  /// </summary>
  /// <remarks>
  /// The bytes come from somewhere else — a file on disk that anybody may have written — and the
  /// resource tree is a tree of offsets into itself. Every one of these was a real crash in a reader
  /// of this format at some point in its history.
  /// </remarks>
  [Test]
  public void AHeaderPointingOutsideTheFileIsRefusedRatherThanFollowed() {
    var image = File.ReadAllBytes(
      Path.Combine(TestContext.CurrentContext.TestDirectory, "Hawkynt.ProcessManager.Core.dll")
    );

    // The PE header's own offset, past the end of the file.
    var moved = (byte[])image.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(moved.AsSpan(0x3C), 0x7FFF_0000);
    Assert.That(PortableExecutable.TryRead(moved, out _), Is.False, "e_lfanew past the end");

    // A file cut off inside its own section table, which is the first thing the walk needs whole.
    Assert.That(PortableExecutable.TryRead(image.AsSpan(0, 256), out _), Is.False, "cut off mid-header");

    // Cut off after the headers instead. That is still a readable PE header describing a resource
    // section whose bytes are not there, and the honest answer is the header's facts and no version
    // rather than a refusal to read the file at all (PRD §72.3).
    Assert.That(PortableExecutable.TryRead(image.AsSpan(0, 1024), out var headerOnly), Is.True, "headers survive");
    Assert.That(headerOnly.Subsystem, Is.EqualTo((ushort)3));
    Assert.That(headerOnly.FileVersion, Is.Null, "the resource section was not in the bytes");

    // The whole file with the resource directory's address moved somewhere it cannot be. The image
    // is still a PE image and still parses; it simply has no version resource to find, which is a
    // true statement about a great many programs (PRD §72.3).
    var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C));
    var directories = peOffset + 24 + 96 + (2 * 8);
    var broken = (byte[])image.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(directories), 0x7FFF_0000);
    Assert.That(PortableExecutable.TryRead(broken, out var facts), Is.True);
    Assert.That(facts.FileVersion, Is.Null, "no resource tree to read a version out of");
    Assert.That(facts.Subsystem, Is.EqualTo((ushort)3), "the header is still readable");
  }

  #endregion

}
