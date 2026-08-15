namespace Hawkynt.ProcessManager.Elevated;

/// <summary>
/// The privileged helper (PRD §8) — not implemented yet.
/// </summary>
/// <remarks>
/// It exists as a binary now so that the shape of the product is honest: the UI never runs elevated,
/// and the only thing that ever will is this. Until §8's framed protocol and its opcode allowlist are
/// written (milestone M7), it refuses to do anything rather than offering a half-checked channel to
/// something running as root. A helper that "mostly" validates is worse than no helper.
/// </remarks>
internal static class Program {

  private static int Main(string[] args) {
    Console.Error.WriteLine(
      "procman-helper: the privileged helper is not implemented yet (PRD §8, milestone M7)."
    );

    Console.Error.WriteLine(
      "procman runs unprivileged and reports what it may not read; nothing needs this binary yet."
    );

    return 1;
  }

}
