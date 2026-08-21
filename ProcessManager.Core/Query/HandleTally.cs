using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// How many descriptors of each kind one process holds (PRD §20).
/// </summary>
/// <remarks>
/// <para>
/// One pass over a list that has already been enumerated, so the tally itself costs nothing — but the
/// enumeration behind it costs 85 µs per process, which is why §20's per-type counts are not columns
/// in the process table. They are reported for the one process somebody opened, next to the
/// descriptors they were counted from, and the sample loop never learns they exist (PRD §5.4, §71.2).
/// </para>
/// <para>
/// The kinds are grouped the way §32 groups them rather than one counter per <see cref="HandleKind"/>:
/// a reader wants to know that a process holds four hundred sockets, not that six of them are epoll
/// sets.
/// </para>
/// </remarks>
public readonly record struct HandleTally(
  int Total,
  int Files,
  int Directories,
  int Sockets,
  int Pipes,
  int Devices,
  int SharedMemory,
  int EventInterfaces,
  int Other
) {

  public static HandleTally From(IReadOnlyList<HandleRecord> handles) {
    ArgumentNullException.ThrowIfNull(handles);

    int files = 0, directories = 0, sockets = 0, pipes = 0, devices = 0, shared = 0, events = 0, other = 0;
    for (var i = 0; i < handles.Count; ++i)
      switch (handles[i].Kind) {
        case HandleKind.File: ++files; break;
        case HandleKind.Directory: ++directories; break;
        case HandleKind.Socket: ++sockets; break;
        case HandleKind.Pipe: ++pipes; break;
        case HandleKind.Device: ++devices; break;
        case HandleKind.SharedMemory or HandleKind.Section: ++shared; break;
        case HandleKind.Event or HandleKind.EventPoll or HandleKind.Timer or HandleKind.Signal or HandleKind.Notify:
          ++events;
          break;
        default: ++other; break;
      }

    return new(handles.Count, files, directories, sockets, pipes, devices, shared, events, other);
  }

  /// <summary>
  /// A one-line summary, naming only the kinds that are actually present.
  /// </summary>
  /// <remarks>
  /// Zeroes are left out rather than printed: "0 pipes" is true and tells nobody anything, and eight
  /// of them push the two counts that matter off the end of the line.
  /// </remarks>
  public string Describe() {
    var text = new StringBuilder();
    text.Append(this.Total.ToString(CultureInfo.InvariantCulture)).Append(this.Total == 1 ? " descriptor" : " descriptors");

    var first = true;
    Append(this.Files, "file", "files");
    Append(this.Directories, "directory", "directories");
    Append(this.Sockets, "socket", "sockets");
    Append(this.Pipes, "pipe", "pipes");
    Append(this.Devices, "device", "devices");
    Append(this.SharedMemory, "shared mapping", "shared mappings");
    Append(this.EventInterfaces, "event interface", "event interfaces");
    Append(this.Other, "other", "other");
    return text.ToString();

    void Append(int count, string singular, string plural) {
      if (count == 0)
        return;

      text.Append(first ? " — " : ", ");
      first = false;
      text.Append(count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(count == 1 ? singular : plural);
    }
  }

}
