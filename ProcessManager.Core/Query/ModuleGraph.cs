using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Why each mapped image is in the process, worked out from what the images say about each other
/// (PRD §31).
/// </summary>
/// <remarks>
/// <para>
/// Windows keeps a load reason per module and hands it out. Linux keeps one too — the loader's
/// <c>link_map</c> has an open count — and hands it to nobody: there is no file under
/// <c>/proc/[pid]</c> that says which library was named by the program and which was opened by hand.
/// So the answer is derived instead, from the two things the files themselves declare: the program
/// names its interpreter in <c>PT_INTERP</c> and its libraries in <c>DT_NEEDED</c>, and every
/// library does the same for its own.
/// </para>
/// <para>
/// That derivation is sound in one direction only, and the class is honest about which. An image
/// that something names <em>is</em> a dependency. An image that nothing names may have been
/// <c>dlopen</c>ed, may have come from <c>LD_PRELOAD</c>, or may be named by a library whose own
/// headers this user was not allowed to read — so it reports
/// <see cref="ModuleLoadReason.RunTime"/>, whose documented meaning is "nothing that could be read
/// names this" and not "somebody called <c>dlopen</c>" (PRD §5.3, §72.3).
/// </para>
/// <para>
/// No platform attribute and no file access: it is handed the descriptions and the paths, so it runs
/// on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ModuleGraph {

  /// <summary>
  /// Fills in <see cref="ModuleRecord.LoadReason"/> for a whole modules list.
  /// </summary>
  /// <param name="modules">The rows, in the order <see cref="MapsParser"/> produced them.</param>
  /// <param name="descriptions">
  /// What each row's file declared, one per row and in the same order. A row whose file could not be
  /// read carries <see cref="ElfImage.Unread"/>, which names nothing and is named by nothing.
  /// </param>
  /// <param name="imagePath">
  /// What <c>/proc/[pid]/exe</c> points at, or null. Only a hint: an executable is recognised by
  /// declaring an interpreter, which no library does, and this settles the tie when a process has
  /// mapped somebody else's program as well as its own.
  /// </param>
  public static void Assign(
    List<ModuleRecord> modules,
    IReadOnlyList<ElfImage.Description> descriptions,
    string? imagePath = null
  ) {
    ArgumentNullException.ThrowIfNull(modules);
    ArgumentNullException.ThrowIfNull(descriptions);

    if (modules.Count != descriptions.Count)
      throw new ArgumentException("There must be one description per module.", nameof(descriptions));

    var image = FindImage(modules, descriptions, Undeleted(imagePath));
    var interpreter = image >= 0 ? descriptions[image].Interpreter : null;

    // Named by the program, and named by anything else. The two sets are kept apart because the
    // difference is the whole point: a library the program itself asks for is part of what it is,
    // and one pulled in three levels down is part of what it happens to use.
    var direct = new HashSet<string>(StringComparer.Ordinal);
    var indirect = new HashSet<string>(StringComparer.Ordinal);
    for (var i = 0; i < descriptions.Count; ++i) {
      var into = i == image ? direct : indirect;
      var needed = descriptions[i].Needed;
      for (var n = 0; n < needed.Count; ++n)
        into.Add(needed[n]);
    }

    for (var i = 0; i < modules.Count; ++i)
      modules[i] = modules[i] with { LoadReason = Reason(modules[i], descriptions[i]) };

    ModuleLoadReason Reason(in ModuleRecord module, in ElfImage.Description description) {
      if (image >= 0 && string.Equals(module.Path, modules[image].Path, StringComparison.Ordinal))
        return ModuleLoadReason.Image;

      // By path and by file name both: the program asks for /lib64/ld-linux-x86-64.so.2 and the
      // kernel maps /usr/lib/ld-linux-x86-64.so.2, which are the same loader through a symlink that
      // /proc has already resolved. Comparing only the paths reports the loader as a run-time load
      // on every distribution that puts its libraries under /usr/lib.
      if (interpreter is { Length: > 0 } && SamePath(module.Path, interpreter))
        return ModuleLoadReason.Interpreter;

      // The SONAME is what a DT_NEEDED entry holds, and the file name is what it holds when the
      // library publishes no SONAME. Neither alone answers for every library on a machine.
      var soname = description.Soname;
      var file = FileName(module.Path);
      if (Names(direct, soname, file))
        return ModuleLoadReason.Direct;

      if (Names(indirect, soname, file))
        return ModuleLoadReason.Dependency;

      return description.Type switch {
        ModuleType.Data => ModuleLoadReason.Data,
        // The headers were never read — the file is gone, or this user may not open it. Saying it
        // was loaded at run time would be a claim built on a file nobody looked at (PRD §72.3).
        ModuleType.Unknown => ModuleLoadReason.Unknown,
        _ => ModuleLoadReason.RunTime,
      };
    }
  }

  private static bool Names(HashSet<string> names, string? soname, ReadOnlySpan<char> file)
    => (soname is { Length: > 0 } && names.Contains(soname))
    || names.Contains(file.ToString());

  /// <summary>
  /// Which row is the program.
  /// </summary>
  /// <remarks>
  /// <c>PT_INTERP</c> is the test that works without being told anything: a program names the loader
  /// it wants and a shared library never does, so an image declaring an interpreter is an image
  /// somebody executed. The path from <c>/proc/[pid]/exe</c> wins where it matches, for the process
  /// that has mapped a second program's file as data.
  /// </remarks>
  private static int FindImage(
    List<ModuleRecord> modules,
    IReadOnlyList<ElfImage.Description> descriptions,
    string? imagePath
  ) {
    var byInterpreter = -1;
    for (var i = 0; i < modules.Count; ++i) {
      if (imagePath is { Length: > 0 } && string.Equals(modules[i].Path, imagePath, StringComparison.Ordinal))
        return i;

      if (byInterpreter < 0 && descriptions[i].Interpreter is { Length: > 0 })
        byInterpreter = i;
    }

    return byInterpreter;
  }

  /// <summary>The same file, allowing for the symlink <c>/proc</c> has already resolved.</summary>
  private static bool SamePath(string path, string other)
    => string.Equals(path, other, StringComparison.Ordinal)
    || FileName(path).SequenceEqual(FileName(other));

  private static ReadOnlySpan<char> FileName(string path) {
    var slash = path.LastIndexOf('/');
    return slash < 0 ? path : path.AsSpan(slash + 1);
  }

  /// <summary>
  /// <c>/proc/[pid]/exe</c> keeps pointing at a program whose file has been replaced, and says so by
  /// appending a suffix to the name — the same suffix <c>maps</c> appends, and which the map parser
  /// has already taken off the rows this is matched against.
  /// </summary>
  private static string? Undeleted(string? path) {
    const string DeletedSuffix = " (deleted)";
    return path is not null && path.EndsWith(DeletedSuffix, StringComparison.Ordinal)
      ? path[..^DeletedSuffix.Length]
      : path;
  }

}
