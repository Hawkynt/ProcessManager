using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Windows;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The per-type handle tally of PRD §20, replayed on any OS.
/// </summary>
/// <remarks>
/// <para>
/// The same caveat as <see cref="WindowsProcessInformationReplayTests"/>: the table below is built
/// from the struct the walker reads, so a shared misunderstanding of the layout is invisible to it.
/// What it does catch is everything on top of the layout — the two bounds on the walk, the grouping
/// by owner, and the one thing here that is not obvious at all: that an object type index which was
/// never identified must produce a <em>missing</em> count and not a nought.
/// </para>
/// <para>
/// That last point is the reason this file exists. The type indices are the running kernel's, handed
/// out in the order its object types were created at boot, so they differ between machines and
/// between boots of the same machine. A build that hard-coded them would report a plausible tally of
/// entirely the wrong objects, and no test on a Windows machine would notice unless it happened to
/// boot differently.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WindowsObjectTallyTests {

  private static readonly int _HeaderSize = Unsafe.SizeOf<NtStructures.SystemHandleInformationEx>();
  private static readonly int _EntrySize = Unsafe.SizeOf<NtStructures.SystemHandleTableEntryInfoEx>();

  /// <summary>A handle table the way <c>SystemExtendedHandleInformation</c> hands one over.</summary>
  private static byte[] Table(params (int Pid, ushort TypeIndex)[] handles) {
    var buffer = new byte[_HeaderSize + (handles.Length * _EntrySize)];
    MemoryMarshal.Write(buffer, (nuint)handles.Length);
    for (var i = 0; i < handles.Length; ++i)
      MemoryMarshal.Write(buffer.AsSpan(_HeaderSize + (i * _EntrySize)), new NtStructures.SystemHandleTableEntryInfoEx {
        UniqueProcessId = (nuint)handles[i].Pid,
        HandleValue = (i + 1) * 4,
        ObjectTypeIndex = handles[i].TypeIndex,
      });

    return buffer;
  }

  /// <summary>An arbitrary but internally consistent set of indices, as one boot might have.</summary>
  private static readonly ObjectTypeIndices _Types = new(Event: 16, Semaphore: 17, Mutant: 18, Section: 43, Key: 37);

  [Test]
  public void EachKindIsCountedAgainstTheProcessThatHoldsIt() {
    var table = Table(
      (1234, 16), (1234, 16), (1234, 16),
      (1234, 43),
      (1234, 37), (1234, 37),
      // Another process entirely, and a type nothing here counts — a File, say.
      (5678, 16), (5678, 18), (5678, 99), (5678, 99)
    );

    var tallies = new Dictionary<int, ObjectTally>();
    WindowsObjectTally.Tally(table, in _Types, tallies);

    Assert.That(tallies[1234], Is.EqualTo(new ObjectTally(Events: 3, Semaphores: 0, Mutexes: 0, Sections: 1, Keys: 2)));
    Assert.That(tallies[5678], Is.EqualTo(new ObjectTally(Events: 1, Semaphores: 0, Mutexes: 1, Sections: 0, Keys: 0)));
    Assert.That(tallies.ContainsKey(99), Is.False, "a type index is not a process id");
  }

  /// <summary>
  /// A process that holds none of these objects is absent from the table, and that is a nought
  /// rather than an absence.
  /// </summary>
  [Test]
  public void AProcessTheTableNeverMentionsHoldsNoneOfThem() {
    var tallies = new Dictionary<int, ObjectTally>();
    WindowsObjectTally.Tally(Table((1234, 16)), in _Types, tallies);

    Assert.That(tallies.ContainsKey(4242), Is.False);
    // Which the probe turns into a real nought, because the whole machine's table was read and this
    // process was not in it. That is a measurement (PRD §72.3).
    tallies.TryGetValue(4242, out var missing);
    Assert.That(missing, Is.EqualTo(default(ObjectTally)));
  }

  /// <summary>
  /// The count in the header and the length of the buffer are two numbers that can disagree, and the
  /// walk must be bounded by both.
  /// </summary>
  /// <remarks>
  /// They do disagree in practice: the table is sized by one call and read by a second, and the
  /// machine keeps opening handles in between. Trusting the header walks off the end of the
  /// allocation; trusting the length reads whatever the tail of the buffer happens to contain.
  /// </remarks>
  [Test]
  public void AHeaderCountLargerThanTheBufferIsNotWalkedPastTheBuffer() {
    var table = Table((1234, 16), (1234, 16));
    // The kernel found two thousand more handles a moment after it told us there were two.
    MemoryMarshal.Write(table.AsSpan(), (nuint)2048);

    var tallies = new Dictionary<int, ObjectTally>();
    Assert.DoesNotThrow(() => WindowsObjectTally.Tally(table, in _Types, tallies));
    Assert.That(tallies[1234].Events, Is.EqualTo(2u));
  }

  [Test]
  public void ABufferTooSmallToHoldAHeaderIsRefusedRatherThanRead() {
    var tallies = new Dictionary<int, ObjectTally>();
    Assert.DoesNotThrow(() => WindowsObjectTally.Tally([], in _Types, tallies));
    Assert.DoesNotThrow(() => WindowsObjectTally.Tally(new byte[_HeaderSize - 1], in _Types, tallies));
    Assert.That(tallies, Is.Empty);
  }

  /// <summary>
  /// Every distinct type index is sampled once, whatever the size of the table.
  /// </summary>
  /// <remarks>
  /// The naming pass duplicates a handle per sample, so sampling per row rather than per index would
  /// mean a million duplications to learn a few dozen facts.
  /// </remarks>
  [Test]
  public void EveryTypeIsSampledOnceAndOnlyOnce() {
    var handles = new List<(int, ushort)>();
    for (var i = 0; i < 500; ++i)
      handles.Add((1000 + (i % 7), (ushort)(i % 12)));

    var samples = WindowsObjectTally.SampleTypes(Table([.. handles]));
    Assert.That(samples, Has.Count.EqualTo(12));
    Assert.That(samples.Select(sample => sample.Index), Is.Unique);
    // The sample has to carry the owner too: a handle value only means anything in the process that
    // holds it, so naming the type means duplicating it out of that process first.
    foreach (var sample in samples)
      Assert.That(sample.Pid, Is.InRange(1000, 1006));
  }

  /// <summary>
  /// The kernel's own words for the types, which are not the words user mode uses for two of them.
  /// </summary>
  /// <remarks>
  /// A mutex is a "Mutant" to the object manager and a registry key is a "Key". Matching on "Mutex"
  /// would leave that column permanently unfilled on every Windows there is, and nothing on this
  /// machine could ever show it.
  /// </remarks>
  [Test]
  public void TheKernelsOwnTypeNamesAreWhatIsMatched() {
    var types = ObjectTypeIndices.None
      .With("Event", 16)
      .With("Semaphore", 17)
      .With("Mutant", 18)
      .With("Section", 43)
      .With("Key", 37)
      .With("File", 44)
      .With("Mutex", 90);

    Assert.That(types.Event, Is.EqualTo((ushort)16));
    Assert.That(types.Semaphore, Is.EqualTo((ushort)17));
    Assert.That(types.Mutant, Is.EqualTo((ushort)18), "the object manager calls a mutex a mutant");
    Assert.That(types.Section, Is.EqualTo((ushort)43));
    Assert.That(types.Key, Is.EqualTo((ushort)37));
    Assert.That(types.IsEmpty, Is.False);
    Assert.That(ObjectTypeIndices.None.IsEmpty, Is.True);
  }

  /// <summary>
  /// A type index nothing identified must leave its column missing, not nought.
  /// </summary>
  /// <remarks>
  /// The indices are discovered by duplicating a handle and asking what it is, and a machine where
  /// every semaphore is held by a process this user may not open leaves the semaphore index unknown.
  /// A nought there would report every process as holding no semaphores, which is a finding, out of
  /// an absence — the exact thing §5.3 exists to stop.
  /// </remarks>
  [Test]
  public void AnUnidentifiedTypeIsMissingRatherThanNought() {
    var partial = ObjectTypeIndices.None.With("Event", 16);
    var tallies = new Dictionary<int, ObjectTally>();
    WindowsObjectTally.Tally(Table((1234, 16), (1234, 18)), in partial, tallies);

    Assert.That(tallies[1234].Events, Is.EqualTo(1u));
    // The mutant handle was in the table and was not counted, because nothing knew that index 18
    // meant mutants. The probe therefore reports the mutex column as unknown for every row.
    Assert.That(tallies[1234].Mutexes, Is.EqualTo(0u));
    Assert.That(partial.Mutant, Is.EqualTo(ObjectTypeIndices.Unknown));
  }

  /// <summary>The columns themselves: a real nought reads as one, and an unknown does not.</summary>
  [Test]
  public void TheColumnsTellANoughtFromAnUnknown() {
    var counted = new ProcessRecord {
      EventObjectCount = Counter.Of(0ul),
      RegistryKeyCount = Counter.Of(1_234ul),
      UserObjectCount = Counter.Of(0ul),
      GdiObjectCount = Counter.Of(97ul),
    };

    Assert.That(FieldAccessor.Text(ProcessField.EventObjectCount, in counted, null, 0), Is.EqualTo("0"));
    Assert.That(FieldAccessor.Number(ProcessField.EventObjectCount, in counted, null, 0), Is.EqualTo(0d));
    Assert.That(FieldAccessor.Number(ProcessField.RegistryKeyCount, in counted, null, 0), Is.EqualTo(1234d));
    Assert.That(FieldAccessor.Text(ProcessField.UserObjectCount, in counted, null, 0), Is.EqualTo("0"));
    Assert.That(FieldAccessor.Number(ProcessField.GdiObjectCount, in counted, null, 0), Is.EqualTo(97d));

    var unasked = new ProcessRecord {
      EventObjectCount = Counter.NotSampledYet,
      UserObjectCount = Counter.NotPermitted,
    };

    Assert.That(
      FieldAccessor.Text(ProcessField.EventObjectCount, in unasked, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet))
    );

    Assert.That(FieldAccessor.Number(ProcessField.EventObjectCount, in unasked, null, 0), Is.Null);
    Assert.That(
      FieldAccessor.Text(ProcessField.UserObjectCount, in unasked, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotPermitted))
    );
  }

}
