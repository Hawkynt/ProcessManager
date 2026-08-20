using System.Runtime.InteropServices;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

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
