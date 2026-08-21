using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The three packaging systems that live beside the distribution's own: Flatpak, Snap and AppImage
/// (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// None of them is in the package manager's database, and each says who it is somewhere else: a
/// Flatpak in a file inside its own sandbox, a snap in the name of the cgroup <c>snapd</c> confined
/// it to, an AppImage in the mount its own runtime made for it. So this is three small readings
/// rather than one, and each says which evidence it stands on.
/// </para>
/// <para>
/// The evidence is not equally reachable. <c>/proc/[pid]/cgroup</c> is world-readable, so the snap
/// answer is available for anybody's process; <c>/proc/[pid]/root</c> and <c>/proc/[pid]/environ</c>
/// are behind the kernel's <c>PTRACE_MODE_READ_FSCREDS</c> check, so the Flatpak and AppImage
/// answers are available for this user's processes and are honestly unknown for everybody else's
/// rather than absent (PRD §72.3, <c>proc_pid_root(5)</c>).
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2). The
/// machine this was written on runs neither Flatpak nor Snap, which is exactly why the shapes below
/// come from upstream — flatpak's <c>flatpak-metadata(5)</c> and snapd's own
/// <c>sandbox/cgroup/tracking.go</c> — and are replayed from recordings rather than from memory.
/// </para>
/// </remarks>
public static class SandboxPackaging {

  /// <summary>
  /// The application id and branch out of a <c>.flatpak-info</c>.
  /// </summary>
  /// <remarks>
  /// <c>flatpak-metadata(5)</c>: "A file describing effective configuration is accessible inside the
  /// sandbox at <c>/.flatpak-info</c>". It is keyfile syntax; <c>[Application] name</c> is the
  /// application id and is documented as mandatory, and the <c>[Instance]</c> group is the part
  /// flatpak itself fills in for a running app — which is why <c>branch</c> is read from there and
  /// not from the metadata the author wrote.
  /// </remarks>
  /// <param name="info">The whole file.</param>
  public static PackageIdentity ReadFlatpakInfo(ReadOnlySpan<byte> info) {
    string? id = null, branch = null;
    var group = Group.None;

    var scanner = new AsciiScanner(info);
    while (!scanner.IsEmpty) {
      var line = Trim(scanner.NextLine());
      if (line.IsEmpty || line[0] == (byte)'#')
        continue;

      if (line[0] == (byte)'[') {
        group = line.SequenceEqual("[Application]"u8) ? Group.Application
          : line.SequenceEqual("[Instance]"u8) ? Group.Instance
          // A [Runtime] section instead of an [Application] one is a runtime rather than an app —
          // the name still identifies it, and calling it the application would be a small lie.
          : line.SequenceEqual("[Runtime]"u8) ? Group.Application
          : Group.None;

        continue;
      }

      var equals = line.IndexOf((byte)'=');
      if (equals <= 0)
        continue;

      var key = line[..equals];
      var value = line[(equals + 1)..];
      if (value.IsEmpty)
        continue;

      switch (group) {
        case Group.Application when key.SequenceEqual("name"u8):
          id = Encoding.UTF8.GetString(value);
          break;
        case Group.Instance when key.SequenceEqual("branch"u8):
          branch = Encoding.UTF8.GetString(value);
          break;
      }
    }

    if (id is not { Length: > 0 })
      return PackageIdentity.Unknown(UnknownReason.CounterInvalid);

    // The branch is the version somebody can act on — "stable", "45". The runtime the app was built
    // against is in the same file and is deliberately not read here: it is what the application runs
    // on rather than what the application is, and putting it in the version column would answer a
    // question nobody asked with a number that looks like one they did (§5.3).
    return new(PackageSource.Flatpak, id, branch, id, UnknownReason.None);
  }

  private enum Group : byte { None, Application, Instance }

