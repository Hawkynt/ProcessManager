using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Photographs the window by asking GTK to paint it into a Cairo surface, inside this process.
/// </summary>
/// <remarks>
/// <para>
/// The obvious route — ImageMagick's <c>import</c>, or <c>xwd</c> — was tried first and produces a
/// uniformly black 290-byte PNG here. An <c>import</c> built without its X11 delegate exits zero
/// having written nothing, and under a bare Xvfb with no window manager there is nothing on the root
/// window to grab in the first place. Asking the widget to draw itself sidesteps the display server
/// entirely, which also makes the picture deterministic: it is the toolkit's own paint pipeline
/// rather than whatever happened to be stacked on a desktop.
/// </para>
/// <para>
/// The technique is NativeForms' own — its demo photographs its gallery this way, and its comments
/// are what pointed at the diagnosis above. This is the small version: one window, no popups, no
/// compositing.
/// </para>
/// <para>
/// Must be called on the UI thread, which for this program means from inside a timer tick.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class GtkCapture {

  private const string _Gtk = "libgtk-3.so.0";
  private const string _Cairo = "libcairo.so.2";

  /// <summary><c>CAIRO_FORMAT_RGB24</c>: 32-bit, no alpha, native-endian.</summary>
  private const int _CairoFormatRgb24 = 1;

  private const int _CairoStatusSuccess = 0;

  /// <summary>A guard against a nonsense geometry asking for gigabytes.</summary>
  private const int _MaxExtent = 8192;

  [LibraryImport(_Gtk)]
  private static partial nint gtk_window_list_toplevels();

  [LibraryImport(_Gtk)]
  private static partial int gtk_widget_get_mapped(nint widget);

  [LibraryImport(_Gtk)]
  private static partial void gtk_widget_draw(nint widget, nint cr);

  /// <summary>
  /// Blocks until the widget's pending frame has actually been drawn.
  /// </summary>
  /// <remarks>
  /// Without it a capture taken right after anything that queues a relayout — selecting a row, say —
  /// photographs the frame *before* that layout, which for a window whose children have not been
  /// allocated yet is a rectangle of black. That is exactly what the first version of this produced.
  /// </remarks>
  [LibraryImport(_Gtk)]
  private static partial void gtk_test_widget_wait_for_draw(nint widget);

  [LibraryImport(_Gtk)]
  private static partial void gtk_widget_get_allocation(nint widget, out GtkAllocation allocation);

  [LibraryImport(_Cairo)]
  private static partial nint cairo_image_surface_create(int format, int width, int height);

  [LibraryImport(_Cairo)]
  private static partial nint cairo_create(nint surface);

  [LibraryImport(_Cairo)]
  private static partial void cairo_destroy(nint cr);

  [LibraryImport(_Cairo)]
  private static partial void cairo_surface_destroy(nint surface);

  [LibraryImport(_Cairo, StringMarshalling = StringMarshalling.Utf8)]
  private static partial int cairo_surface_write_to_png(nint surface, string filename);

  [LibraryImport("libglib-2.0.so.0")]
  private static partial void g_list_free(nint list);

  [StructLayout(LayoutKind.Sequential)]
  private struct GtkAllocation {
    public int X;
    public int Y;
    public int Width;
    public int Height;
  }

  /// <summary>
  /// Writes a PNG of the first mapped toplevel. Returns its size, or null with the reason.
  /// </summary>
  public static Size? Window(string path, out string? failure) {
    failure = null;
    var widget = FirstMappedToplevel();
    if (widget == 0) {
      failure = "no mapped GTK toplevel — the window never reached the display server";
      return null;
    }

    // Let any queued relayout and its repaint finish before asking for the pixels.
    gtk_test_widget_wait_for_draw(widget);
    gtk_widget_get_allocation(widget, out var allocation);
    if (allocation.Width is <= 0 or > _MaxExtent || allocation.Height is <= 0 or > _MaxExtent) {
      failure = $"the window's allocation is {allocation.Width}x{allocation.Height}, which is not a picture";
      return null;
    }

    var surface = cairo_image_surface_create(_CairoFormatRgb24, allocation.Width, allocation.Height);
    if (surface == 0) {
      failure = "cairo would not allocate a surface";
      return null;
    }

    try {
      var cr = cairo_create(surface);
      if (cr == 0) {
        failure = "cairo would not create a context";
        return null;
      }

      try {
        // The toolkit's own draw signal, children, theme and all.
        gtk_widget_draw(widget, cr);
      } finally {
        cairo_destroy(cr);
      }

      var status = cairo_surface_write_to_png(surface, path);
      if (status != _CairoStatusSuccess) {
        failure = $"cairo could not write the PNG (status {status})";
        return null;
      }

      return new(allocation.Width, allocation.Height);
    } finally {
      cairo_surface_destroy(surface);
    }
  }

  /// <summary>
  /// The first toplevel GTK reports as mapped.
  /// </summary>
  /// <remarks>
  /// By mapped-ness rather than by title: the title is the caller's to change, and this program has
  /// exactly one window when it is being photographed.
  /// </remarks>
  private static nint FirstMappedToplevel() {
    var list = gtk_window_list_toplevels();
    if (list == 0)
      return 0;

    try {
      // GList: { gpointer data; GList* next; GList* prev; }
      for (var node = list; node != 0; node = Marshal.ReadIntPtr(node, nint.Size)) {
        var widget = Marshal.ReadIntPtr(node);
        if (widget != 0 && gtk_widget_get_mapped(widget) != 0)
          return widget;
      }

      return 0;
    } finally {
      g_list_free(list);
    }
  }

}
