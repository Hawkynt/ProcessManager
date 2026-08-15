using System.Runtime.InteropServices;
using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Windows;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The Windows structure walk, replayed on any OS (PRD §9.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this proves and what it does not.</strong> The buffer here is <em>synthesised</em>
/// from the same struct definition the parser reads, so it cannot catch the parser understanding the
/// layout wrongly — if <see cref="NtStructures.SystemProcessInformation"/> has a field in the wrong
/// place, both sides are wrong together and every assertion still passes. What it does catch is
/// everything on top of the layout: the chain walk and its terminator, the FILETIME-to-nanosecond
/// conversion, the byte-versus-character length of a <c>UNICODE_STRING</c>, the pointer-to-offset
/// rebase, the bounds check, and any regression in the record mapping.
/// </para>
/// <para>
/// Replacing this with a buffer captured from a real Windows machine is the remaining half of §9.4
/// and the thing that would actually verify the layout. Until then the Windows probe is
/// <em>exercised</em> but not <em>verified</em>, and the README says so.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WindowsProcessInformationReplayTests {

  private sealed record FakeProcess(
    int Pid,
    int ParentPid,
    string Name,
    long CreateTime,
    long UserTime100Ns,
    long KernelTime100Ns,
    uint Threads,
    uint Handles,
    ulong PrivateBytes,
    ulong WorkingSet,
    long ReadBytes,
    long WriteBytes,
    uint SessionId
  );

  [Test]
  public void TheChainIsWalkedToItsTerminatorAndEveryFieldLands() {
    var processes = new[] {
      new FakeProcess(0, 0, "", 0, 0, 0, 4, 0, 0, 0, 0, 0, 0),
      new FakeProcess(4, 0, "System", 133_000_000_000_000_000L, 10_000_000, 20_000_000, 180, 4200, 0, 143_360, 0, 0, 0),
      new FakeProcess(1234, 4, "explorer.exe", 133_100_000_000_000_000L, 5_000_000, 2_500_000, 42, 1337,
        104_857_600, 209_715_200, 4_096_000, 2_048_000, 1),
    };

    var (buffer, baseAddress, handle) = Build(processes);
    try {
      var snapshot = new SystemSnapshot();
      InvokeParse(buffer, baseAddress, snapshot);

      Assert.That(snapshot.ProcessCount, Is.EqualTo(3), "the walk stopped at NextEntryOffset == 0");
      Assert.That(snapshot.System.TotalThreads, Is.EqualTo(4 + 180 + 42));

      var explorer = Find(snapshot, 1234);
      Assert.That(explorer.Name, Is.EqualTo("explorer.exe"));
      Assert.That(explorer.ParentPid, Is.EqualTo(4));
      Assert.That(explorer.SessionId, Is.EqualTo(1));
      Assert.That(explorer.ThreadCount, Is.EqualTo(42));
      Assert.That(explorer.HandleCount.Value, Is.EqualTo(1337ul));
      Assert.That(explorer.PrivateBytes.Value, Is.EqualTo(104_857_600ul));
      Assert.That(explorer.WorkingSetBytes.Value, Is.EqualTo(209_715_200ul));
      Assert.That(explorer.ReadBytes.Value, Is.EqualTo(4_096_000ul));
      Assert.That(explorer.WriteBytes.Value, Is.EqualTo(2_048_000ul));
    } finally {
      handle.Free();
    }
  }

  [Test]
  public void FileTimeUnitsBecomeNanoseconds() {
    // 5,000,000 FILETIME units is half a second; the model counts in nanoseconds everywhere above
    // the probe, so a probe that forgets the ×100 is out by two orders of magnitude and still looks
    // plausible on screen.
    var (buffer, baseAddress, handle) = Build([
      new(1234, 4, "explorer.exe", 133_100_000_000_000_000L, 5_000_000, 2_500_000, 1, 0, 0, 0, 0, 0, 1),
    ]);

    try {
      var snapshot = new SystemSnapshot();
      InvokeParse(buffer, baseAddress, snapshot);

      var process = Find(snapshot, 1234);
      Assert.That(process.UserTimeNs.Value, Is.EqualTo(500_000_000ul));
      Assert.That(process.KernelTimeNs.Value, Is.EqualTo(250_000_000ul));
      Assert.That(process.CpuTimeNs.Value, Is.EqualTo(750_000_000ul));
    } finally {
      handle.Free();
    }
  }

  [Test]
  public void TheIdentityKeyIsThePidAndItsCreationTime() {
    var (buffer, baseAddress, handle) = Build([
      new(1234, 4, "explorer.exe", 133_100_000_000_000_000L, 0, 0, 1, 0, 0, 0, 0, 0, 1),
    ]);

    try {
      var snapshot = new SystemSnapshot();
      InvokeParse(buffer, baseAddress, snapshot);

      var process = Find(snapshot, 1234);
      Assert.That(process.Key.StartTicks, Is.EqualTo(133_100_000_000_000_000ul));
      Assert.That(process.StartTimeUtcTicks, Is.EqualTo(DateTime.FromFileTimeUtc(133_100_000_000_000_000L).Ticks));
    } finally {
      handle.Free();
    }
  }

  [Test]
  public void TheTwoNamelessSystemProcessesAreNamedRatherThanBlank() {
    var (buffer, baseAddress, handle) = Build([
      new(0, 0, "", 0, 0, 0, 4, 0, 0, 0, 0, 0, 0),
      new(4, 0, "", 0, 0, 0, 180, 0, 0, 0, 0, 0, 0),
      new(9999, 0, "", 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
    ]);

    try {
      var snapshot = new SystemSnapshot();
      InvokeParse(buffer, baseAddress, snapshot);

      Assert.That(Find(snapshot, 0).Name, Is.EqualTo("Idle"));
      Assert.That(Find(snapshot, 4).Name, Is.EqualTo("System"));
      // Anything else without a name is a process we could not read, and its pid is the only honest
      // thing to show for it.
      Assert.That(Find(snapshot, 9999).Name, Is.EqualTo("(9999)"));
    } finally {
      handle.Free();
    }
  }

  [Test]
  public void AnImageNamePointingOutsideTheBufferIsRefusedRatherThanRead() {
    // What a captured buffer looks like when it is replayed with the wrong base address, and what a
    // corrupt one looks like either way. Dereferencing the pointer — which the first implementation
    // did — reads whatever happens to be at that address in this process.
    var (buffer, baseAddress, handle) = Build([
      new(1234, 4, "explorer.exe", 0, 0, 0, 1, 0, 0, 0, 0, 0, 1),
    ]);

    try {
      var snapshot = new SystemSnapshot();
      InvokeParse(buffer, baseAddress + 0x7FFF_0000, snapshot);
      Assert.That(Find(snapshot, 1234).Name, Is.EqualTo("(1234)"));
    } finally {
      handle.Free();
    }
  }

  #region building a buffer the way the kernel would

  private static (byte[] Buffer, nint BaseAddress, GCHandle Handle) Build(FakeProcess[] processes) {
    var entrySize = Marshal.SizeOf<NtStructures.SystemProcessInformation>();
    var threadSize = Marshal.SizeOf<NtStructures.SystemThreadInformation>();

    // Layout, as the kernel writes it: each process entry, then its thread entries, then the next
    // process. The names go in a run at the end, which every UNICODE_STRING points back into.
    var total = 0;
    foreach (var process in processes)
      total += Align(entrySize + threadSize * (int)process.Threads);

    var nameStart = total;
    foreach (var process in processes)
      total += process.Name.Length * sizeof(char);

    var buffer = new byte[total];
    var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
    var baseAddress = handle.AddrOfPinnedObject();

    var offset = 0;
    var nameOffset = nameStart;
    for (var i = 0; i < processes.Length; ++i) {
      var process = processes[i];
      var size = Align(entrySize + threadSize * (int)process.Threads);
      var isLast = i == processes.Length - 1;

      var entry = new NtStructures.SystemProcessInformation {
        NextEntryOffset = isLast ? 0u : (uint)size,
        NumberOfThreads = process.Threads,
        CreateTime = process.CreateTime,
        UserTime = process.UserTime100Ns,
        KernelTime = process.KernelTime100Ns,
        BasePriority = 8,
        UniqueProcessId = process.Pid,
        InheritedFromUniqueProcessId = process.ParentPid,
        HandleCount = process.Handles,
        SessionId = process.SessionId,
        PrivatePageCount = (nuint)process.PrivateBytes,
        WorkingSetSize = (nuint)process.WorkingSet,
        VirtualSize = (nuint)(process.WorkingSet * 4),
        PagefileUsage = (nuint)process.PrivateBytes,
        ReadTransferCount = process.ReadBytes,
        WriteTransferCount = process.WriteBytes,
      };

      if (process.Name.Length > 0) {
        var bytes = Encoding.Unicode.GetBytes(process.Name);
        bytes.CopyTo(buffer, nameOffset);
        entry.ImageName = new() {
          // In bytes, not characters — the kernel's convention, and the one the parser has to match.
          Length = (ushort)bytes.Length,
          MaximumLength = (ushort)bytes.Length,
          Buffer = baseAddress + nameOffset,
        };

        nameOffset += bytes.Length;
      }

      MemoryMarshal.Write(buffer.AsSpan(offset), in entry);
      offset += size;
    }

    return (buffer, baseAddress, handle);
  }

  /// <summary>The kernel aligns each entry; so does this, or the walk lands mid-struct.</summary>
  private static int Align(int value) => (value + 7) & ~7;

  private static void InvokeParse(byte[] buffer, nint baseAddress, SystemSnapshot snapshot) {
    // The parser is internal to the platform assembly, which grants InternalsVisibleTo here. It is
    // not the probe: the probe is Windows-only, the walk is not, which is the whole point.
    SystemProcessInformationReader.Parse(buffer, baseAddress, snapshot);
  }

  private static ProcessRecord Find(SystemSnapshot snapshot, int pid) {
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process;

    Assert.Fail($"pid {pid} is not in the snapshot");
    return default;
  }

  #endregion

}