  /// <summary>
  /// The snap behind a cgroup path, or nothing when the path names no snap.
  /// </summary>
  /// <remarks>
  /// <para>
  /// snapd puts each launched application in a transient scope named after its security tag:
  /// <c>snap.&lt;snap&gt;.&lt;app&gt;-&lt;uuid&gt;.scope</c>, built in
  /// <c>sandbox/cgroup/tracking.go</c> as <c>fmt.Sprintf("%s-%s.scope", securityTagUnitName, uuid)</c>
  /// with the example <c>snap.hello-world.sh-4706fe54-7802-4808-aa7e-ae8b567239e0.scope</c> in the
  /// comment beside it. The UUID is joined with a hyphen and not with a third dot, which matters:
  /// splitting on dots would report the application name as <c>sh-4706fe54</c>.
  /// </para>
  /// <para>
  /// A snap name may contain hyphens itself — <c>hello-world</c> does — so the UUID is found from
  /// the right and by its shape rather than by cutting at the first hyphen.
  /// </para>
  /// </remarks>
  public static PackageIdentity ReadSnapCgroup(string? cgroupPath) {
    if (cgroupPath is not { Length: > 0 })
      return PackageIdentity.Unknown(UnknownReason.NotPermitted);

    var span = cgroupPath.AsSpan();
    var slash = span.LastIndexOf('/');
    var leaf = slash < 0 ? span : span[(slash + 1)..];
    if (!leaf.StartsWith("snap.", StringComparison.Ordinal) || !leaf.EndsWith(".scope", StringComparison.Ordinal))
      return PackageIdentity.NotPackaged;

    var tag = leaf[5..^6];
    // The 36-character UUID and the hyphen before it, when there is one. A scope written without a
    // UUID — snapd's older shape, and what a hand-made unit looks like — keeps the whole tag.
    if (tag.Length > 37 && tag[^37] == '-' && IsUuid(tag[^36..]))
      tag = tag[..^37];

    var dot = tag.IndexOf('.');
    var snap = dot < 0 ? tag : tag[..dot];
    var application = dot < 0 ? default : tag[(dot + 1)..];
    if (snap.IsEmpty)
      return PackageIdentity.NotPackaged;

    return new(
      PackageSource.Snap,
      snap.ToString(),
      null,
      // The application id of a snap is the security tag without the instance: two applications of
      // one snap are two ids, which is what somebody looking at two rows needs to tell them apart.
      application.IsEmpty ? snap.ToString() : $"{snap}.{application}",
      UnknownReason.None
    );
  }

  /// <summary>
  /// The Flatpak behind a cgroup path.
  /// </summary>
  /// <remarks>
  /// Second-best evidence, and used only when the sandbox's own <c>.flatpak-info</c> is out of
  /// reach — another user's process, where the kernel's ptrace check refuses the root link. Flatpak
  /// documents the shape as <c>app-flatpak-&lt;app-id&gt;-&lt;suffix&gt;.scope</c>; unlike the snap
  /// shape it is a documented convention rather than a format string in the source, so an id read
  /// this way is treated as an id and never as proof of a version.
  /// </remarks>
  public static PackageIdentity ReadFlatpakCgroup(string? cgroupPath) {
    if (cgroupPath is not { Length: > 0 })
      return PackageIdentity.Unknown(UnknownReason.NotPermitted);

    var span = cgroupPath.AsSpan();
    var slash = span.LastIndexOf('/');
    var leaf = slash < 0 ? span : span[(slash + 1)..];
    if (!leaf.StartsWith("app-flatpak-", StringComparison.Ordinal))
      return PackageIdentity.NotPackaged;

    var body = leaf[12..];
    foreach (var suffix in (ReadOnlySpan<string>)[".scope", ".service"])
      if (body.EndsWith(suffix, StringComparison.Ordinal)) {
        body = body[..^suffix.Length];
        break;
      }

    // "-<pid>" or "-<random>" trails the id. An application id is dotted and the suffix is not, so
    // the last hyphenated part is dropped only when it carries no dot of its own.
    var hyphen = body.LastIndexOf('-');
    if (hyphen > 0 && !body[(hyphen + 1)..].Contains('.'))
      body = body[..hyphen];

    return body.IsEmpty
      ? PackageIdentity.NotPackaged
      : new(PackageSource.Flatpak, body.ToString(), null, body.ToString(), UnknownReason.None);
  }

