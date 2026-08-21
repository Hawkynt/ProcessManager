using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Which managed runtime, if any, is executing inside a process (PRD §14, §80).
/// </summary>
/// <remarks>
/// <para>
/// From the module list and from nothing else. A process called <c>java</c> may be a shell script,
/// a process called <c>python3</c> may be a compiled binary that borrowed the name, and a renamed
/// process is exactly the case the identity fields of §14 exist for. What cannot be argued with is
/// that the process has <c>libjvm.so</c> mapped: a virtual machine is in there, whatever the row
/// says its name is. Guessing from a name would be the false equivalence §5.3 forbids.
/// </para>
/// <para>
/// The absence of a runtime is an answer too. A process that maps none of these is
/// <see cref="ProcessRuntime.Native"/> — machine code and a libc — and that is a finding rather
/// than a hole. Not being able to read <c>maps</c> at all is the hole, and it is a different value.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class RuntimeDetector {

  /// <summary>
  /// Reads a whole <c>maps</c> file and names the runtime in it.
  /// </summary>
  /// <remarks>
  /// Every line is looked at rather than stopping at the first match, because the first match is not
  /// the most interesting one: a .NET program that has loaded a Python extension maps both, and the
  /// answer somebody wants is the one that runs the program rather than whichever came first in the
  /// address space. <see cref="Rank"/> is that order.
  /// </remarks>
  public static ProcessRuntime Detect(ReadOnlySpan<byte> maps) {
    var found = ProcessRuntime.Native;
    var scanner = new AsciiScanner(maps);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (!MapsParser.TryParseRegion(line, out _, out var path))
        continue;

      var candidate = Classify(FileName(line[path]));
      if (Rank(candidate) > Rank(found))
        found = candidate;
    }

    return found;
  }

  /// <summary>
  /// What one mapped file says about the runtime, by its name alone.
  /// </summary>
  /// <remarks>
  /// Prefixes rather than exact names, because every one of these carries a version somewhere in it
  /// — <c>libpython3.13.so.1.0</c>, <c>libruby.so.3.4</c> — and a table of exact file names is a
  /// table that is wrong on the next release. The prefixes are anchored at the start of the file
  /// name so that a program of somebody's own called <c>mylibjvm.so</c> is not read as a JVM.
  /// </remarks>
  public static ProcessRuntime Classify(ReadOnlySpan<byte> fileName) {
    // CoreCLR is the runtime itself; the JIT and the host policy libraries sit beside it and are
    // enough on their own, because a process that has suspended between loading them is still a
    // .NET process. NativeAOT maps none of the three and is honestly native: there is no runtime in
    // it to find, which is the whole point of publishing that way.
    if (StartsWith(fileName, "libcoreclr.so"u8)
        || StartsWith(fileName, "libclrjit.so"u8)
        || StartsWith(fileName, "libhostpolicy.so"u8))
      return ProcessRuntime.DotNet;

    if (StartsWith(fileName, "libmonosgen-"u8) || StartsWith(fileName, "libmono-"u8))
      return ProcessRuntime.Mono;

    // libjvm is the virtual machine; libjli is the launcher that finds it, and a process that has
    // only the launcher mapped is still on its way to being a JVM.
    if (StartsWith(fileName, "libjvm.so"u8) || StartsWith(fileName, "libjli.so"u8))
      return ProcessRuntime.Java;

    if (StartsWith(fileName, "libpython"u8))
      return ProcessRuntime.Python;

    if (StartsWith(fileName, "libruby.so"u8))
      return ProcessRuntime.Ruby;

    if (StartsWith(fileName, "libperl.so"u8))
      return ProcessRuntime.Perl;

    if (StartsWith(fileName, "libphp"u8))
      return ProcessRuntime.Php;

    if (StartsWith(fileName, "libnode.so"u8))
      return ProcessRuntime.Node;

    // Wine is not a managed runtime and is in this list for the same reason the others are: the
    // program executing is not the one whose name the row shows. A Windows binary running here is
    // worth saying so.
    if (StartsWith(fileName, "libwine.so"u8) || StartsWith(fileName, "ntdll.so"u8))
      return ProcessRuntime.Wine;

    return ProcessRuntime.Native;
  }

  /// <summary>
  /// Which answer wins when a process maps more than one runtime.
  /// </summary>
  /// <remarks>
  /// The order is "what is running the program" first and "what the program has loaded" second. An
  /// embedding host — a .NET or Java application with a scripting engine in it — is the host, and a
  /// scripting library mapped into it does not make it a script interpreter.
  /// </remarks>
  private static int Rank(ProcessRuntime runtime) => runtime switch {
    ProcessRuntime.DotNet => 8,
    ProcessRuntime.Mono => 7,
    ProcessRuntime.Java => 6,
    ProcessRuntime.Wine => 5,
    ProcessRuntime.Node => 4,
    ProcessRuntime.Python => 3,
    ProcessRuntime.Ruby => 2,
    ProcessRuntime.Perl => 1,
    ProcessRuntime.Php => 1,
    _ => 0,
  };

  /// <summary>The last path component, and the whole thing when there is no slash in it.</summary>
  private static ReadOnlySpan<byte> FileName(ReadOnlySpan<byte> path) {
    var slash = path.LastIndexOf((byte)'/');
    return slash < 0 ? path : path[(slash + 1)..];
  }

  private static bool StartsWith(ReadOnlySpan<byte> name, ReadOnlySpan<byte> prefix)
    => name.Length >= prefix.Length && name[..prefix.Length].SequenceEqual(prefix);

}
