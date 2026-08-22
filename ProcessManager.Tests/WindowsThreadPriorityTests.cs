using System.Runtime.InteropServices;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Windows;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The base priority the bulk query carries, which was being discarded (PRD §29).
/// </summary>
/// <remarks>
/// <para>
/// <c>SYSTEM_THREAD_INFORMATION</c> has a <c>BasePriority</c> field, the parser reads the whole
/// structure into a buffer, and the record it built passed <c>null</c> for it — under a comment
/// saying "the bulk query carries neither an affinity nor a base priority". The value was arriving
/// and being thrown away one line before it was used.
/// </para>
/// <para>
/// Base is what the scheduler was told the thread is worth. <c>Priority</c> beside it is where the
/// thread sits now, after the boosts a waiting thread collects and loses. A view showing one and not
/// the other cannot show that a thread has been boosted, which is why both are columns.
/// </para>
/// <para>
/// Replayed from a synthesised buffer, so this runs on every CI leg rather than only on Windows. It
/// cannot catch the struct definition itself being wrong — nothing here can — but it catches the
/// mapping from a field of that structure to a field of the record, which is what was broken.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WindowsThreadPriorityTests {

  private static (byte[] Buffer, GCHandle Handle) OneProcessWithOneThread(int priority, int basePriority) {
    var entrySize = Marshal.SizeOf<NtStructures.SystemProcessInformation>();
    var threadSize = Marshal.SizeOf<NtStructures.SystemThreadInformation>();
    var buffer = new byte[entrySize + threadSize];
    var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

    var entry = new NtStructures.SystemProcessInformation {
      NextEntryOffset = 0,
      NumberOfThreads = 1,
      CreateTime = 133_100_000_000_000_000L,
      UniqueProcessId = 1234,
    };

    var thread = new NtStructures.SystemThreadInformation {
      KernelTime = 1_000,
      UserTime = 2_000,
      CreateTime = 133_100_000_000_000_000L,
      StartAddress = (nint)0x7FF6_0000_1000L,
      ClientId = new() { UniqueProcess = 1234, UniqueThread = 5678 },
      Priority = priority,
      BasePriority = basePriority,
      ContextSwitches = 99,
    };

    MemoryMarshal.Write(buffer.AsSpan(), in entry);
    MemoryMarshal.Write(buffer.AsSpan(entrySize), in thread);
    return (buffer, handle);
  }

  private static ThreadRecord Read(int priority, int basePriority) {
    var (buffer, handle) = OneProcessWithOneThread(priority, basePriority);
    try {
      var threads = SystemProcessInformationReader.ReadThreads(
        buffer,
        new(1234, 133_100_000_000_000_000UL)
      );

      Assert.That(threads, Has.Count.EqualTo(1));
      return threads[0];
    } finally {
      handle.Free();
    }
  }

  /// <summary>The value arrives, where it used to be dropped.</summary>
  [Test]
  public void TheBasePriorityTheQueryCarriesReachesTheRecord()
    => Assert.That(Read(priority: 10, basePriority: 8).BasePriority, Is.EqualTo(8));

  /// <summary>
  /// And it is kept apart from the priority the thread has now. A boosted thread is one where the
  /// two differ, and that is the whole reason for showing both.
  /// </summary>
  [Test]
  public void ABoostedThreadShowsBothNumbers() {
    var boosted = Read(priority: 14, basePriority: 8);

    Assert.That(boosted.Priority, Is.EqualTo(14));
    Assert.That(boosted.BasePriority, Is.EqualTo(8));
    Assert.That(boosted.Priority, Is.Not.EqualTo(boosted.BasePriority), "the boost is visible");
  }

  /// <summary>
  /// The idle thread's base priority is nought, and nought is a real answer there rather than the
  /// absence of one — which is exactly why it must not be confused with the null it used to be.
  /// </summary>
  [Test]
  public void NoughtIsARealBasePriority()
    => Assert.That(Read(priority: 0, basePriority: 0).BasePriority, Is.EqualTo(0));

  /// <summary>
  /// The affinity really is absent from the bulk query, so it stays null — the half of the old
  /// comment that was true.
  /// </summary>
  [Test]
  public void TheAffinityIsStillAbsent()
    => Assert.That(Read(priority: 8, basePriority: 8).Affinity, Is.Null);

}
