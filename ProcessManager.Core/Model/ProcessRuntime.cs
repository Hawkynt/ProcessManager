namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// What is executing inside a process, when it is not only machine code (PRD §14, §80).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is nought because a record nobody filled knows nothing, and
/// <see cref="Native"/> is a finding: every module of the process was looked at and none of them was
/// a runtime. The two must never collapse into one value — "there is no virtual machine in here" and
/// "nobody could look" are opposite statements (PRD §72.3).
/// </remarks>
public enum ProcessRuntime : byte {

  /// <summary>Not looked at, or not readable.</summary>
  Unknown = 0,

  /// <summary>Machine code and libraries, with no runtime mapped. A NativeAOT build lands here.</summary>
  Native,

  /// <summary>CoreCLR — .NET.</summary>
  DotNet,

  /// <summary>Mono.</summary>
  Mono,

  /// <summary>A Java virtual machine.</summary>
  Java,

  /// <summary>CPython, embedded or otherwise.</summary>
  Python,

  /// <summary>Ruby.</summary>
  Ruby,

  /// <summary>Perl.</summary>
  Perl,

  /// <summary>PHP.</summary>
  Php,

  /// <summary>Node, when its libraries are shared rather than linked into the executable.</summary>
  Node,

  /// <summary>Wine: a Windows program, running here.</summary>
  Wine,

}

/// <summary>The runtime as a word, in one place so no front-end invents a second spelling.</summary>
public static class ProcessRuntimeText {

  public static string Text(this ProcessRuntime runtime) => runtime switch {
    ProcessRuntime.Native => "native",
    ProcessRuntime.DotNet => ".NET",
    ProcessRuntime.Mono => "Mono",
    ProcessRuntime.Java => "Java",
    ProcessRuntime.Python => "Python",
    ProcessRuntime.Ruby => "Ruby",
    ProcessRuntime.Perl => "Perl",
    ProcessRuntime.Php => "PHP",
    ProcessRuntime.Node => "Node",
    ProcessRuntime.Wine => "Wine",
    _ => "?",
  };

}