  /// <summary>
  /// Whether an image is running out of an AppImage's own mount, and what the AppImage is called.
  /// </summary>
  /// <remarks>
  /// <para>
  /// AppImageKit's runtime builds its mount point as <c>&lt;tmp&gt;/.mount_&lt;name&gt;XXXXXX</c> and
  /// hands it to <c>mkdtemp(3)</c>, which replaces the six X's with random characters
  /// (<c>src/runtime.c</c>). So the name is recoverable from the directory, less those six
  /// characters — and it is recoverable exactly when the AppImage ran that way. Told to extract
  /// itself instead, the runtime unpacks into <c>/tmp/appimage_extracted_&lt;hash&gt;</c>, where the
  /// name is not in the path at all and this says so rather than inventing one.
  /// </para>
  /// <para>
  /// <paramref name="appImagePath"/> is the <c>APPIMAGE</c> variable the runtime sets, when the
  /// environment could be read: the absolute path of the file itself, which beats deriving a name
  /// from a directory. Reading it needs the same ptrace-mode access as the executable link, so it is
  /// there for this user's processes and not for anybody else's.
  /// </para>
  /// </remarks>
  public static PackageIdentity ReadAppImage(string? imagePath, string? appImagePath) {
    if (appImagePath is { Length: > 0 })
      return new(PackageSource.AppImage, FileName(appImagePath), null, null, UnknownReason.None);

    if (imagePath is not { Length: > 0 })
      return PackageIdentity.NotPackaged;

    var span = imagePath.AsSpan();
    var marker = span.IndexOf("/.mount_".AsSpan(), StringComparison.Ordinal);
    if (marker >= 0) {
      var rest = span[(marker + 8)..];
      var end = rest.IndexOf('/');
      var directory = end < 0 ? rest : rest[..end];
      // Six random characters, and nothing else known about them. A directory too short to have a
      // name under them is an AppImage whose name is not recoverable, which is still an AppImage.
      var name = directory.Length > 6 ? directory[..^6] : default;
      return new(PackageSource.AppImage, name.IsEmpty ? null : name.ToString(), null, null, UnknownReason.None);
    }

    return span.Contains("/appimage_extracted_".AsSpan(), StringComparison.Ordinal)
      ? new(PackageSource.AppImage, null, null, null, UnknownReason.None)
      : PackageIdentity.NotPackaged;
  }

  /// <summary>
  /// One variable out of a <c>/proc/[pid]/environ</c> block, which is NUL-separated.
  /// </summary>
  /// <remarks>
  /// The environment is what a program was started with and a program may have changed it since, so
  /// this is evidence and not proof — which is why it is only ever used to confirm a mount that has
  /// already been recognised for what it is.
  /// </remarks>
  public static string? ReadEnvironmentVariable(ReadOnlySpan<byte> environ, ReadOnlySpan<byte> name) {
    while (!environ.IsEmpty) {
      var nul = environ.IndexOf((byte)0);
      var entry = nul < 0 ? environ : environ[..nul];
      environ = nul < 0 ? default : environ[(nul + 1)..];
      if (entry.Length <= name.Length || entry[name.Length] != (byte)'=')
        continue;

      if (entry[..name.Length].SequenceEqual(name))
        return Encoding.UTF8.GetString(entry[(name.Length + 1)..]);
    }

    return null;
  }

  private static bool IsUuid(ReadOnlySpan<char> text) {
    for (var i = 0; i < text.Length; ++i) {
      var c = text[i];
      var expected = i is 8 or 13 or 18 or 23;
      if (expected != (c == '-'))
        return false;

      if (!expected && !char.IsAsciiHexDigit(c))
        return false;
    }

    return true;
  }

  private static string FileName(string path) {
    var slash = path.LastIndexOf('/');
    return slash < 0 ? path : path[(slash + 1)..];
  }

  private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line) {
    while (!line.IsEmpty && (line[^1] == (byte)'\r' || line[^1] == (byte)' '))
      line = line[..^1];

    while (!line.IsEmpty && line[0] == (byte)' ')
      line = line[1..];

    return line;
  }

}
