using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The three per-thread files §29 and §30 added: <c>schedstat</c>, <c>syscall</c> and <c>stack</c>.
/// </summary>
/// <remarks>
/// Parsers only, so this runs on every CI leg — which is the point of keeping them in Core with no
/// platform attribute (PRD §9.2).
/// </remarks>
[TestFixture]
public sealed class ThreadFileParserTests {

  private static ReadOnlySpan<byte> Bytes(string text) => Encoding.UTF8.GetBytes(text);

  #region schedstat

  [Test]
  public void SchedStatIsRunTimeThenQueuedTimeThenTimeslices() {
    var parsed = ThreadSchedStatParser.Parse(Bytes("10209178470013 3686926799 25527182\n"));

    Assert.That(parsed.RunNs.Value, Is.EqualTo(10_209_178_470_013ul));
    Assert.That(parsed.QueuedNs.Value, Is.EqualTo(3_686_926_799ul));
    Assert.That(parsed.Timeslices.Value, Is.EqualTo(25_527_182ul));
  }

  /// <summary>
  /// A kernel booted with <c>schedstats=disable</c> keeps the file and writes literal zeroes into it.
  /// Taken at face value that is a thread which has never been given a processor, which is not
  /// something that can be true of a thread there is a file to read — so all three zeroes together
  /// are the switch being off rather than three measurements (PRD §72.3).
  /// </summary>
  [Test]
  public void AllThreeZeroesMeanTheSchedulerStatisticsAreSwitchedOff() {
    var parsed = ThreadSchedStatParser.Parse(Bytes("0 0 0\n"));

    Assert.That(parsed.QueuedNs.HasValue, Is.False);
    Assert.That(parsed.QueuedNs.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(parsed.RunNs.HasValue, Is.False);
    Assert.That(parsed.Timeslices.HasValue, Is.False);
  }

  /// <summary>
  /// Only all three. A thread that has genuinely never had to wait writes a real run time beside a
  /// zero delay, and that zero is a reading worth having rather than a hole.
  /// </summary>
  [Test]
  public void AZeroDelayBesideARealRunTimeIsAReading() {
    var parsed = ThreadSchedStatParser.Parse(Bytes("203398 0 1\n"));

    Assert.That(parsed.QueuedNs.HasValue, Is.True);
    Assert.That(parsed.QueuedNs.Value, Is.EqualTo(0ul), "this thread ran the moment it was ready");
    Assert.That(parsed.RunNs.Value, Is.EqualTo(203_398ul));
  }

  [Test]
  public void AShortSchedStatLineIsNotThreeMeasurements() {
    var parsed = ThreadSchedStatParser.Parse(Bytes("12345\n"));

    Assert.That(parsed.RunNs.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  #endregion

  #region syscall

  /// <summary>
  /// A task on a processor: the kernel will not stop it to take a consistent register snapshot, so
  /// the mode is an answer and the three numbers are holes.
  /// </summary>
  [Test]
  public void ARunningThreadIsInUserCodeWithNoRegistersToShow() {
    var parsed = ThreadSyscallParser.Parse(Bytes("running\n"));

    Assert.That(parsed.Mode, Is.EqualTo(ThreadMode.User));
    Assert.That(parsed.InstructionPointer.HasValue, Is.False);
    Assert.That(parsed.InstructionPointer.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(parsed.Number.HasValue, Is.False, "it is not in a system call");
  }

  /// <summary>
  /// The stack pointer and the program counter are the last two of nine fields, behind six arguments
  /// nothing here reads. Counting to the wrong one lands on an argument, which is a plausible-looking
  /// address — so the guard is a line whose arguments are deliberately unlike its registers.
  /// </summary>
  [Test]
  public void TheRegistersAreTheLastTwoFieldsAndNotAnArgument() {
    var parsed = ThreadSyscallParser.Parse(
      Bytes("202 0x1 0x2 0x3 0x4 0x5 0x6 0x7ffd0001f000 0x7f1000012345\n")
    );

    Assert.That(parsed.Mode, Is.EqualTo(ThreadMode.Kernel));
    Assert.That(parsed.Number.Value, Is.EqualTo(202ul));
    Assert.That(parsed.StackPointer.Value, Is.EqualTo(0x7ffd0001f000ul));
    Assert.That(parsed.InstructionPointer.Value, Is.EqualTo(0x7f1000012345ul));
  }

  /// <summary>
  /// In the kernel and not in a system call. The kernel writes -1 and then two registers rather than
  /// eight, and -1 is not a call number anybody could look up — so it is a hole with a reason and not
  /// a negative that would render as eighteen quintillion.
  /// </summary>
  [Test]
  public void MinusOneMeansInTheKernelWithoutBeingInACall() {
    var parsed = ThreadSyscallParser.Parse(Bytes("-1 0x7ffd0001e000 0x7f1000009999\n"));

    Assert.That(parsed.Mode, Is.EqualTo(ThreadMode.Kernel));
    Assert.That(parsed.Number.HasValue, Is.False);
    Assert.That(parsed.StackPointer.Value, Is.EqualTo(0x7ffd0001e000ul));
    Assert.That(parsed.InstructionPointer.Value, Is.EqualTo(0x7f1000009999ul));
  }

  /// <summary>
  /// Every other hexadecimal field under <c>/proc</c> is written without a prefix, so the shared
  /// scanner stops at the <c>x</c>. Reading these through it gives zero for every address on the
  /// line — and zero is an address (PRD §72.3).
  /// </summary>
  [Test]
  public void TheZeroXPrefixIsSkippedRatherThanParsedAsAZero() {
    var parsed = ThreadSyscallParser.Parse(Bytes("-1 0x0 0xdeadBEEF\n"));

    Assert.That(parsed.InstructionPointer.Value, Is.EqualTo(0xdeadbeefUL), "and case does not matter");
    Assert.That(parsed.StackPointer.Value, Is.EqualTo(0ul), "0x0 really is zero, and says so as a value");
  }

  [Test]
  public void AnUnreadableSyscallFileCarriesItsReasonIntoEveryField() {
    var parsed = ThreadSyscall.Unreadable(UnknownReason.NotPermitted);

    Assert.That(parsed.Mode, Is.EqualTo(ThreadMode.Unknown));
    Assert.That(parsed.Number.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(parsed.StackPointer.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(parsed.InstructionPointer.Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  #endregion

  #region kernel stack

  [Test]
  public void EveryLineOfAKernelStackBecomesAFrame() {
    var frames = KernelStackParser.Parse(Bytes(
      "[<0>] futex_wait_queue_me+0x60/0xc0\n"
      + "[<0>] futex_wait+0x1e0/0x2c0\n"
      + "[<0>] do_syscall_64+0x5c/0x90\n"
    ));

    Assert.That(frames, Has.Count.EqualTo(3));
    Assert.That(frames[0].Symbol, Is.EqualTo("futex_wait_queue_me"));
    Assert.That(frames[0].Displacement.Value, Is.EqualTo(0x60ul));
    Assert.That(frames[0].Index, Is.EqualTo(0));
    Assert.That(frames[2].Index, Is.EqualTo(2));
    foreach (var frame in frames)
      Assert.That(frame.Kind, Is.EqualTo(FrameKind.Kernel));
  }

  /// <summary>
  /// <c>kernel.kptr_restrict</c> is set nearly everywhere, and the kernel then writes <c>[&lt;0&gt;]</c>
  /// for every frame. Zero is an address, so a column of <c>0x0</c> would read as a stack that is
  /// entirely at the null page — the reason has to travel instead (PRD §3.4).
  /// </summary>
  [Test]
  public void ARestrictedAddressIsARefusalRatherThanTheAddressZero() {
    var frames = KernelStackParser.Parse(Bytes("[<0>] schedule+0x2e/0xd0\n"));

    Assert.That(frames[0].Address.HasValue, Is.False);
    Assert.That(frames[0].Address.Reason, Is.EqualTo(UnknownReason.NotPermitted));
    Assert.That(frames[0].Symbol, Is.EqualTo("schedule"));
  }

  [Test]
  public void AnUnrestrictedAddressIsRead() {
    var frames = KernelStackParser.Parse(Bytes("[<ffffffff810b2a30>] do_wait+0x1e5/0x220\n"));

    Assert.That(frames[0].Address.Value, Is.EqualTo(0xffffffff810b2a30ul));
    Assert.That(frames[0].Symbol, Is.EqualTo("do_wait"));
    Assert.That(frames[0].Displacement.Value, Is.EqualTo(0x1e5ul));
  }

  /// <summary>
  /// A symbol out of a loadable module carries the module's name in trailing brackets. Leaving it on
  /// the symbol would produce a function called <c>nvidia_open [nvidia]</c>, which no symbol table
  /// contains.
  /// </summary>
  [Test]
  public void AModuleSymbolKeepsTheModuleOutOfItsName() {
    var frames = KernelStackParser.Parse(Bytes("[<0>] nvidia_open+0x40/0x180 [nvidia]\n"));

    Assert.That(frames[0].Symbol, Is.EqualTo("nvidia_open"));
    Assert.That(frames[0].Module, Is.EqualTo("nvidia"));
    Assert.That(frames[0].Displacement.Value, Is.EqualTo(0x40ul));
  }

  /// <summary>
  /// A frame with no <c>+0x</c> has no displacement, and zero would claim the address is the first
  /// instruction of the function.
  /// </summary>
  [Test]
  public void AFrameWithoutAnOffsetHasNoDisplacementRatherThanZero() {
    var frames = KernelStackParser.Parse(Bytes("[<0>] entry_SYSCALL_64_after_hwframe\n"));

    Assert.That(frames[0].Symbol, Is.EqualTo("entry_SYSCALL_64_after_hwframe"));
    Assert.That(frames[0].Displacement.HasValue, Is.False);
  }

  /// <summary>
  /// §30 keeps the source columns and leaves them empty, because this build reads no DWARF. A column
  /// that is absent is a requirement quietly dropped; a column that says it has nothing is a fact.
  /// </summary>
  [Test]
  public void NoFrameClaimsASourceFile() {
    var frames = KernelStackParser.Parse(Bytes("[<0>] schedule+0x2e/0xd0\n"));

    Assert.That(frames[0].SourceFile, Is.Null);
    Assert.That(frames[0].SourceLine, Is.EqualTo(0));
  }

  [Test]
  public void BlankLinesAreNotFrames() {
    var frames = KernelStackParser.Parse(Bytes("\n[<0>] schedule+0x2e/0xd0\n\n"));

    Assert.That(frames, Has.Count.EqualTo(1));
  }

  #endregion

}
