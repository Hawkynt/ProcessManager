using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// How many of each kind of thing a process holds open (PRD §20).
/// </summary>
/// <remarks>
/// <para>
/// One pass over the descriptor table that the handle view of §32 already walks, and it goes
/// through the same <see cref="DescriptorParser.Classify"/> — so the column and the view cannot
/// disagree about what a socket is (PRD §5.1).
/// </para>
/// <para>
/// A struct with no allocation of its own, because the caller builds one per process per sample.
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public struct DescriptorTally {

  private uint _sockets;
  private uint _files;
  private uint _pipes;
  private uint _classified;
  private uint _seen;

  /// <summary>
  /// Counts one descriptor, from the target of its link.
  /// </summary>
  /// <param name="target">Where <c>fd/[n]</c> points, or null when the link could not be read.</param>
  /// <param name="openFlags">
  /// The descriptor's open flags where they are known. Left unknown by the sampler: reading them is
  /// a second file per descriptor, and the one thing they decide here — a directory against a
  /// file — is a distinction this tally does not draw.
  /// </param>
  public void Add(string? target, Counter openFlags) {
    ++this._seen;
    switch (DescriptorParser.Classify(target, openFlags)) {
      case HandleKind.Socket:
        ++this._sockets;
        break;
      case HandleKind.Pipe:
        ++this._pipes;
        break;
      // Directories with them, because separating the two needs the open flags out of fdinfo and
      // that is a file per descriptor. A device, a memfd and an anonymous inode are each their own
      // kind and none of them is a file (PRD §5.3).
      case HandleKind.File:
      case HandleKind.Directory:
        ++this._files;
        break;
      case HandleKind.Unknown:
        // Not classified, so it is counted in nothing — and remembered, because a scan that
        // classified none of what it saw has not found a process holding no sockets.
        return;
      default:
        break;
    }

    ++this._classified;
  }

  /// <summary>Descriptors open on a socket — the count §40's connection list joins against.</summary>
  public readonly Counter Sockets => this.Counted(this._sockets);

  /// <summary>Descriptors open on a name in the file system: files and directories.</summary>
  public readonly Counter Files => this.Counted(this._files);

  /// <summary>Descriptors open on a pipe, both ends of which are one each.</summary>
  public readonly Counter Pipes => this.Counted(this._pipes);

  /// <summary>
  /// A count, or the reason there is none.
  /// </summary>
  /// <remarks>
  /// A process with no descriptors at all holds nought sockets, and that is a real nought. Entries
  /// that could not be classified are the other case: a live <c>/proc</c> names every descriptor, so
  /// a scan that saw entries and classified none of them was not looking at one — a recorded tree
  /// whose <c>fd</c> directory carries no link targets, typically. Reporting that as "no sockets"
  /// would be the confident zero this program exists not to print (PRD §72.3).
  /// </remarks>
  private readonly Counter Counted(uint value)
    => this._seen == 0 || this._classified > 0 ? Counter.Of(value) : Counter.NotSupported;

}
