using Microsoft.Win32.SafeHandles;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// A file opened once and read at whatever offsets a parser asks for (PRD §31, §53).
/// </summary>
/// <remarks>
/// <para>
/// Every reader in this folder takes an <see cref="ElfImage.ElfRead"/> rather than a path, which is
/// what keeps them in Core with no platform attribute and under test on every CI leg (PRD §9.2).
/// This is the one place that turns a path into one, so that a report touching eleven pages opens
/// the file once instead of eleven times.
/// </para>
/// <para>
/// Ranges and never the whole file. A binary inspector is pointed at exactly the images that are
/// large — a runtime is three hundred megabytes and a debug build of a browser engine is more — and
/// reading one into memory to print its section table would cost a thousand times what the answer
/// is worth (PRD §5.4). <see cref="System.IO.RandomAccess"/> is a <c>pread</c>, so nothing here
/// keeps a file position and nothing is copied that was not asked for.
/// </para>
/// <para>
/// A short read is not an error. Several of the callers deliberately ask past the end of a range —
/// a NUL-terminated name has no length written anywhere — so <see cref="Read"/> reports how much it
/// got and leaves the judgement to whoever wanted the bytes.
/// </para>
/// </remarks>
public sealed class ImageBytes : IDisposable {

  private readonly SafeFileHandle _handle;

  private ImageBytes(SafeFileHandle handle, string path, long length) {
    this._handle = handle;
    this.Path = path;
    this.Length = length;
  }

  /// <summary>The path this was opened from, as it was given.</summary>
  public string Path { get; }

  /// <summary>How many bytes the file had when it was opened.</summary>
  public long Length { get; }

  /// <summary>
  /// Opens a file for reading, or says why it could not be.
  /// </summary>
  /// <param name="reason">
  /// Null on success, and otherwise the sentence a properties box shows. Distinguishing the three
  /// ways this fails matters: a file that is gone, a file this user may not read and a path that is
  /// a directory are three different things to do next, and one empty view for all of them tells
  /// somebody nothing (PRD §72.3).
  /// </param>
  public static ImageBytes? Open(string? path, out string? reason) {
    if (string.IsNullOrWhiteSpace(path)) {
      reason = "there is no path to read";
      return null;
    }

    try {
      // Shared for writing as well as reading: the image of a running process is routinely being
      // replaced by a package manager while this looks at it, and refusing to open a file because
      // somebody else has it open would fail on exactly the machines worth inspecting.
      var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      try {
        var length = RandomAccess.GetLength(handle);
        reason = null;
        return new(handle, path, length);
      } catch {
        handle.Dispose();
        throw;
      }
    } catch (UnauthorizedAccessException) {
      reason = "this file may not be read as this user";
      return null;
    } catch (FileNotFoundException) {
      reason = "there is no such file";
      return null;
    } catch (DirectoryNotFoundException) {
      reason = "there is no such file";
      return null;
    } catch (IOException e) {
      reason = e.Message;
      return null;
    }
  }

  /// <summary>
  /// Fills <paramref name="buffer"/> from <paramref name="offset"/>, and says how much it got.
  /// </summary>
  /// <remarks>
  /// Looped, because one <c>pread</c> may return less than was asked for without being at the end of
  /// anything. A caller that treated the first short read as the end of the file would truncate a
  /// symbol table on whichever file system felt like it that day.
  /// </remarks>
  public int Read(long offset, Span<byte> buffer) {
    if (offset < 0 || buffer.IsEmpty)
      return 0;

    var total = 0;
    try {
      while (total < buffer.Length) {
        var got = RandomAccess.Read(this._handle, buffer[total..], offset + total);
        if (got <= 0)
          break;

        total += got;
      }
    } catch (IOException) {
      // A device node, a file truncated under us, a network mount that went away. What was read
      // before it happened is still true, and the caller's bounds checks handle the rest.
      return total;
    } catch (ObjectDisposedException) {
      return total;
    }

    return total;
  }

  /// <summary>The same read as a delegate, which is what every parser in this folder takes.</summary>
  public ElfImage.ElfRead Reader => this.Read;

  public void Dispose() => this._handle.Dispose();

}
