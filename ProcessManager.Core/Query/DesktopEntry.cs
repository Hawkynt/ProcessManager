using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a desktop entry says an executable is called, in words a person chose (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// Windows carries a product name inside the binary, in a version resource. An ELF has no such
/// thing and never has — there is no section to read it out of — so a name for the *application*
/// rather than for the file has to come from somewhere else. On Linux that somewhere is the desktop
/// entry: a <c>.desktop</c> file under <c>applications/</c> in one of the XDG data directories,
/// whose <c>Name</c> is the string every menu, launcher and taskbar on the machine already shows
/// for the program. It is the same fact, kept in a different place.
/// </para>
/// <para>
/// Only the <c>[Desktop Entry]</c> group is read, and that is not a simplification. A file may carry
/// <c>[Desktop Action …]</c> groups after it, each with its own <c>Name</c> and <c>Exec</c>, and on
/// the machine this was written on <c>libreoffice-startcenter.desktop</c> has six of them — Writer,
/// Calc, Impress, Draw, Base, Math. A parser that reads keys wherever it finds them reports
/// whichever of those came last, for every LibreOffice process on the machine.
/// </para>
/// <para>
/// The unlocalised <c>Name</c> only. <c>Name[de]</c> and its forty siblings are the same fact in
/// another language, and choosing between them would make a column's contents depend on the
/// environment a process manager happened to start in.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is exercised on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class DesktopEntry {

  private static ReadOnlySpan<byte> _group => "[Desktop Entry]"u8;
  private static ReadOnlySpan<byte> _name => "Name"u8;
  private static ReadOnlySpan<byte> _exec => "Exec"u8;
  private static ReadOnlySpan<byte> _tryExec => "TryExec"u8;
  private static ReadOnlySpan<byte> _type => "Type"u8;
  private static ReadOnlySpan<byte> _hidden => "Hidden"u8;

  /// <summary>
  /// Reads one <c>.desktop</c> file.
  /// </summary>
  /// <returns>
  /// <see cref="DesktopApplication.None"/> for anything that does not name an application: a link,
  /// a directory entry, an entry marked <c>Hidden</c> — which the specification defines as deleted
  /// rather than merely invisible — or one with no name or no command to attach it to.
  /// </returns>
  public static DesktopApplication Read(ReadOnlySpan<byte> content) {
    string? name = null, exec = null, tryExec = null;
    var isApplication = false;
    var hidden = false;
    var inGroup = false;

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = Trim(scanner.NextLine());
      if (line.IsEmpty || line[0] == (byte)'#')
        continue;

      // A group header ends the one before it. Everything outside [Desktop Entry] is somebody
      // else's keys — an action's, usually — and reading them is how an application ends up named
      // after the last of its right-click menu items.
      if (line[0] == (byte)'[') {
        if (inGroup)
          break;

        inGroup = line.SequenceEqual(_group);
        continue;
      }

      if (!inGroup)
        continue;

      var equals = line.IndexOf((byte)'=');
      if (equals < 0)
        continue;

      var key = Trim(line[..equals]);
      var value = Trim(line[(equals + 1)..]);
      if (value.IsEmpty)
        continue;

      // Name[de], Name[sr@latin] and the other forty are the same fact in another language.
      if (key.SequenceEqual(_name))
        name ??= Encoding.UTF8.GetString(value);
      else if (key.SequenceEqual(_exec))
        exec ??= Encoding.UTF8.GetString(value);
      else if (key.SequenceEqual(_tryExec))
        tryExec ??= Encoding.UTF8.GetString(value);
      else if (key.SequenceEqual(_type))
        isApplication = value.SequenceEqual("Application"u8);
      else if (key.SequenceEqual(_hidden))
        hidden = value.SequenceEqual("true"u8);
    }

    if (!isApplication || hidden || name is not { Length: > 0 })
      return DesktopApplication.None;

    var program = ProgramOf(exec, out var bare);
    if (program is null) {
      // TryExec is a program and never a command line — it is there for a launcher to test that the
      // thing exists — so an entry with only that starts the program plain.
      program = ProgramOf(tryExec, out _);
      bare = program is not null;
    }

    return program is null ? DesktopApplication.None : new(name, program, bare);
  }

  /// <summary>
  /// The program a command line starts, without its directory.
  /// </summary>
  /// <param name="bare">
  /// True when the command is that program and nothing else. Field codes do not count as arguments:
  /// <c>Exec=dolphin %u</c> is a way of saying "open Dolphin on this", while
  /// <c>Exec=libreoffice --calc %U</c> is a way of starting one part of a larger program, and the
  /// difference decides which of eight LibreOffice entries names the application itself.
  /// </param>
  /// <remarks>
  /// The quoting is the specification's: double quotes group, and a backslash inside them escapes
  /// the next byte. Reserved characters may appear quoted in a path, and splitting on spaces alone
  /// would cut <c>"/opt/My App/thing"</c> in half.
  /// </remarks>
  public static string? ProgramOf(string? command, out bool bare) {
    bare = false;
    if (command is not { Length: > 0 })
      return null;

    var first = NextToken(command, 0, out var next);
    if (first is not { Length: > 0 })
      return null;

    bare = true;
    while (next < command.Length) {
      var token = NextToken(command, next, out next);
      if (token is not { Length: > 0 })
        continue;

      // "%f", "%U", "%i" and the rest are the launcher's business, not the program's. "%%" is a
      // literal per cent sign and so is a real argument.
      if (token is { Length: 2 } && token[0] == '%' && token[1] != '%')
        continue;

      bare = false;
      break;
    }

    var slash = first.LastIndexOf('/');
    return slash < 0 ? first : slash + 1 < first.Length ? first[(slash + 1)..] : null;
  }

  /// <summary>
  /// An <c>Exec</c> line split into the program it runs and what it passes (PRD §42).
  /// </summary>
  /// <remarks>
  /// The whole path, unlike <see cref="ProgramOf"/>, which wants a name to match a process against.
  /// A column headed "executable" is read to find out <em>which</em> <c>python</c> an entry starts,
  /// and the directory is the entire answer to that.
  /// <para>
  /// The arguments come back as the rest of the line rather than as re-joined tokens, so the quoting
  /// the file was written with survives into anything that copies the cell. Re-joining would produce a
  /// line that looks like the original and does not run the same way.
  /// </para>
  /// </remarks>
  public static (string? Executable, string? Arguments) SplitCommand(string? command) {
    if (command is not { Length: > 0 })
      return (null, null);

    var program = NextToken(command, 0, out var next);
    if (program is not { Length: > 0 })
      return (null, null);

    var rest = next < command.Length ? command[next..].TrimStart() : string.Empty;
    return (program, rest.Length > 0 ? rest : null);
  }

  /// <summary>One shell-ish token, honouring double quotes and the backslash inside them.</summary>
  private static string? NextToken(string command, int start, out int next) {
    while (start < command.Length && command[start] == ' ')
      ++start;

    if (start >= command.Length) {
      next = command.Length;
      return null;
    }

    var builder = new StringBuilder();
    var quoted = false;
    var i = start;
    for (; i < command.Length; ++i) {
      var c = command[i];
      if (c == '"') {
        quoted = !quoted;
        continue;
      }

      if (quoted && c == '\\' && i + 1 < command.Length) {
        builder.Append(command[++i]);
        continue;
      }

      if (!quoted && c == ' ')
        break;

      builder.Append(c);
    }

    next = i;
    return builder.ToString();
  }

  private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line) {
    while (!line.IsEmpty && (line[0] == (byte)' ' || line[0] == (byte)'\t'))
      line = line[1..];

    while (!line.IsEmpty && (line[^1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
      line = line[..^1];

    return line;
  }

}

/// <summary>One desktop entry, reduced to the three things naming an application needs.</summary>
/// <param name="Name">What a person calls it.</param>
/// <param name="Program">The executable it starts, without its directory.</param>
/// <param name="IsBareCommand">
/// Whether the entry starts that program plain, rather than with the arguments that select one part
/// of it.
/// </param>
public readonly record struct DesktopApplication(string? Name, string? Program, bool IsBareCommand) {

  /// <summary>Not an application entry, or one with nothing to say about a program.</summary>
  public static readonly DesktopApplication None = default;

  public bool NamesAProgram => this.Name is { Length: > 0 } && this.Program is { Length: > 0 };

}

/// <summary>
/// Every desktop entry on the machine, indexed by the program it starts (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// The awkward part is that the mapping is not one to one, and answering as though it were is the
/// way to be confidently wrong. On the machine this was written on, eight entries start
/// <c>libreoffice</c> and each carries a different name; picking any one of them would report a
/// spreadsheet as a drawing whenever the guess went the other way.
/// </para>
/// <para>
/// So three rules, in order, and a refusal when none of them applies:
/// </para>
/// <list type="number">
///   <item>One entry starts the program — it is the application.</item>
///   <item>
///     One entry starts it plain while the rest start it with arguments — the plain one is the
///     application and the others are its parts. This is what makes <c>libreoffice</c> "LibreOffice"
///     rather than "LibreOffice Calc".
///   </item>
///   <item>Several entries start it and every one gives the same name — that name.</item>
/// </list>
/// <para>
/// Otherwise the program is several applications and the catalogue says so, because a column that
/// names one of them is wrong more often than a column that admits it does not know (PRD §5.3).
/// </para>
/// <para>
/// Entries are added in the order the base directory specification searches — the user's own data
/// directory, then the system ones — and the first file to claim an id wins, so a locally overridden
/// entry replaces the packaged one rather than arguing with it.
/// </para>
/// </remarks>
public sealed class DesktopApplications {

  private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Slot> _byProgram = new(StringComparer.Ordinal);

  /// <summary>How many entries went in, which is what tells an empty machine from an unread one.</summary>
  public int Count { get; private set; }

  /// <summary>
  /// Adds one entry under its desktop file id.
  /// </summary>
  /// <param name="id">
  /// The file's name relative to its <c>applications</c> directory, with the separators turned into
  /// dashes — the identifier the specification gives it, and what makes an entry in the user's own
  /// directory shadow the packaged one of the same name rather than double it.
  /// </param>
  /// <returns>False when the entry named nothing, or when that id has already been claimed.</returns>
  public bool Add(string id, in DesktopApplication application) {
    if (!application.NamesAProgram || !this._ids.Add(id))
      return false;

    ++this.Count;
    var program = application.Program!;
    if (!this._byProgram.TryGetValue(program, out var slot)) {
      this._byProgram[program] = new() {
        Name = application.Name,
        Count = 1,
        BareCount = application.IsBareCommand ? 1 : 0,
        BareName = application.IsBareCommand ? application.Name : null,
      };

      return true;
    }

    slot.Count++;
    if (!string.Equals(slot.Name, application.Name, StringComparison.Ordinal))
      slot.NamesDiffer = true;

    if (application.IsBareCommand) {
      slot.BareCount++;
      slot.BareName = application.Name;
    }

    this._byProgram[program] = slot;
    return true;
  }

  /// <summary>
  /// What the machine calls the application that runs this program.
  /// </summary>
  /// <param name="several">
  /// True when more than one application starts the program and nothing distinguishes them. A real
  /// answer, and not the same as having found nothing.
  /// </param>
  public string? NameOf(string? program, out bool several) {
    several = false;
    if (program is not { Length: > 0 } || !this._byProgram.TryGetValue(program, out var slot))
      return null;

    if (slot.Count == 1 || !slot.NamesDiffer)
      return slot.Name;

    if (slot.BareCount == 1)
      return slot.BareName;

    several = true;
    return null;
  }

  private struct Slot {
    public string? Name;
    public string? BareName;
    public int Count;
    public int BareCount;
    public bool NamesDiffer;
  }

}
