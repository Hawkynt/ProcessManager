using System.Runtime.InteropServices;
using System.Text;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// What became of a request to a window (PRD §39).
/// </summary>
/// <remarks>
/// Six answers and not a boolean, because five of them are different things to tell somebody and only
/// one of them is a fault. "There is no window manager here" and "that window belongs to another
/// program now" both mean nothing happened, and they mean it for reasons a reader needs to tell apart
/// (PRD §5.3, §72.3).
/// </remarks>
internal enum WindowCommandResult : byte {

  /// <summary>The request went to the desktop. Whether it is granted is the window manager's to say.</summary>
  Sent,

  /// <summary>There is no X11 session to ask — a server, or a program run over ssh.</summary>
  NoSession,

  /// <summary>No window of that id is a top-level of this session. It has closed, or it never was one.</summary>
  NotListed,

  /// <summary>The window is here and says it belongs to a different process.</summary>
  NotThisProcess,

  /// <summary>The window does not list <c>WM_DELETE_WINDOW</c>: it has said it does not handle being asked.</summary>
  NotHandled,

  /// <summary>Nothing on this session manages windows, so there is nobody to grant the request.</summary>
  NoWindowManager,

  /// <summary>The server refused the request itself.</summary>
  Failed,

}

/// <summary>
/// The desktop's windows, and which process owns each (PRD §39).
/// </summary>
/// <remarks>
/// <para>
/// X11 through <c>libX11</c>, opened on demand and never from the sampling path. The library is
/// optional in exactly the way GTK is — a server has none — so a missing one is caught once and
/// remembered rather than being an error every time somebody opens the page.
/// </para>
/// <para>
/// <b>Wayland cannot answer this and that is deliberate on its part.</b> A Wayland client is not
/// told about other clients' surfaces, by design, and no amount of asking changes it. What a Wayland
/// desktop does have is XWayland, which hosts X11 clients and does answer — so the honest result
/// there is a partial list plus a sentence saying why it is partial, rather than either an empty
/// list or a refusal.
/// </para>
/// </remarks>
internal static partial class X11Windows {

  private const string _Library = "libX11.so.6";

  private const int _Success = 0;
  private const int _AnyPropertyType = 0;
  private const int _IsViewable = 2;

  private static bool? _available;

  [LibraryImport(_Library, EntryPoint = "XOpenDisplay", StringMarshalling = StringMarshalling.Utf8)]
  private static partial nint OpenDisplay(string? name);

  [LibraryImport(_Library, EntryPoint = "XCloseDisplay")]
  private static partial int CloseDisplay(nint display);

