using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// The native entry points the Windows probe uses.
/// </summary>
/// <remarks>
/// <c>[LibraryImport]</c> only, so the marshalling is generated at compile time and the NativeAOT
/// publish stays warning-free (PRD §2).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class Native {

  [LibraryImport("ntdll.dll")]
  internal static partial uint NtQuerySystemInformation(
    int systemInformationClass,
    [Out] byte[] systemInformation,
    int systemInformationLength,
    out int returnLength
  );

  [LibraryImport("psapi.dll", EntryPoint = "GetPerformanceInfo", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool GetPerformanceInfo(ref NtStructures.PerformanceInformation info, uint size);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool CloseHandle(nint handle);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool TerminateProcess(nint handle, uint exitCode);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool SetPriorityClass(nint handle, uint priorityClass);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool GetProcessTimes(nint handle, out long creation, out long exit, out long kernel, out long user);

  [LibraryImport("kernel32.dll", EntryPoint = "SetProcessAffinityMask", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool SetProcessAffinityMask(nint handle, nuint mask);

  [LibraryImport("ntdll.dll")]
  internal static partial uint NtSuspendProcess(nint handle);

  [LibraryImport("ntdll.dll")]
  internal static partial uint NtResumeProcess(nint handle);

  public const uint PROCESS_TERMINATE = 0x0001;
  public const uint PROCESS_SET_INFORMATION = 0x0200;
  public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
  public const uint PROCESS_SUSPEND_RESUME = 0x0800;

  public const int ERROR_ACCESS_DENIED = 5;
  public const int ERROR_INVALID_PARAMETER = 87;

  public const uint IDLE_PRIORITY_CLASS = 0x00000040;
  public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
  public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
  public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
  public const uint HIGH_PRIORITY_CLASS = 0x00000080;
  public const uint REALTIME_PRIORITY_CLASS = 0x00000100;

}
