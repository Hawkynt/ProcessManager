using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// <c>maps</c> and <c>smaps</c>, against recorded files (PRD §9.1, §31).
/// </summary>
/// <remarks>
/// The fixtures are a real <c>cat</c> process: five mappings of the executable, five of libc, and a
/// second recording of the same program running from a binary that was unlinked while it ran, which
/// is the only way to get a <c>(deleted)</c> line that a kernel actually wrote.
/// </remarks>
[TestFixture]
public sealed class ModuleMapTests {

  private static string Fixture(string name)
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-maps", name);

  private static List<ModuleRecord> Collect(string name, Counter resident)
    => MapsParser.Collect(File.ReadAllBytes(Fixture(name)), resident);

  private static ModuleRecord Find(List<ModuleRecord> modules, string path) {
    foreach (var module in modules)
      if (module.Path == path)
        return module;

    Assert.Fail($"{path} is not in the list.");
    return default;
  }

  [Test]
  public void OnlyFileBackedMappingsAreListed() {
    var modules = Collect("maps", Counter.NotSupported);

    // The recording also contains [heap], [stack], [vdso], [vvar], [vsyscall] and four anonymous
    // mappings. None of them is a loaded image, and the modules view exists to answer "which code is
    // in this process" (PRD §31); the memory map of §34 is where the rest belongs.
    Assert.That(modules, Has.Count.EqualTo(5));
    foreach (var module in modules)
      Assert.That(module.Path, Does.StartWith("/"));
  }

  [Test]
  public void TheFiveMappingsOfALibraryBecomeOneRow() {
    var libc = Find(Collect("maps", Counter.NotSupported), "/usr/lib/libc.so.6");

    Assert.Multiple(() => {
      Assert.That(libc.MappingCount, Is.EqualTo(5));
      Assert.That(libc.BaseAddress, Is.EqualTo(0x7fb178200000ul));
      Assert.That(libc.EndAddress, Is.EqualTo(0x7fb178418000ul));
      // The sum of the five, not the span from the first to the last: the two differ whenever the
      // loader leaves a gap between segments, which it does on every hardened toolchain.
      Assert.That(libc.Size, Is.EqualTo(2195456ul));
      Assert.That(libc.FileOffset.Value, Is.EqualTo(0ul));
      Assert.That(libc.Inode.Value, Is.EqualTo(680249ul));
      Assert.That(libc.Device, Is.EqualTo("00:1e"));
      Assert.That(libc.IsDeleted, Is.False);
    });
  }

  [Test]
  public void ThePermissionsOfTheFoldedMappingsAreTheirUnion() {
    var libc = Find(Collect("maps", Counter.NotSupported), "/usr/lib/libc.so.6");

    // r--p, r-xp, r--p, r--p, rw-p — the library is readable, writable and executable across its
    // mappings even though no single mapping is all three.
    Assert.That(libc.Permissions, Is.EqualTo("rwxp"));
    var access = MapsParser.ParsePermissions(libc.Permissions);
    Assert.Multiple(() => {
      Assert.That(access.HasFlag(MapPermissions.Execute), Is.True);
      Assert.That(access.HasFlag(MapPermissions.Write), Is.True);
      Assert.That(access.HasFlag(MapPermissions.Shared), Is.False);
      Assert.That(access.HasFlag(MapPermissions.Private), Is.True);
    });
  }