  [LibraryImport(_Library, EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
  private static partial nint InternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

  [LibraryImport(_Library, EntryPoint = "XDefaultRootWindow")]
  private static partial nint DefaultRootWindow(nint display);

  [LibraryImport(_Library, EntryPoint = "XGetWindowProperty")]
  private static partial int GetWindowProperty(
    nint display, nint window, nint property, long offset, long length,
    [MarshalAs(UnmanagedType.Bool)] bool delete, nint requestedType,
    out nint actualType, out int actualFormat, out ulong itemCount, out ulong bytesAfter, out nint data);

  [LibraryImport(_Library, EntryPoint = "XFree")]
  private static partial int Free(nint data);

  [LibraryImport(_Library, EntryPoint = "XQueryPointer")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool QueryPointer(
    nint display, nint window, out nint root, out nint child,
    out int rootX, out int rootY, out int winX, out int winY, out uint mask);

  [LibraryImport(_Library, EntryPoint = "XGetWindowAttributes")]
  private static partial int GetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);

  [LibraryImport(_Library, EntryPoint = "XQueryTree")]
  private static partial int QueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint count);

  [LibraryImport(_Library, EntryPoint = "XTranslateCoordinates")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool TranslateCoordinates(
    nint display, nint source, nint destination, int sourceX, int sourceY,
    out int x, out int y, out nint child);

  [LibraryImport(_Library, EntryPoint = "XSendEvent")]
  private static partial int SendEvent(
    nint display, nint window, [MarshalAs(UnmanagedType.Bool)] bool propagate, long eventMask, ref XClientMessageEvent send);

  [LibraryImport(_Library, EntryPoint = "XFlush")]
  private static partial int Flush(nint display);

  [LibraryImport(_Library, EntryPoint = "XMapRaised")]
  private static partial int MapRaised(nint display, nint window);

  /// <summary>
  /// <c>XClientMessageEvent</c>, padded to the size of the whole <c>XEvent</c> union.
  /// </summary>
  /// <remarks>
  /// <c>XSendEvent</c> takes an <c>XEvent*</c> and Xlib copies the union's full 24 <c>long</c>s out
  /// of it regardless of which member was filled in. Handing it a struct the size of the client
  /// message alone would have it read 96 bytes past the end of ours, so the padding is not
  /// decoration — it is the difference between a close request and a random stack read.
  /// </remarks>
  [StructLayout(LayoutKind.Sequential)]
  private struct XClientMessageEvent {
    // int, then the padding the next field's alignment forces — not one 64-bit field. They occupy
    // the same eight bytes on a little-endian machine and not on any other, and a 33 written into
    // the high half of a word Xlib reads as an int is a message the server discards in silence.
    public int Type;
    private readonly int _typePadding;
    public nuint Serial;
    public int SendEventFlag;
    public nint Display;
    public nint Window;
    public nint MessageType;
    public int Format;
    public nint Data0, Data1, Data2, Data3, Data4;
    private readonly nint _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6, _pad7, _pad8, _pad9, _pad10, _pad11;
  }

  private const int _ClientMessage = 33;

  /// <summary>Format 32: the message's payload is read as the five <c>long</c>s of <c>data.l</c>.</summary>
  private const int _LongFormat = 32;

  [StructLayout(LayoutKind.Sequential)]
  private struct XWindowAttributes {
    public int X, Y, Width, Height, BorderWidth, Depth;
    public nint Visual;
    public nint Root;
    public int Class;
    public int BitGravity, WinGravity, BackingStore;
    public nuint BackingPlanes, BackingPixel;
    public int SaveUnder;
    public nint Colormap;
    public int MapInstalled, MapState;
    public long AllEventMasks, YourEventMask;
    public long DoNotPropagateMask;
    public int OverrideRedirect;
    public nint Screen;
  }

  /// <summary>Whether X11 is here at all, asked once.</summary>
  public static bool Available {
    get {
      if (_available is { } known)
        return known;

      try {
        var display = OpenDisplay(null);
        _available = display != 0;
        if (display != 0)
          CloseDisplay(display);
      } catch (DllNotFoundException) {
        _available = false;
      } catch (EntryPointNotFoundException) {
        _available = false;
      }

      return _available.Value;
    }
  }

  /// <summary>
  /// Whether this session is Wayland, which changes what an empty list means.
  /// </summary>
  /// <remarks>
  /// By the environment rather than by asking the display server: <c>WAYLAND_DISPLAY</c> is what
  /// every toolkit uses to decide the same thing, and a program that disagreed with the toolkits
  /// about which session it is in would be wrong in a more confusing way.
  /// </remarks>
  private static bool IsWayland
    => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
      || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

  /// <summary>Every window the window manager lists, with the process behind each.</summary>
  public static WindowList Enumerate() {
    if (!Available)
      return new(IsWayland ? WindowSourceState.WaylandRefuses : WindowSourceState.NoSession, []);

    var display = OpenDisplay(null);
    if (display == 0)
      return new(WindowSourceState.NoSession, []);

    try {
      var root = DefaultRootWindow(display);
      var handles = TopLevels(display, root);

      var windows = new List<WindowRecord>();
      foreach (var handle in handles)
        if (Describe(display, handle) is { } window)
          windows.Add(window);

      // A Wayland session with XWayland answers, and answers partially. Saying so is the whole
      // point: a short list with no explanation reads as a broken program.
      return new(IsWayland ? WindowSourceState.WaylandRefuses : WindowSourceState.Available, windows);
    } finally {
      CloseDisplay(display);
    }
  }

  /// <summary>
  /// The window under the pointer, right now, and the process that owns it (PRD §39).
  /// </summary>
  /// <remarks>
  /// The pointer is read rather than grabbed. A grab would let somebody drag a crosshair the way
  /// Process Explorer does, but it also takes the pointer away from the whole desktop until it is
  /// released — and a grab left dangling by a crash makes a session unusable. Reading where the
  /// pointer already is, on a delay, gets the same answer without ever holding the desktop hostage.
  /// </remarks>
  public static WindowRecord? UnderPointer() {
    if (!Available)
      return null;

    var display = OpenDisplay(null);
    if (display == 0)
      return null;

    try {
      var root = DefaultRootWindow(display);
      if (!QueryPointer(display, root, out _, out var child, out var rootX, out var rootY, out _, out _, out _))
        return null;

      // The child of the root is the window manager's frame, not the application's window. Walking
      // down to the deepest child at that point is what turns a frame into the window somebody
      // actually pointed at.
      var window = child;
      while (window != 0 && TranslateCoordinates(display, root, window, rootX, rootY, out _, out _, out var deeper) && deeper != 0)
        window = deeper;

      if (window == 0)
        window = child;

      // The pointed-at window is often a child that owns no _NET_WM_PID; the property lives on the
      // top-level, so the answer comes from whichever listed window contains this one.
      return window == 0 ? null : Describe(display, window) ?? Owner(display, window);
    } finally {
      CloseDisplay(display);
    }
  }

  /// <summary>
  /// Asks every window a process owns to close itself, the way its close button would (PRD §25.1).
  /// </summary>
  /// <returns>
  /// Whether the session could be asked at all, and how many windows were asked. The pair is needed
  /// because zero windows and no session are different answers: the first says this process has no
  /// user interface to ask, and the second says nothing here can tell us either way — and only the
  /// first justifies falling back to a signal (PRD §5.3).
  /// </returns>
  /// <remarks>
  /// <para>
  /// <c>WM_DELETE_WINDOW</c>, sent to the window itself, rather than the window manager's
  /// <c>_NET_CLOSE_WINDOW</c>. It is the same message in the end — the window manager's job is to
  /// forward one as the other — but this one arrives whether or not there is a window manager
  /// running to do the forwarding, and it is the message the toolkit's own handler is written for.
  /// It is what makes an editor put up "save your changes?" instead of vanishing.
  /// </para>
  /// <para>
  /// A window that does not list <c>WM_DELETE_WINDOW</c> in its <c>WM_PROTOCOLS</c> has said it does
  /// not handle being asked, and is not counted as asked. The only thing left for such a window is
  /// <c>XKillClient</c>, which severs the connection rather than requesting anything, and is
  /// therefore not a polite close by any reading.
  /// </para>
  /// </remarks>
  public static (bool SessionAnswered, int Asked) AskToClose(int pid) {
    if (!Available || pid <= 0)
      return (false, 0);

    var display = OpenDisplay(null);
    if (display == 0)
      return (false, 0);

    try {
      var protocols = InternAtom(display, "WM_PROTOCOLS", onlyIfExists: false);
      var deleteWindow = InternAtom(display, "WM_DELETE_WINDOW", onlyIfExists: false);
      if (protocols == 0 || deleteWindow == 0)
        return (false, 0);

      var asked = 0;
      foreach (var handle in TopLevels(display, DefaultRootWindow(display))) {
        if (ReadCardinal(display, handle, "_NET_WM_PID") != pid || !Handles(display, handle, protocols, deleteWindow))
          continue;

        var message = new XClientMessageEvent {
          Type = _ClientMessage,
          Window = handle,
          MessageType = protocols,
          Format = _LongFormat,
          Data0 = deleteWindow,
          // CurrentTime. A real timestamp would be better manners and there is none to hand: this
          // program selects no events, so it has never seen one the server issued.
          Data1 = 0,
        };

        if (SendEvent(display, handle, propagate: false, 0, ref message) != 0)
          ++asked;
      }

      // Xlib buffers requests. Without this the close messages sit in the client's queue until the
      // next call that happens to flush, and the display is closed underneath them.
      Flush(display);
      return (true, asked);
    } finally {
      CloseDisplay(display);
    }
  }

  /// <summary>
  /// Asks one window to come forward, go away, grow, shrink or close (PRD §39).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Four of the five go to the <em>root</em> window and not to the target, which is what EWMH
  /// specifies and is not an accident of this code: a client asking for a window to be raised or
  /// maximised is asking the window manager, and the window manager is the thing selecting
  /// <c>SubstructureRedirect</c> on the root. Sending them to the window itself delivers them to the
  /// application, which has no handler for them and drops them in silence — which is exactly what a
  /// menu item that appears to work and does nothing looks like.
  /// </para>
  /// <para>
  /// The fifth, <see cref="WindowCommand.Close"/>, goes to the window, for the reason
  /// <see cref="AskToClose"/> records: <c>WM_DELETE_WINDOW</c> arrives whether or not there is a
  /// window manager to forward it, and it is the message a toolkit's own close handler is written
  /// for. A window that does not list the protocol has said it does not handle being asked, and is
  /// told so rather than being severed with <c>XKillClient</c>.
  /// </para>
  /// <para>
  /// The window is checked to be a listed top-level of this session <em>and</em> to still name the
  /// process it is being commanded on behalf of. A window id is a number in a space the server
  /// reuses, so a stale one may name a live window of another program — the same hazard a stale tid
  /// carries and checked the same way (PRD §8.2).
  /// </para>
  /// </remarks>
  public static WindowCommandResult Command(int pid, ulong window, WindowCommand command) {
    if (command == WindowCommand.None)
      return WindowCommandResult.Failed;

    if (!Available)
      return WindowCommandResult.NoSession;

    var display = OpenDisplay(null);
    if (display == 0)
      return WindowCommandResult.NoSession;

    try {
      var handle = (nint)window;
      var listed = false;
      foreach (var candidate in TopLevels(display, DefaultRootWindow(display)))
        if (candidate == handle) {
          listed = true;
          break;
        }

      if (!listed)
        return WindowCommandResult.NotListed;

      // The window's own claim about who owns it, checked after the caller has already re-validated
      // the process key. A window that has been recycled onto another program answers with another
      // pid, and that is a refusal rather than a command sent to the wrong desktop.
      if (pid > 0 && ReadCardinal(display, handle, "_NET_WM_PID") != pid)
        return WindowCommandResult.NotThisProcess;

      var result = command == WindowCommand.Close
        ? Close(display, handle)
        : Manage(display, handle, command);

      // Xlib buffers requests. Without this they sit in the client's queue until something else
      // happens to flush, and the display is closed underneath them.
      Flush(display);
      return result;
    } finally {
      CloseDisplay(display);
    }
  }

  private static WindowCommandResult Close(nint display, nint handle) {
    var protocols = InternAtom(display, "WM_PROTOCOLS", onlyIfExists: false);
    var deleteWindow = InternAtom(display, "WM_DELETE_WINDOW", onlyIfExists: false);
    if (protocols == 0 || deleteWindow == 0)
      return WindowCommandResult.Failed;

    if (!Handles(display, handle, protocols, deleteWindow))
      return WindowCommandResult.NotHandled;

    var message = new XClientMessageEvent {
      Type = _ClientMessage,
      Window = handle,
      MessageType = protocols,
      Format = _LongFormat,
      Data0 = deleteWindow,
      // CurrentTime, as AskToClose sends: this program selects no events, so it has never seen a
      // timestamp the server issued.
      Data1 = 0,
    };

    return SendEvent(display, handle, propagate: false, 0, ref message) != 0
      ? WindowCommandResult.Sent
      : WindowCommandResult.Failed;
  }

  /// <summary>
  /// The four requests that are the window manager's to grant.
  /// </summary>
  /// <remarks>
  /// Refused outright where there is no window manager, rather than sent into a session with nothing
  /// listening. <c>_NET_SUPPORTING_WM_CHECK</c> on the root is how EWMH says to ask, and a bare X
  /// server or a minimal window manager answers nothing — in which case there is no one to raise or
  /// minimise anything and saying so beats a message that vanishes (PRD §5.3, §72.3).
  /// </remarks>
  private static WindowCommandResult Manage(nint display, nint handle, WindowCommand command) {
    var root = DefaultRootWindow(display);
    var check = InternAtom(display, "_NET_SUPPORTING_WM_CHECK", onlyIfExists: true);
    if (check == 0 || ReadWindowList(display, root, check).Count == 0)
      return WindowCommandResult.NoWindowManager;

    switch (command) {
      case WindowCommand.Foreground: {
        var active = InternAtom(display, "_NET_ACTIVE_WINDOW", onlyIfExists: false);
        // Source 2 is "a pager", which is what this program is here: window managers apply their
        // focus-stealing prevention to source 1 (an application asking for itself) and let a pager
        // through, because a pager is acting for the person at the keyboard.
        return SendToRoot(display, root, handle, active, 2, 0, 0);
      }

      case WindowCommand.Minimize: {
        // ICCCM's, not EWMH's: there is no _NET_WM_STATE_MINIMIZED, and iconifying is still done by
        // asking for IconicState the way XIconifyWindow has since X11R4.
        var changeState = InternAtom(display, "WM_CHANGE_STATE", onlyIfExists: false);
        return SendToRoot(display, root, handle, changeState, _IconicState, 0, 0);
      }

      case WindowCommand.Maximize:
      case WindowCommand.Restore: {
        var state = InternAtom(display, "_NET_WM_STATE", onlyIfExists: false);
        // Both axes in one message, which _NET_WM_STATE allows and which matters: sent one at a time
        // a window would be drawn once half-maximised, and a window manager that animates would
        // animate twice.
        var vertical = InternAtom(display, "_NET_WM_STATE_MAXIMIZED_VERT", onlyIfExists: false);
        var horizontal = InternAtom(display, "_NET_WM_STATE_MAXIMIZED_HORZ", onlyIfExists: false);
        var action = command == WindowCommand.Maximize ? _StateAdd : _StateRemove;
        var sent = SendToRoot(display, root, handle, state, action, vertical, horizontal);
        // Restoring is two undoings and not one. A minimised window is not maximised, so removing the
        // two states does nothing for it; mapping it is what ICCCM says takes a window out of
        // IconicState, and doing both is what "restore" means to the person who pressed it.
        if (command == WindowCommand.Restore)
          MapRaised(display, handle);

        return sent;
      }

      default:
        return WindowCommandResult.Failed;
    }
  }

  /// <summary>IconicState, from ICCCM's WM_STATE — the value WM_CHANGE_STATE asks for.</summary>
  private const int _IconicState = 3;

  private const int _StateRemove = 0;
  private const int _StateAdd = 1;

  /// <summary>
  /// SubstructureNotify | SubstructureRedirect, which is the mask EWMH names for a root message.
  /// </summary>
  /// <remarks>
  /// A zero mask, which is what the close message uses, delivers only to a client that selected the
  /// event on the window itself. The window manager selects <c>SubstructureRedirect</c> on the root
  /// and nothing else, so a root message sent with no mask reaches nobody at all.
  /// </remarks>
  private const long _SubstructureMask = (1L << 19) | (1L << 20);

  private static WindowCommandResult SendToRoot(
    nint display, nint root, nint handle, nint messageType, nint data0, nint data1, nint data2
  ) {
    if (messageType == 0)
      return WindowCommandResult.Failed;

    var message = new XClientMessageEvent {
      Type = _ClientMessage,
      // The window the request is *about*, even though the event is delivered to the root. This is
      // the field the window manager reads to know which window was meant.
      Window = handle,
      MessageType = messageType,
      Format = _LongFormat,
      Data0 = data0,
      Data1 = data1,
      Data2 = data2,
    };

    return SendEvent(display, root, propagate: false, _SubstructureMask, ref message) != 0
      ? WindowCommandResult.Sent
      : WindowCommandResult.Failed;
  }

  /// <summary>Whether a window lists <paramref name="protocol"/> among the ones it handles.</summary>
  private static bool Handles(nint display, nint window, nint protocolsAtom, nint protocol) {
    if (GetWindowProperty(display, window, protocolsAtom, 0, 64, false, _AnyPropertyType,
          out _, out var format, out var count, out _, out var data) != _Success || data == 0)
      return false;

    try {
      if (format != 32)
        return false;

      for (var i = 0ul; i < count; ++i)
        if (Marshal.ReadIntPtr(data, (int)i * nint.Size) == protocol)
          return true;

      return false;
    } finally {
      Free(data);
    }
  }

  /// <summary>The listed top-level whose id matches, for a child window that carries no properties.</summary>
  private static WindowRecord? Owner(nint display, nint window) {
    foreach (var handle in TopLevels(display, DefaultRootWindow(display)))
      if (handle == window)
        return Describe(display, handle);

    return null;
  }

  /// <summary>
  /// The top-level windows, by whichever route this desktop offers.
  /// </summary>
  /// <remarks>
  /// <c>_NET_CLIENT_LIST</c> first, because a window manager that maintains it has already done the
  /// work of deciding which windows are applications and which are its own decorations. But it is
  /// the window manager that sets it, and a session with a minimal one — or none at all, which is
  /// what a bare X server is — has no such property. Walking the root's children with
  /// <c>XQueryTree</c> is what <c>xwininfo</c> does and answers in both cases.
  /// </remarks>
  private static List<nint> TopLevels(nint display, nint root) {
    var clientList = InternAtom(display, "_NET_CLIENT_LIST", onlyIfExists: true);
    if (clientList != 0) {
      var listed = ReadWindowList(display, root, clientList);
      if (listed.Count > 0)
        return listed;
    }

    return Children(display, root);
  }

  private static List<nint> Children(nint display, nint window) {
    var found = new List<nint>();
    if (QueryTree(display, window, out _, out _, out var children, out var count) == 0 || children == 0)
      return found;

    try {
      for (var i = 0u; i < count; ++i)
        found.Add(Marshal.ReadIntPtr(children, (int)i * nint.Size));
    } finally {
      Free(children);
    }

    return found;
  }

  private static List<nint> ReadWindowList(nint display, nint root, nint property) {
    var handles = new List<nint>();
    if (GetWindowProperty(display, root, property, 0, 4096, false, _AnyPropertyType,
          out _, out var format, out var count, out _, out var data) != _Success || data == 0)
      return handles;

    try {
      // Format 32 means "long" in Xlib's vocabulary, which is the C long and so 64-bit here — one of
      // the oldest traps in X11 and the reason this reads nint rather than int.
      if (format != 32)
        return handles;

      for (var i = 0ul; i < count; ++i)
        handles.Add(Marshal.ReadIntPtr(data, (int)i * nint.Size));
    } finally {
      Free(data);
    }

    return handles;
  }

  private static WindowRecord? Describe(nint display, nint window) {
    var title = ReadText(display, window, "_NET_WM_NAME") ?? ReadText(display, window, "WM_NAME");
    var pid = ReadCardinal(display, window, "_NET_WM_PID");
    var className = ReadClass(display, window);
    if (title is null && pid < 0 && className is null)
      return null;

    var bounds = (0, 0, 0, 0);
    var visible = false;
    if (GetWindowAttributes(display, window, out var attributes) != 0) {
      bounds = (attributes.X, attributes.Y, attributes.Width, attributes.Height);
      visible = attributes.MapState == _IsViewable;
    }

    return new((ulong)window, pid, title ?? string.Empty, className, bounds, visible);
  }

  private static string? ReadText(nint display, nint window, string name) {
    var atom = InternAtom(display, name, onlyIfExists: true);
    if (atom == 0)
      return null;

    if (GetWindowProperty(display, window, atom, 0, 1024, false, _AnyPropertyType,
          out _, out _, out var count, out _, out var data) != _Success || data == 0)
      return null;

    try {
      return count == 0 ? null : Marshal.PtrToStringUTF8(data);
    } finally {
      Free(data);
    }
  }

  /// <summary>
  /// <c>WM_CLASS</c>, which is two NUL-separated strings — the instance name and the class name.
  /// The second is the one worth showing.
  /// </summary>
  private static string? ReadClass(nint display, nint window) {
    var atom = InternAtom(display, "WM_CLASS", onlyIfExists: true);
    if (atom == 0)
      return null;

    if (GetWindowProperty(display, window, atom, 0, 256, false, _AnyPropertyType,
          out _, out _, out var count, out _, out var data) != _Success || data == 0)
      return null;

    try {
      if (count == 0)
        return null;

      var bytes = new byte[count];
      Marshal.Copy(data, bytes, 0, (int)count);
      var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
      var separator = text.IndexOf('\0');
      return separator < 0 ? text : text[(separator + 1)..];
    } finally {
      Free(data);
    }
  }

  private static int ReadCardinal(nint display, nint window, string name) {
    var atom = InternAtom(display, name, onlyIfExists: true);
    if (atom == 0)
      return -1;

    if (GetWindowProperty(display, window, atom, 0, 1, false, _AnyPropertyType,
          out _, out var format, out var count, out _, out var data) != _Success || data == 0)
      return -1;

    try {
      return count == 0 || format != 32 ? -1 : (int)Marshal.ReadIntPtr(data);
    } finally {
      Free(data);
    }
  }

}
