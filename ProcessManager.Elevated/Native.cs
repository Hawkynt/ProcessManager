using System.Runtime.InteropServices;

namespace Hawkynt.ProcessManager.Elevated;

/// <summary>
/// The three syscalls the helper is allowed to make, and nothing else.
/// </summary>
/// <remarks>
/// Deliberately its own tiny file rather than a reference to the platform assembly: what a program
/// running as root can do should be readable in one screen, and this is that screen.
/// </remarks>
internal static partial class Native {

  [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
  internal static partial int Kill(int pid, int signal);

  [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
  internal static partial int SetPriority(int which, uint who, int value);

  [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
  internal static partial int SchedSetAffinity(int pid, nuint cpuSetSize, ref ulong mask);

}
