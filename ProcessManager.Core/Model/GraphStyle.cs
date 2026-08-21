namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// How an in-row history is drawn (PRD §57.4).
/// </summary>
/// <remarks>
/// In Core rather than beside the painter that uses it, because it is a preference and the settings
/// record is here. Keeping it in the terminal assembly meant the choice could be made per run and
/// never remembered — a person who preferred braille had to say so every time, which §67 counted
/// against this program.
/// </remarks>
public enum GraphStyle : byte {

  /// <summary>The eighth-block ramp: one sample per cell, eight levels.</summary>
  Blocks,

  /// <summary>Braille dots: two samples per cell, four levels — twice the time in the same width.</summary>
  Braille,

  /// <summary>A ramp of punctuation, for a terminal that can draw neither.</summary>
  Ascii,

  /// <summary>No plot at all: the numbers the plot would have shown.</summary>
  Numbers,

}
