namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// What kind of core a logical processor sits on (PRD §46).
/// </summary>
/// <remarks>
/// Hybrid parts — Intel from Alder Lake, and every ARM big.LITTLE design — put cores of two very
/// different speeds in one package. A meter that treats them alike says a machine is half busy when
/// what it means is that the fast half is idle and the slow half is saturated, which are not the
/// same machine at all.
/// </remarks>
public enum CoreKind {

  /// <summary>Not a hybrid part, or nothing this machine exposes says which.</summary>
  Unknown,

  /// <summary>A performance core: Intel's <c>cpu_core</c>, ARM's big.</summary>
  Performance,

  /// <summary>An efficiency core: Intel's <c>cpu_atom</c>, ARM's LITTLE.</summary>
  Efficiency,

}

/// <summary>One logical processor and where it sits.</summary>
/// <param name="Logical">Its number, which is the index every per-core counter is keyed by.</param>
/// <param name="Package">
/// Which socket it is in, or -1 where the machine does not say — a container usually, or an
/// architecture that does not publish topology.
/// </param>
/// <param name="Core">
/// Which physical core within that socket. Two logical processors sharing one are SMT siblings, and
/// saturating both does not give twice the work of saturating one.
/// </param>
/// <param name="Kind">Performance, efficiency, or unknown.</param>
public readonly record struct CoreDescriptor(int Logical, int Package, int Core, CoreKind Kind);

/// <summary>
/// How the machine's logical processors are arranged (PRD §46).
/// </summary>
/// <remarks>
/// Read once: a machine does not repartition its cores while a program watches it. Offline cores are
/// not listed at all, so a caller must key by <see cref="CoreDescriptor.Logical"/> and never by
/// position in this list.
/// </remarks>
public sealed record CpuTopology(IReadOnlyList<CoreDescriptor> Cores) {

  public static readonly CpuTopology Empty = new([]);

  /// <summary>Whether this machine has cores of more than one kind.</summary>
  public bool IsHybrid {
    get {
      var seen = CoreKind.Unknown;
      foreach (var core in this.Cores) {
        if (core.Kind == CoreKind.Unknown)
          continue;

        if (seen == CoreKind.Unknown)
          seen = core.Kind;
        else if (seen != core.Kind)
          return true;
      }

      return false;
    }
  }

  /// <summary>The sockets, in order, or empty where the machine does not say which is which.</summary>
  public IReadOnlyList<int> Packages {
    get {
      var packages = new List<int>();
      foreach (var core in this.Cores)
        if (core.Package >= 0 && !packages.Contains(core.Package))
          packages.Add(core.Package);

      packages.Sort();
      return packages;
    }
  }

  /// <summary>
  /// The logical processors of one socket, performance cores first and efficiency cores after, each
  /// group in core order with SMT siblings adjacent.
  /// </summary>
  /// <remarks>
  /// The order is the point: a heat map is read as a picture, and a picture whose cells are in the
  /// kernel's enumeration order interleaves the two kinds on some machines and separates them on
  /// others. Sorting here means the same silicon always looks the same.
  /// </remarks>
  public IReadOnlyList<CoreDescriptor> Of(int package) {
    var members = new List<CoreDescriptor>();
    foreach (var core in this.Cores)
      if (core.Package == package)
        members.Add(core);

    members.Sort(static (a, b) => {
      var kind = Rank(a.Kind).CompareTo(Rank(b.Kind));
      if (kind != 0)
        return kind;

      var core = a.Core.CompareTo(b.Core);
      return core != 0 ? core : a.Logical.CompareTo(b.Logical);
    });

    return members;
  }

  /// <summary>Performance before efficiency before unknown, which is fastest-first.</summary>
  private static int Rank(CoreKind kind) => kind switch {
    CoreKind.Performance => 0,
    CoreKind.Efficiency => 1,
    _ => 2,
  };

}
