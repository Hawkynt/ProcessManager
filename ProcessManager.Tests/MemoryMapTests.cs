using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The memory map of §34, against recorded files (PRD §9.1).
/// </summary>
/// <remarks>
/// The same two fixtures the module list is tested against — a real <c>cat</c> process — read for the
/// opposite question. §31 asks which images are loaded and folds; this asks what is at an address and
/// must not, so the two tests over one recording are what keeps the second from quietly becoming the
/// first.
/// </remarks>
[TestFixture]
public sealed class MemoryMapTests {

  private static string Fixture(string name)
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-maps", name);

  private static List<MemoryRegionRecord> Collect(string name, Counter detail)
    => MemoryMap.Collect(File.ReadAllBytes(Fixture(name)), detail);

  private static MemoryRegionRecord At(List<MemoryRegionRecord> regions, ulong start) {
    foreach (var region in regions)
      if (region.Start == start)
        return region;

    Assert.Fail($"nothing is mapped at {start:x}.");
    return default;
  }

  [Test]
  public void EveryMappingIsARow() {
    // Twenty-six lines, twenty-six rows. The module list turns the same file into five, which is the
    // whole difference between the two views (PRD §31, §34).
    Assert.That(Collect("maps", Counter.NotSupported), Has.Count.EqualTo(26));
  }

  [Test]
  public void TheTwoFilesDescribeTheSameAddressSpace() {
    var fromMaps = Collect("maps", Counter.NotSupported);
    var fromSmaps = Collect("smaps", Counter.NotSupported);

    Assert.That(fromSmaps, Has.Count.EqualTo(fromMaps.Count));
    for (var i = 0; i < fromMaps.Count; ++i)
      Assert.Multiple(() => {
        Assert.That(fromSmaps[i].Start, Is.EqualTo(fromMaps[i].Start));
        Assert.That(fromSmaps[i].End, Is.EqualTo(fromMaps[i].End));
        Assert.That(fromSmaps[i].Permissions, Is.EqualTo(fromMaps[i].Permissions));
        Assert.That(fromSmaps[i].Path, Is.EqualTo(fromMaps[i].Path));
      });
  }

  [Test]
  public void TheKernelsOrderIsKept() {
    // Ascending, which is the one property a memory map has that a module list does not: the row
    // above is the memory below. Sorting it here would take that away from every caller at once.
    var regions = Collect("maps", Counter.NotSupported);
    for (var i = 1; i < regions.Count; ++i)
      Assert.That(regions[i].Start, Is.GreaterThanOrEqualTo(regions[i - 1].End));
  }

  [Test]
  public void ASegmentIsNotFoldedIntoTheOneBelowIt() {
    var regions = Collect("maps", Counter.NotSupported);

    // The five mappings of the executable are five rows, and the point of that is here: read-only at
    // the bottom, executable above it, writable at the top. One folded row would report a mapping
    // that is readable, writable and executable at once — which is not what the kernel granted, and
    // is the single fact somebody opens this view to check.
    var text = At(regions, 0x556a67582000);
    var data = At(regions, 0x556a6758d000);
    Assert.Multiple(() => {
      Assert.That(text.Permissions, Is.EqualTo(MapPermissions.Read | MapPermissions.Execute | MapPermissions.Private));
      Assert.That(data.Permissions, Is.EqualTo(MapPermissions.Read | MapPermissions.Write | MapPermissions.Private));
      Assert.That(text.Path, Is.EqualTo("/usr/bin/cat"));
      Assert.That(data.Path, Is.EqualTo("/usr/bin/cat"));
    });
  }

  [Test]
  public void APseudoRegionKeepsItsNameAndIsNotAFile() {
    var regions = Collect("maps", Counter.NotSupported);

    Assert.Multiple(() => {
      Assert.That(At(regions, 0x556a739e2000).Kind, Is.EqualTo(MemoryRegionKind.Heap));
      Assert.That(At(regions, 0x7ffff6f96000).Kind, Is.EqualTo(MemoryRegionKind.Stack));
      Assert.That(At(regions, 0x7fb1784ad000).Kind, Is.EqualTo(MemoryRegionKind.KernelProvided));
      Assert.That(At(regions, 0xffffffffff600000).Kind, Is.EqualTo(MemoryRegionKind.KernelProvided));
      // The module list throws these away, because none of them is a loaded image. Here the name is
      // what identifies the row.
      Assert.That(At(regions, 0x556a739e2000).Path, Is.EqualTo("[heap]"));
    });
  }

  [Test]
  public void APseudoNameThisBuildDoesNotKnowStillReachesTheReader() {
    // [vvar_vclock] arrived in 6.13 and this build has never been taught it by name. Classification
    // is by prefix precisely so that the next one lands as something rather than as a file that does
    // not exist — and so that its name is still on the row.
    var region = At(Collect("maps", Counter.NotSupported), 0x7fb1784ab000);

    Assert.Multiple(() => {
      Assert.That(region.Path, Is.EqualTo("[vvar_vclock]"));
      Assert.That(region.Kind, Is.EqualTo(MemoryRegionKind.KernelProvided));
    });

    // And a bracketed name that is not one of the kernel's virtual regions is not silently called one.
    Assert.That(MemoryMap.Classify("[something_new]"), Is.EqualTo(MemoryRegionKind.Pseudo));
  }

