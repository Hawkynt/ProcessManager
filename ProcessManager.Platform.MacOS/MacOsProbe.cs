using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.MacOS;

/// <summary>
/// The macOS probe, which does not exist yet (PRD §5.3, §10 M9).
/// </summary>
/// <remarks>
/// Every member throws. It deliberately does not return an empty process list: a program that comes
/// up showing nothing looks broken in a way nobody can diagnose, where one that says "this platform
/// is not implemented" says exactly what is wrong and where the work is tracked. The real
/// implementation is <c>proc_listpids</c> / <c>proc_pidinfo</c> over <c>libproc</c> plus
/// <c>sysctl</c> for the machine-wide counters.
/// </remarks>
public sealed class MacOsProbe : ISystemProbe {

  private const string _Message =
    "The macOS probe is not implemented (PRD §5.3, milestone M9). Windows and Linux are supported.";

  public string Description => "macos:not-implemented";

  public void Sample(SystemSnapshot snapshot) => throw new PlatformNotSupportedException(_Message);

  public Counter GetHandleCount(ProcessKey key) => throw new PlatformNotSupportedException(_Message);

  public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => throw new PlatformNotSupportedException(_Message);

  public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => throw new PlatformNotSupportedException(_Message);

  public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => throw new PlatformNotSupportedException(_Message);

  public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => throw new PlatformNotSupportedException(_Message);

  public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key)
    => throw new PlatformNotSupportedException(_Message);

  public void Dispose() { }

}
