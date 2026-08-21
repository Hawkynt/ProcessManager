using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// The machine's own <c>/etc/services</c> (PRD §40).
/// </summary>
/// <remarks>
/// Read once and kept: the file changes about as often as the machine is rebuilt, and re-reading it
/// per frame would be a syscall for a fact that cannot have moved. A machine without one — a
/// container built from nothing much — names no ports, which is the honest answer rather than a
/// compiled-in table that would be wrong about this machine specifically.
/// </remarks>
public static class ServiceNameReader {

  private static ServiceNames? _cached;

  public const string DefaultPath = "/etc/services";

  public static ServiceNames Read(string path = DefaultPath) {
    if (_cached is { } already && path == DefaultPath)
      return already;

    var names = ServiceNames.Empty;
    try {
      if (File.Exists(path))
        names = ServiceNames.Parse(File.ReadAllText(path));
    } catch (IOException) {
      // A machine that will not let this be read names no ports. It is not worth an error: the
      // numbers are still there, and they were always the fact underneath the name.
    } catch (UnauthorizedAccessException) {
    }

    if (path == DefaultPath)
      _cached = names;

    return names;
  }

}
