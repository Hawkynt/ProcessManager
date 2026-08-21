using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Why each image is in a process, and how many times it is (PRD §31).
/// </summary>
/// <remarks>
/// Windows publishes a load reason per module and Linux publishes none, so the answer is derived
/// from what the images say about each other — and the derivation is sound in one direction only.
/// These tests are as much about the direction it is <em>not</em> sound in: an image nothing names
/// reports "nothing that could be read names this" and never "somebody called <c>dlopen</c>".
/// </remarks>
[TestFixture]
public sealed class ModuleGraphTests {

  /// <summary>A row with only the fields the graph reads, and a reason on everything else.</summary>
  private static ModuleRecord Row(string path, ulong at = 0x1000) => new(
    Path: path,
    BaseAddress: at,
    Size: 0x1000,
    Permissions: "r-xp",
    EndAddress: at + 0x1000,
    ResidentBytes: Counter.NotSampledYet,
    FileOffset: Counter.Of(0ul),
    Inode: Counter.NotSampledYet,
    Device: null,
    IsDeleted: false,
    MappingCount: 1,
    FileSizeBytes: Counter.NotSampledYet,
    FileModifiedUtcTicks: 0,
    Type: ModuleType.SharedObject,
    Architecture: "x86-64",
    EntryPoint: Counter.NotSampledYet,
    Soname: null,
    Interpreter: null,
    Mitigations: ImageMitigations.Read,
    BuildId: null,
    // What the graph is being asked to fill in. Both start at the value that means "nobody has
    // worked this out yet", so a test that passes because the field was already right cannot.
    LoadReason: ModuleLoadReason.Unknown,
    LoadCount: 0,
    Runtime: ModuleRuntime.Native
  );

  private static ElfImage.Description Image(string? soname = null, string? interpreter = null, params string[] needed)
    => ElfImage.Unread with {
      Type = interpreter is null ? ModuleType.SharedObject : ModuleType.Executable,
      Soname = soname,
      Interpreter = interpreter,
      Needed = needed,
      Mitigations = ImageMitigations.Read,
    };

  /// <summary>The four rows of an ordinary dynamically linked program.</summary>
  private static (List<ModuleRecord> Modules, List<ElfImage.Description> Images) Program() {
    var modules = new List<ModuleRecord> {
      Row("/usr/bin/editor", 0x400000),
      Row("/usr/lib/ld-linux-x86-64.so.2", 0x7f0000),
      Row("/usr/lib/libc.so.6", 0x7f1000),
      Row("/usr/lib/libm.so.6", 0x7f2000),
      Row("/usr/lib/libcurl.so.4", 0x7f3000),
    };

    var images = new List<ElfImage.Description> {
      Image(null, "/lib64/ld-linux-x86-64.so.2", "libc.so.6"),
      Image("ld-linux-x86-64.so.2"),
      Image("libc.so.6"),
      // Named by libc and not by the program: an indirect dependency.
      Image("libm.so.6"),
      // Named by nothing at all.
      Image("libcurl.so.4"),
    };

    // libc names libm, which is what makes libm indirect rather than direct.
    images[2] = images[2] with { Needed = new[] { "libm.so.6" } };
    return (modules, images);
  }

  #region why an image is here

  [Test]
  public void TheProgramItsLoaderAndItsLibrariesEachGetTheirOwnReason() {
    var (modules, images) = Program();
    ModuleGraph.Assign(modules, images, "/usr/bin/editor");

    Assert.Multiple(() => {
      Assert.That(modules[0].LoadReason, Is.EqualTo(ModuleLoadReason.Image));
      Assert.That(modules[1].LoadReason, Is.EqualTo(ModuleLoadReason.Interpreter));
      Assert.That(modules[2].LoadReason, Is.EqualTo(ModuleLoadReason.Direct));
      Assert.That(modules[3].LoadReason, Is.EqualTo(ModuleLoadReason.Dependency));
      Assert.That(modules[4].LoadReason, Is.EqualTo(ModuleLoadReason.RunTime));
    });
  }

  /// <summary>
  /// The program asks for <c>/lib64/ld-linux-x86-64.so.2</c> and the kernel maps
  /// <c>/usr/lib/ld-linux-x86-64.so.2</c>. Comparing only the paths reports the dynamic loader as a
  /// run-time load on every distribution that puts its libraries under <c>/usr/lib</c>.
  /// </summary>
  [Test]
  public void TheLoaderIsRecognisedThroughTheSymlinkProcHasAlreadyResolved() {
    var (modules, images) = Program();
    ModuleGraph.Assign(modules, images, "/usr/bin/editor");

    Assert.That(modules[1].Path, Is.Not.EqualTo(images[0].Interpreter), "the test would prove nothing otherwise");
    Assert.That(modules[1].LoadReason, Is.EqualTo(ModuleLoadReason.Interpreter));
  }

  /// <summary>
  /// The executable is found by its <c>PT_INTERP</c> when nothing hands over a path, because a
  /// program names a loader and a shared library never does.
  /// </summary>
  [Test]
  public void TheProgramIsFoundByItsInterpreterWithNoPathToGoOn() {
    var (modules, images) = Program();
    ModuleGraph.Assign(modules, images);

    Assert.That(modules[0].LoadReason, Is.EqualTo(ModuleLoadReason.Image));
  }

