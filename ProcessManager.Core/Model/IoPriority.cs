namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// Which scheduling class a process's disk requests belong to (PRD §26).
/// </summary>
/// <remarks>
/// The one control that makes a backup, an indexer or a compile stop making a machine unusable
/// without slowing it down much: a process left at normal CPU priority but moved to idle I/O keeps
/// running at full speed and simply yields the disk whenever anything else wants it.
/// </remarks>
public enum IoPriorityClass : byte {

  /// <summary>Nobody set one, so the kernel derives it from the CPU nice value.</summary>
  None = 0,

  /// <summary>First in the queue, always. Needs privilege, and starves everything else.</summary>
  Realtime = 1,

  /// <summary>The ordinary class, with eight levels inside it.</summary>
  BestEffort = 2,

  /// <summary>Served only when the disk is otherwise idle.</summary>
  Idle = 3,

}

/// <summary>
/// A class and, where the class has them, a level within it.
/// </summary>
/// <param name="Level">
/// 0 is the highest and 7 the lowest, which is the opposite of how most people read a number and
/// exactly how the kernel means it. Meaningless for <see cref="IoPriorityClass.Idle"/> and
/// <see cref="IoPriorityClass.None"/>.
/// </param>
public readonly record struct IoPriority(IoPriorityClass Class, int Level = 0) {

  /// <summary>The kernel packs the class into the top three bits and the level into the low
  /// thirteen; both halves come back as one integer.</summary>
  private const int _ClassShift = 13;

  private const int _LevelMask = (1 << _ClassShift) - 1;

  public static readonly IoPriority Unset = new(IoPriorityClass.None);

  /// <summary>Packs into the single integer <c>ioprio_set</c> takes.</summary>
  public int Pack() => ((int)this.Class << _ClassShift) | (this.Level & _LevelMask);

  /// <summary>
  /// Unpacks what <c>ioprio_get</c> returned.
  /// </summary>
  /// <remarks>
  /// A negative value is the syscall's error return and not a priority; the caller checks errno, and
  /// this reports that nothing is set rather than inventing a class from a bit pattern.
  /// </remarks>
  public static IoPriority Unpack(int value) {
    if (value < 0)
      return Unset;

    var kind = (value >> _ClassShift) & 0x7;
    return kind > (int)IoPriorityClass.Idle ? Unset : new((IoPriorityClass)kind, value & _LevelMask);
  }

  /// <summary>How it reads in a menu and in the detail pane.</summary>
  public override string ToString() => this.Class switch {
    IoPriorityClass.Realtime => $"real-time {this.Level}",
    IoPriorityClass.BestEffort => $"best effort {this.Level}",
    IoPriorityClass.Idle => "idle",
    _ => "default",
  };

}
