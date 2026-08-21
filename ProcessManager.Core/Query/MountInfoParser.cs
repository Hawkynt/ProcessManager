using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/[pid]/mountinfo</c>: what a descriptor's <c>mnt_id</c> means (PRD §32).
/// </summary>
/// <remarks>
/// <para>
/// <c>fdinfo</c> says which mount an open file's inode is on and says it as a number that means
/// nothing on its own. This file is the other half of that join: one line per mount, carrying the
/// same number, the device behind it, where it is mounted and what kind of file system it is. So
/// "descriptor 7" becomes "a file on the btrfs mounted at /", which is the answer §32 asks for and
/// the one <c>lsof</c> prints.
/// </para>
/// <para>
/// The mount table is the process's own rather than the machine's, deliberately: a process in a
/// container sees a different one, and its descriptors are on <em>its</em> mounts. Reading the
/// caller's would name mounts the process cannot see and miss the ones it is using.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class MountInfoParser {

  /// <summary>One mount, as much of it as a descriptor needs.</summary>
  /// <param name="Device">
  /// The device as <c>major:minor</c>, in the decimal notation this file writes it in — which is not
  /// the hex notation <c>maps</c> uses for the same pair. Each column keeps the spelling of the file
  /// it came from, so that either can be found in its source by eye (PRD §5.3).
  /// </param>
  public readonly record struct Mount(int Id, string Device, string MountPoint, string FileSystem);

  /// <summary>
  /// Every mount in the file, by its id.
  /// </summary>
  /// <remarks>
  /// A dictionary rather than a list because the only question ever asked of it is "what is mount
  /// 42", once per descriptor. Ids are unique within one table and are reused after a mount goes
  /// away, which is why the table is read beside the descriptors rather than cached across samples.
  /// </remarks>
  public static Dictionary<int, Mount> Collect(ReadOnlySpan<byte> content) {
    var result = new Dictionary<int, Mount>();
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (TryParse(line, out var mount))
        result[mount.Id] = mount;
    }

    return result;
  }

  /// <summary>
  /// One line: <c>36 35 98:0 / /mnt rw,noatime shared:1 - ext4 /dev/sda1 rw</c>.
  /// </summary>
  /// <remarks>
  /// The optional fields between the mount options and the file-system type are variable in number
  /// and terminated by a lone dash, which is what makes this file parseable at all: everything before
  /// the dash is counted from the left and everything after it from the dash. Counting from the right
  /// instead breaks on a source containing spaces, and there is one on every machine with a
  /// <c>systemd-nspawn</c> container on it.
  /// </remarks>
  public static bool TryParse(ReadOnlySpan<byte> line, out Mount mount) {
    mount = default;

    var scanner = new AsciiScanner(line);
    var id = scanner.NextField();
    if (!IsNumber(id))
      return false;

    var mountId = (int)AsciiScanner.ParseUInt64(id);

    scanner.NextField();
    var device = Text(scanner.NextField());
    if (device.IndexOf(':', StringComparison.Ordinal) <= 0)
      return false;

    // The root of the mount inside its own file system, which is what a bind mount makes interesting
    // and what nothing here needs.
    scanner.NextField();
    var mountPoint = Unescape(Text(scanner.NextField()));
    if (mountPoint.Length == 0)
      return false;

    // Walk to the separator. It is a field of exactly one dash, and a mount option or a tag can
    // neither be empty nor be that.
    var fileSystem = string.Empty;
    while (true) {
      var field = scanner.NextField();
      if (field.IsEmpty)
        return false;

      if (field.Length == 1 && field[0] == (byte)'-') {
        fileSystem = Text(scanner.NextField());
        break;
      }
    }

    mount = new(mountId, device, mountPoint, fileSystem);
    return fileSystem.Length > 0;
  }

  private static string Text(ReadOnlySpan<byte> field) => field.IsEmpty ? string.Empty : Encoding.UTF8.GetString(field);

  /// <summary>Digits and nothing else, so that a header or a truncated line is not read as mount 0.</summary>
  private static bool IsNumber(ReadOnlySpan<byte> field) {
    if (field.IsEmpty || field.Length > 10)
      return false;

    for (var i = 0; i < field.Length; ++i)
      if (field[i] is < (byte)'0' or > (byte)'9')
        return false;

    return true;
  }

  /// <summary>
  /// The octal escapes the kernel writes for the four characters that would otherwise split a field.
  /// </summary>
  /// <remarks>
  /// A mount point containing a space is written <c>/mnt/my\040disk</c>, and leaving it that way
  /// puts a backslash and three digits in front of the reader where a space belongs. Only the four
  /// the kernel escapes are undone — space, tab, newline and the backslash itself — because
  /// interpreting every octal escape would also rewrite a directory genuinely called <c>\101</c>.
  /// </remarks>
  private static string Unescape(string text) {
    if (!text.Contains('\\', StringComparison.Ordinal))
      return text;

    var result = new StringBuilder(text.Length);
    for (var i = 0; i < text.Length; ++i) {
      if (text[i] != '\\' || i + 3 >= text.Length) {
        result.Append(text[i]);
        continue;
      }

      var code = text.AsSpan(i + 1, 3);
      var value = (code[0] - '0') * 64 + (code[1] - '0') * 8 + (code[2] - '0');
      if (value is 0x20 or 0x09 or 0x0A or 0x5C) {
        result.Append((char)value);
        i += 3;
        continue;
      }

      result.Append(text[i]);
    }

    return result.ToString();
  }

  /// <summary>
  /// The mount a descriptor's <c>mnt_id</c> names, or null when none does.
  /// </summary>
  /// <remarks>
  /// Null is the ordinary answer for a socket, a pipe or an anonymous inode. Those live on
  /// kernel-internal file systems — <c>sockfs</c>, <c>pipefs</c>, <c>anon_inodefs</c> — which are
  /// mounted nowhere and appear in no mount table, so their ids match nothing here. That is a fact
  /// about the descriptor and not a failure of the lookup, which is why the caller renders it as
  /// "no file system" rather than as a hole (PRD §72.3).
  /// </remarks>
  public static Mount? Find(Dictionary<int, Mount> mounts, Counter mountId) {
    ArgumentNullException.ThrowIfNull(mounts);

    return mountId.TryGetValue(out var id) && id <= int.MaxValue && mounts.TryGetValue((int)id, out var mount)
      ? mount
      : null;
  }

}