  [Test]
  public void AnAnonymousMappingHasNoPathAndIsNotAHole() {
    var region = At(Collect("maps", Counter.NotSupported), 0x7fb178418000);

    Assert.Multiple(() => {
      Assert.That(region.Path, Is.Null);
      Assert.That(region.Kind, Is.EqualTo(MemoryRegionKind.Anonymous));
      // The inode really is nought for an anonymous mapping. A reason here would claim the kernel
      // declined to say, which it did not (PRD §3.4).
      Assert.That(region.Inode.HasValue, Is.True);
      Assert.That(region.Inode.Value, Is.EqualTo(0ul));
    });
  }

  [Test]
  public void NamedMemoryThatIsNotAFileIsNotCountedAsOne() {
    Assert.Multiple(() => {
      Assert.That(MemoryMap.Classify("/memfd:pulseaudio (deleted)"), Is.EqualTo(MemoryRegionKind.SharedMemory));
      Assert.That(MemoryMap.Classify("/dev/shm/wayland.mozilla"), Is.EqualTo(MemoryRegionKind.SharedMemory));
      Assert.That(MemoryMap.Classify("/SYSV00000000"), Is.EqualTo(MemoryRegionKind.SharedMemory));
      // Memory on the card rather than memory on the machine, and the largest thing in a browser's
      // map on any machine with a graphics driver.
      Assert.That(MemoryMap.Classify("/dev/dri/renderD128"), Is.EqualTo(MemoryRegionKind.Device));
      Assert.That(MemoryMap.Classify("/usr/lib/libc.so.6"), Is.EqualTo(MemoryRegionKind.FileBacked));
    });
  }

  [Test]
  public void TheCountersComeOffTheMappingTheyBelongTo() {
    var heap = At(Collect("smaps", Counter.NotSupported), 0x556a739e2000);

    Assert.Multiple(() => {
      Assert.That(heap.Size, Is.EqualTo(135168ul));
      Assert.That(heap.ResidentBytes.Value, Is.EqualTo(8192ul));
      Assert.That(heap.ProportionalBytes.Value, Is.EqualTo(8192ul));
      Assert.That(heap.PrivateDirtyBytes.Value, Is.EqualTo(8192ul));
      Assert.That(heap.SharedDirtyBytes.Value, Is.EqualTo(0ul));
      Assert.That(heap.AnonymousBytes.Value, Is.EqualTo(8192ul));
      Assert.That(heap.SwapBytes.Value, Is.EqualTo(0ul));
      // Two of the counters have keys that are prefixes of other keys in the same block. Swap: must
      // not take SwapPss:, and Pss: must not take Pss_Dirty: — which for this mapping differ, so the
      // assertion above would fail rather than pass by luck.
      Assert.That(heap.Flags, Is.EqualTo("rd wr mr mw me ac sd"));
    });
  }

  [Test]
  public void ReadingTheCheapFileLeavesReasonsAndNotZeroes() {
    // maps carries the addresses and nothing else. A page-table counter of nought here would say the
    // heap has nothing resident, which is a claim rather than an absence (PRD §3.4).
    var heap = At(Collect("maps", Counter.NotPermitted), 0x556a739e2000);

    Assert.Multiple(() => {
      Assert.That(heap.ResidentBytes.HasValue, Is.False);
      Assert.That(heap.ResidentBytes.Reason, Is.EqualTo(UnknownReason.NotPermitted));
      Assert.That(heap.PrivateDirtyBytes.Reason, Is.EqualTo(UnknownReason.NotPermitted));
      Assert.That(heap.HugePageBytes.Reason, Is.EqualTo(UnknownReason.NotPermitted));
      Assert.That(heap.Flags, Is.Null);
      // The addresses are still there. Half an answer is what this file is for.
      Assert.That(heap.Size, Is.EqualTo(135168ul));
    });
  }

  [Test]
  public void ADeletedBackingFileIsSaidRatherThanShown() {
    var deleted = Collect("maps-deleted", Counter.NotSupported);
    var found = false;
    foreach (var region in deleted)
      if (region.IsDeleted) {
        found = true;
        // The suffix is the kernel's annotation and not part of the name; leaving it on would make
        // the path useless for anything that wanted to look the file up.
        Assert.That(region.Path, Does.Not.Contain("(deleted)"));
      }

    Assert.That(found, Is.True, "the recording is of a binary unlinked while it ran");
  }

  [Test]
  public void AnEmptyMapIsNotTheSameAnswerAsARefusedOne() {
    // A kernel thread's maps is an empty file that reads perfectly well, and another user's is a
    // full one that will not (PRD §5.3).
    Assert.Multiple(() => {
      Assert.That(MemoryMap.Collect([], Counter.NotSupported), Is.Empty);
      Assert.That(new MemoryMapReading(MemoryMapState.Available, false, []).Explain(), Is.Empty);
      Assert.That(new MemoryMapReading(MemoryMapState.NotPermitted, false, []).Explain(), Is.Not.Empty);
      Assert.That(MemoryMapReading.NotImplemented.Explain(), Is.Not.Empty);
    });
  }

}
