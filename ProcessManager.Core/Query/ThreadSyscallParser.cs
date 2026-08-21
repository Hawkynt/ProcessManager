using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/[pid]/task/[tid]/syscall</c> — what a thread is in the middle of, and the two registers
/// that go with it (PRD §29, §30).
/// </summary>
/// <remarks>
/// <para>
/// The kernel writes one of three lines. <c>running</c> means the task is on a processor and there
/// is no consistent register state to hand out. A negative call number means the task is in the
/// kernel without being in a system call, and is followed by the stack pointer and the program
/// counter. Anything else is a call number, its six arguments, and then those same two registers.
/// </para>
/// <para>
/// This is the only file on Linux that names another thread's user-space program counter —
/// <c>stat</c>'s <c>kstkeip</c> has read zero for every task that is not core-dumping since 4.9 — and
/// it is gated on <c>PTRACE_MODE_ATTACH</c>, which owning the process does not grant under the
/// default <c>yama/ptrace_scope</c>. On most machines the caller never gets this far.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ThreadSyscallParser {

  /// <summary>The seven fields between the call number and the stack pointer of a full line.</summary>
  private const int _ArgumentCount = 6;

  /// <summary>Parses one thread's <c>syscall</c>, or says the line was not one of the three shapes.</summary>
  public static ThreadSyscall Parse(ReadOnlySpan<byte> content) {
    var scanner = new AsciiScanner(content);
    var first = scanner.NextField();
    if (first.IsEmpty)
      return ThreadSyscall.Unreadable(UnknownReason.CounterInvalid);

    if (first.SequenceEqual("running"u8))
      return ThreadSyscall.Running;

    // A thread inside the kernel and not inside a system call: the kernel writes -1 and then the two
    // registers. -1 is not a call number that could be looked up, so the number is a hole with a
    // reason rather than a negative that would render as 18446744073709551615.
    if (first[0] == (byte)'-') {
      var stackPointer = ParseAddress(scanner.NextField());
      var instructionPointer = ParseAddress(scanner.NextField());
      return new(ThreadMode.Kernel, Counter.NotSupported, stackPointer, instructionPointer);
    }

    var number = AsciiScanner.ParseUInt64(first);
    for (var i = 0; i < _ArgumentCount; ++i)
      scanner.NextField();

    var sp = ParseAddress(scanner.NextField());
    var pc = ParseAddress(scanner.NextField());
    return new(ThreadMode.Kernel, Counter.Of(number), sp, pc);
  }

  /// <summary>
  /// One <c>0x…</c> field.
  /// </summary>
  /// <remarks>
  /// The prefix has to be skipped by hand: every other hexadecimal field under <c>/proc</c> is
  /// written without one, so the shared scanner stops at the <c>x</c> and would read every address
  /// on this line as zero — which is an address, and the exact shape of mistake §72.3 is about.
  /// </remarks>
  private static Counter ParseAddress(ReadOnlySpan<byte> field) {
    if (field.IsEmpty)
      return Counter.Unknown(UnknownReason.CounterInvalid);

    if (field.Length > 2 && field[0] == (byte)'0' && (field[1] | 0x20) == (byte)'x')
      field = field[2..];

    return Counter.Of(AsciiScanner.ParseHex(field));
  }

}