  [Test]
  public void MapsCarriesNoResidentSizeAndSaysSo() {
    foreach (var module in Collect("maps", Counter.NotPermitted))
      Assert.That(module.ResidentBytes.Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  [Test]
  public void SmapsSumsTheResidentBytesOfEveryMappingOfAFile() {
    var modules = Collect("smaps", Counter.NotSupported);

    Assert.Multiple(() => {
      // Read straight out of the recording: the five Rss lines of libc add to 1296 kB, and the whole
      // point of summing them is that the mapping that is resident is not the one that is largest.
      Assert.That(Find(modules, "/usr/lib/libc.so.6").ResidentBytes.Value, Is.EqualTo(1327104ul));
      Assert.That(Find(modules, "/usr/bin/cat").ResidentBytes.Value, Is.EqualTo(57344ul));
      Assert.That(Find(modules, "/usr/lib/locale/locale-archive").ResidentBytes.Value, Is.EqualTo(2215936ul));
      // A three-megabyte mapping with two megabytes resident: the difference is the reason the column
      // exists at all.
      Assert.That(Find(modules, "/usr/lib/locale/locale-archive").Size, Is.EqualTo(3067904ul));
    });
  }

  [Test]
  public void SmapsAndMapsAgreeOnEverythingSmapsDoesNotAdd() {
    var fromMaps = Collect("maps", Counter.NotSupported);
    var fromSmaps = Collect("smaps", Counter.NotSupported);

    Assert.That(fromSmaps, Has.Count.EqualTo(fromMaps.Count));
    for (var i = 0; i < fromMaps.Count; ++i)
      // Same process, same instant, two files: any disagreement is the parser inventing something.
      Assert.That(fromSmaps[i] with { ResidentBytes = Counter.NotSupported }, Is.EqualTo(fromMaps[i]));
  }

  [Test]
  public void AnUnlinkedImageIsStillListedAndIsMarked() {
    var deleted = Find(Collect("maps-deleted", Counter.NotSupported), "/tmp/pm-deleted-image");

    Assert.Multiple(() => {
      // The suffix belongs to the flag, not to the path: a path that keeps it cannot be opened, and
      // every consumer would have had to strip it again.
      Assert.That(deleted.IsDeleted, Is.True);
      Assert.That(deleted.Path, Is.EqualTo("/tmp/pm-deleted-image"));
      Assert.That(deleted.MappingCount, Is.EqualTo(5));
      Assert.That(deleted.Device, Is.EqualTo("00:34"));
    });
  }

  [Test]
  public void APathWithSpacesSurvivesWhole() {
    // The field before it is the inode, and the path runs to the end of the line — so a parser that
    // takes the path as a whitespace-delimited field reports "/opt/My" and a module nobody has.
    var line = "7f0000000000-7f0000001000 r-xp 00000000 08:01 12345    /opt/My App/lib my.so"u8;

    Assert.That(MapsParser.TryParseRegion(line, out _, out var path), Is.True);
    Assert.That(Encoding.UTF8.GetString(line[path]), Is.EqualTo("/opt/My App/lib my.so"));
  }

  [Test]
  public void ACounterLineIsNotMistakenForAMapping() {
    // Every line of smaps that is not a header has to be rejected by shape alone, because the parser
    // reads the two files with the same code and has no state to tell it which one it is in.
    Assert.Multiple(() => {
      Assert.That(MapsParser.TryParseRegion("Rss:                   8 kB"u8, out _, out _), Is.False);
      Assert.That(MapsParser.TryParseRegion("VmFlags: rd mr mw me sd "u8, out _, out _), Is.False);
      Assert.That(MapsParser.TryParseRegion("THPeligible:    0"u8, out _, out _), Is.False);
      Assert.That(MapsParser.TryParseRegion("it_value: (0, 0)"u8, out _, out _), Is.False);
    });
  }

  [Test]
  public void TheCountersOfAnAnonymousMappingAreNotChargedToTheFileAboveIt() {
    // libc's last mapping is followed by an anonymous one. Attributing that block to libc — which is
    // what happens if the current row is not detached when a mapping has no path — overstates every
    // library that happens to sit above the heap.
    var content = Encoding.UTF8.GetBytes(string.Join('\n', [
      "7f0000000000-7f0000001000 r--p 00000000 08:01 4242   /usr/lib/libx.so",
      "Rss:                   4 kB",
      "7f0000001000-7f0000009000 rw-p 00000000 00:00 0 ",
      "Rss:                  32 kB",
      "",
    ]));

    var modules = MapsParser.Collect(content, Counter.NotSupported);
    Assert.That(modules, Has.Count.EqualTo(1));
    Assert.That(modules[0].ResidentBytes.Value, Is.EqualTo(4096ul));
  }

  [Test]
  public void PermissionsRoundTripThroughTheirTextForm() {
    foreach (var text in new[] { "r--p", "rw-p", "r-xp", "rwxs", "---p", "rw-s" })
      Assert.That(MapsParser.Format(MapsParser.ParsePermissions(text)), Is.EqualTo(text));
  }

  [Test]
  public void UnreadPermissionsAreNoneRatherThanNoAccess() {
    // Windows reports no per-module protection at all. None must therefore mean "nobody said", and a
    // mapping that genuinely grants nothing must still parse to something else (PRD §72.3).
    Assert.Multiple(() => {
      Assert.That(MapsParser.ParsePermissions(string.Empty), Is.EqualTo(MapPermissions.None));
      Assert.That(MapsParser.ParsePermissions("---p"), Is.EqualTo(MapPermissions.Private));
    });
  }

}