  /// <summary>
  /// An image whose own headers could not be read is not a run-time load: it is a row nobody looked
  /// at, and saying "loaded at run time" would be a claim built on a file nobody opened (PRD §72.3).
  /// </summary>
  [Test]
  public void AnImageNobodyCouldReadIsUnknownRatherThanADlopen() {
    var (modules, images) = Program();
    images[4] = ElfImage.Unread;
    ModuleGraph.Assign(modules, images, "/usr/bin/editor");

    Assert.That(modules[4].LoadReason, Is.EqualTo(ModuleLoadReason.Unknown));
    Assert.That(Humanize.LoadReason(modules[4].LoadReason), Is.EqualTo("—"));
  }

  /// <summary>A mapped file that is not an image at all is data, and data is its own reason.</summary>
  [Test]
  public void AMappedFileThatIsNotAnImageIsData() {
    var (modules, images) = Program();
    modules.Add(Row("/usr/lib/locale/locale-archive", 0x800000));
    images.Add(ElfImage.Unread with { Type = ModuleType.Data, Runtime = ModuleRuntime.NotCode });
    ModuleGraph.Assign(modules, images, "/usr/bin/editor");

    Assert.That(modules[^1].LoadReason, Is.EqualTo(ModuleLoadReason.Data));
  }

  /// <summary>
  /// A library is named by its <c>SONAME</c> and not by its path, and a library that publishes none
  /// is named by its file name — so neither alone answers for every library on a machine.
  /// </summary>
  [Test]
  public void ALibraryIsMatchedByItsSonameAndByItsFileName() {
    var (modules, images) = Program();
    // libcurl publishes no SONAME, and the program names it by the only name it has.
    images[4] = images[4] with { Soname = null };
    images[0] = images[0] with { Needed = new[] { "libc.so.6", "libcurl.so.4" } };
    ModuleGraph.Assign(modules, images, "/usr/bin/editor");

    Assert.That(modules[4].LoadReason, Is.EqualTo(ModuleLoadReason.Direct));
  }

  #endregion

  #region how many times it is here (PRD §31 — load count)

  [Test]
  public void OneLoadOfAFileCountsAsOne() {
    var (modules, images) = Program();
    ModuleGraph.Assign(modules, images, "/usr/bin/editor");

    Assert.That(modules.Select(m => m.LoadCount), Is.All.EqualTo(1));
    Assert.That(Humanize.LoadCount(modules[0].LoadCount), Is.EqualTo("1"));
  }

  /// <summary>
  /// The case the column exists for: two copies of one file in one address space, which is two sets
  /// of its global state and is what .NET does to every assembly it loads.
  /// </summary>
  [Test]
  public void AFileLoadedTwiceSaysSoOnBothOfItsRows() {
    var (modules, images) = Program();
    modules.Add(Row("/usr/lib/libcurl.so.4", 0x9000000));
    images.Add(Image("libcurl.so.4"));
    ModuleGraph.Assign(modules, images, "/usr/bin/editor");

    Assert.Multiple(() => {
      Assert.That(modules[4].LoadCount, Is.EqualTo(2));
      Assert.That(modules[^1].LoadCount, Is.EqualTo(2));
      // And every other row is still one: the count is per file and not per list.
      Assert.That(modules[2].LoadCount, Is.EqualTo(1));
    });
  }

  /// <summary>
  /// A nought is the pass never having run, and must never render as a number. A row exists because
  /// a mapping named the file, so no row is loaded nought times (PRD §72.3).
  /// </summary>
  [Test]
  public void AnUncountedRowSaysSoRatherThanClaimingNoLoads() {
    Assert.That(Row("/usr/lib/libc.so.6").LoadCount, Is.Zero, "the map parser leaves it for the graph");
    Assert.That(Humanize.LoadCount(0), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)));
    Assert.That(Humanize.LoadCount(0), Is.Not.EqualTo("0"));
  }

  /// <summary>
  /// The parser's own promise, held here because the count rests on it: consecutive mappings of one
  /// file are one row, and mappings that are not consecutive are two — which is what makes a second
  /// row a second load rather than a second segment (PRD §31).
  /// </summary>
  [Test]
  public void TheCountRestsOnTheFoldTheMapParserPerforms() {
    const string Maps = """
      00400000-00401000 r--p 00000000 08:02 131074 /usr/lib/libtwice.so
      00401000-00402000 r-xp 00001000 08:02 131074 /usr/lib/libtwice.so
      7f0000000000-7f0000001000 r--p 00000000 08:02 131074 /usr/lib/libtwice.so
      """;

    var modules = MapsParser.Collect(System.Text.Encoding.UTF8.GetBytes(Maps), Counter.NotSupported);
    Assert.That(modules, Has.Count.EqualTo(2), "adjacent folds; a jump does not");
    Assert.That(modules[0].MappingCount, Is.EqualTo(2));

    ModuleGraph.Assign(modules, [Image("libtwice.so"), Image("libtwice.so")]);
    Assert.That(modules[0].LoadCount, Is.EqualTo(2));
    Assert.That(modules[1].LoadCount, Is.EqualTo(2));
  }

  #endregion

  [Test]
  public void ADescriptionPerRowIsRequiredRatherThanAssumed() {
    var (modules, _) = Program();
    Assert.That(() => ModuleGraph.Assign(modules, [Image()]), Throws.ArgumentException);
  }

}
