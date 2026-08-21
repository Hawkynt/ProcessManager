using System.Text.RegularExpressions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>How a term compares its value.</summary>
public enum QueryOperator : byte {
  Contains,
  Equal,
  NotEqual,
  Greater,
  GreaterOrEqual,
  Less,
  LessOrEqual,
  Matches,
}

/// <summary>
/// The filter language, shared by the window, the terminal and the command line (PRD §56).
/// </summary>
/// <remarks>
/// One parser in Core, over the canonical field keys of <see cref="FieldRegistry"/>, with no
/// front-end permitted its own dialect — that is the requirement, and it is also why the registry is
/// a data structure rather than a switch: every field added to it becomes filterable here for free.
/// <para>
/// A query is parsed once when the user stops typing and evaluated per process per sample, so
/// parsing may allocate and matching may not.
/// </para>
/// </remarks>
public sealed class ProcessQuery {

  private readonly Node? _root;

  private ProcessQuery(Node? root) => this._root = root;

  /// <summary>Matches everything; what an empty search box means.</summary>
  public static readonly ProcessQuery Empty = new(null);

  public bool IsEmpty => this._root is null;

  /// <summary>The query as it was typed, for redisplay.</summary>
  public string Text { get; private init; } = string.Empty;

  /// <summary>
  /// Parses a query. A query that does not parse is reported rather than silently matching nothing,
  /// because a filter that quietly hides every row looks exactly like a machine with no processes.
  /// </summary>
  public static bool TryParse(string? text, out ProcessQuery query, out string? error) {
    error = null;
    if (string.IsNullOrWhiteSpace(text)) {
      query = Empty;
      return true;
    }

    try {
      var parser = new Parser(text);
      var root = parser.ParseExpression();
      parser.ExpectEnd();
      query = new(root) { Text = text };
      return true;
    } catch (QueryException problem) {
      query = Empty;
      error = problem.Message;
      return false;
    }
  }

  /// <summary>Parses, falling back to a plain substring search on anything that will not parse.</summary>
  /// <remarks>
  /// What an interactive search box wants: somebody typing "chrome:" has not written a broken query,
  /// they are halfway through writing a working one, and blanking the list at every keystroke would
  /// make the box unusable.
  /// </remarks>
  public static ProcessQuery ParseOrSubstring(string? text) {
    if (string.IsNullOrWhiteSpace(text))
      return Empty;

    return TryParse(text, out var query, out _)
      ? query
      : new(new FreeTextNode(text)) { Text = text };
  }

  public bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index)
    => this._root is null || this._root.Matches(in process, delta, index);

  #region the tree

  private abstract class Node {
    public abstract bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index);
  }

  private sealed class AndNode(Node left, Node right) : Node {
    public override bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index)
      => left.Matches(in process, delta, index) && right.Matches(in process, delta, index);
  }

  private sealed class OrNode(Node left, Node right) : Node {
    public override bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index)
      => left.Matches(in process, delta, index) || right.Matches(in process, delta, index);
  }

  private sealed class NotNode(Node inner) : Node {
    public override bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index)
      => !inner.Matches(in process, delta, index);
  }

  /// <summary>A bare word: matched against the fields somebody plausibly meant.</summary>
  private sealed class FreeTextNode(string text) : Node {
    public override bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index)
      => Has(process.Name) || Has(process.CommandLine) || Has(process.UserName) || Has(process.ImagePath);

    private bool Has(string? value) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
  }

  private sealed class RegexNode(Regex pattern, ProcessField? field) : Node {
    public override bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index) {
      if (field is { } one)
        return FieldAccessor.RawText(one, in process, delta, index) is { } text && pattern.IsMatch(text);

      return Try(process.Name) || Try(process.CommandLine) || Try(process.ImagePath);
    }

    private bool Try(string? value) => value is not null && pattern.IsMatch(value);
  }

  private sealed class TextComparisonNode(ProcessField field, QueryOperator op, string value) : Node {
    public override bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index) {
      var text = FieldAccessor.RawText(field, in process, delta, index);
      if (text is null)
        // A field with no text on this platform matches nothing — including "not equal", because
        // "this process's container is not X" is not a claim we can make when there are no
        // containers here at all (PRD §72.3).
        return false;

      return op switch {
        QueryOperator.Contains => text.Contains(value, StringComparison.OrdinalIgnoreCase),
        QueryOperator.Equal => string.Equals(text, value, StringComparison.OrdinalIgnoreCase),
        QueryOperator.NotEqual => !string.Equals(text, value, StringComparison.OrdinalIgnoreCase),
        _ => string.Compare(text, value, StringComparison.OrdinalIgnoreCase) switch {
          var c => op switch {
            QueryOperator.Greater => c > 0,
            QueryOperator.GreaterOrEqual => c >= 0,
            QueryOperator.Less => c < 0,
            QueryOperator.LessOrEqual => c <= 0,
            _ => false,
          },
        },
      };
    }
  }

  private sealed class NumberComparisonNode(ProcessField field, QueryOperator op, double value) : Node {
    public override bool Matches(in ProcessRecord process, SnapshotDelta? delta, int index) {
      // No number at all is not zero. A process whose memory could not be read matches neither
      // "> 0" nor "== 0", which is the only honest answer (PRD §72.3).
      if (FieldAccessor.Number(field, in process, delta, index) is not { } actual)
        return false;

      return op switch {
        QueryOperator.Greater => actual > value,
        QueryOperator.GreaterOrEqual => actual >= value,
        QueryOperator.Less => actual < value,
        QueryOperator.LessOrEqual => actual <= value,
        QueryOperator.NotEqual => Math.Abs(actual - value) > Tolerance(value),
        // Equality on a measured double is a trap: "cpu = 12.5" would never match a figure that is
        // really 12.499999. Compare to the precision the value is displayed at.
        _ => Math.Abs(actual - value) <= Tolerance(value),
      };
    }

    private static double Tolerance(double value) => Math.Max(Math.Abs(value) * 1e-9, 0.05);
  }

  #endregion

  #region parsing

  private sealed class QueryException(string message) : Exception(message);

  private sealed class Parser(string text) {

    private int _position;

    public Node ParseExpression() => this.ParseOr();

    public void ExpectEnd() {
      this.SkipSpace();
      if (this._position < text.Length)
        throw new QueryException($"unexpected '{text[this._position]}' at position {this._position}");
    }

    private Node ParseOr() {
      var left = this.ParseAnd();
      while (this.TryKeyword("OR") || this.TrySymbol("||"))
        left = new OrNode(left, this.ParseAnd());

      return left;
    }

    private Node ParseAnd() {
      var left = this.ParseUnary();
      while (true) {
        if (this.TryKeyword("AND") || this.TrySymbol("&&")) {
          left = new AndNode(left, this.ParseUnary());
          continue;
        }

        // Two terms side by side mean AND, which is what every search box in the world does.
        this.SkipSpace();
        if (this._position >= text.Length || text[this._position] == ')'
            || this.PeeksKeyword("OR") || this.Peeks("||"))
          return left;

        left = new AndNode(left, this.ParseUnary());
      }
    }

    private Node ParseUnary() {
      if (this.TryKeyword("NOT") || this.TrySymbol("!") || this.TrySymbol("-"))
        return new NotNode(this.ParseUnary());

      return this.ParsePrimary();
    }

    private Node ParsePrimary() {
      this.SkipSpace();
      if (this._position >= text.Length)
        throw new QueryException("the query ends where a term was expected");

      if (text[this._position] == '(') {
        ++this._position;
        var inner = this.ParseExpression();
        this.SkipSpace();
        if (this._position >= text.Length || text[this._position] != ')')
          throw new QueryException("a '(' was never closed");

        ++this._position;
        return inner;
      }

      if (text[this._position] == '/')
        return new RegexNode(this.ReadRegex(), null);

      return this.ParseTerm();
    }

    private Node ParseTerm() {
      var start = this._position;
      var word = this.ReadWord();
      if (word.Length == 0)
        throw new QueryException($"expected a term at position {start}");

      // A quoted term is always free text: "chrome AND" searches for that string, not for a query.
      if (this._wasQuoted)
        return new FreeTextNode(word);

      if (!TrySplit(word, out var name, out var op, out var value)) {
        // The operator may be detached from its field — "threads > 1" is what people actually type,
        // and reading it as a search for the word "threads" would be silently wrong rather than
        // loudly wrong.
        if (!this.TryReadDetachedOperator(out op))
          return new FreeTextNode(word);

        name = word;
        value = string.Empty;
      }

      if (!FieldRegistry.TryParse(name, out var field))
        throw new QueryException($"there is no field called '{name}'");

      // The value may be the next token: name:"foo bar", or cpu > 50 with spaces around it.
      if (value.Length == 0) {
        value = this.ReadWord();
        if (value.Length == 0)
          throw new QueryException($"'{name}' has no value to compare against");
      }

      // A value may carry its own operator, which is how "cpu:>50" reads.
      if (!this._wasQuoted && TryLeadingOperator(value, out var inner, out var rest)) {
        op = inner;
        value = rest;
      }

      if (value.Length >= 2 && value[0] == '/' && value[^1] == '/')
        return new RegexNode(Compile(value[1..^1]), field);

      // Which comparison to use comes from the field's declared kind, not from guessing at the
      // value: "pid:1234" is a number because a pid is an identifier, and "name:1234" is text
      // because a name is text, even though both look like digits.
      var descriptor = FieldRegistry.Get(field);
      if (descriptor.Kind is FieldKind.Text or FieldKind.State) {
        // A yes/no field shows "yes" and "no", but nobody agrees on how to type that. §56's own
        // example is "unsigned:true", so the usual spellings are accepted for all of them. A state
        // field whose real values are words ("off", "strict", "filter") is unaffected, because none
        // of them is spelled like a boolean.
        if (descriptor.Kind == FieldKind.State)
          value = value.ToLowerInvariant() switch {
            "true" or "1" or "y" => "yes",
            "false" or "0" or "n" => "no",
            _ => value,
          };

        return new TextComparisonNode(field, op, value);
      }

      if (!Quantity.TryParse(value, descriptor.Unit, out var number))
        throw new QueryException($"'{value}' is not a number {descriptor.Header.ToLowerInvariant()} can be compared to");

      return new NumberComparisonNode(field, op, number);
    }

    private bool _wasQuoted;

    private Regex ReadRegex() {
      ++this._position;
      var start = this._position;
      while (this._position < text.Length && text[this._position] != '/')
        ++this._position;

      if (this._position >= text.Length)
        throw new QueryException("a regular expression was never closed with '/'");

      var pattern = text[start..this._position];
      ++this._position;
      return Compile(pattern);
    }

    private static Regex Compile(string pattern) {
      try {
        // Interpreted, never compiled: RegexOptions.Compiled emits IL at run time, which NativeAOT
        // cannot do (PRD §8.3). A timeout because a filter box is not a place to hang the UI.
        return new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
      } catch (ArgumentException problem) {
        throw new QueryException($"'{pattern}' is not a valid regular expression: {problem.Message}");
      }
    }

    private string ReadWord() {
      this.SkipSpace();
      this._wasQuoted = false;
      if (this._position >= text.Length)
        return string.Empty;

      var quote = text[this._position];
      if (quote is '"' or '\'') {
        ++this._position;
        var from = this._position;
        while (this._position < text.Length && text[this._position] != quote)
          ++this._position;

        if (this._position >= text.Length)
          throw new QueryException("a quoted value was never closed");

        var quoted = text[from..this._position];
        ++this._position;
        this._wasQuoted = true;
        return quoted;
      }

      var start = this._position;
      while (this._position < text.Length && !char.IsWhiteSpace(text[this._position])
             && text[this._position] is not ('(' or ')')) {
        // A quote inside a word ends the word's bare part: name:"foo bar" splits at the quote.
        if (text[this._position] is '"' or '\'')
          break;

        ++this._position;
      }

      return text[start..this._position];
    }

    private void SkipSpace() {
      while (this._position < text.Length && char.IsWhiteSpace(text[this._position]))
        ++this._position;
    }

    private bool TrySymbol(string symbol) {
      this.SkipSpace();
      if (this._position + symbol.Length > text.Length
          || !text.AsSpan(this._position, symbol.Length).SequenceEqual(symbol))
        return false;

      this._position += symbol.Length;
      return true;
    }

    /// <summary>Whether the next symbol is this one, without consuming it.</summary>
    private bool Peeks(string symbol) {
      var saved = this._position;
      var found = this.TrySymbol(symbol);
      this._position = saved;
      return found;
    }

    private bool PeeksKeyword(string keyword) {
      var saved = this._position;
      var found = this.TryKeyword(keyword);
      this._position = saved;
      return found;
    }

    private bool TryKeyword(string keyword) {
      this.SkipSpace();
      if (this._position + keyword.Length > text.Length
          || !text.AsSpan(this._position, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        return false;

      // "ORacle" is a search term, not an OR; a keyword must be a whole word.
      var after = this._position + keyword.Length;
      if (after < text.Length && !char.IsWhiteSpace(text[after]) && text[after] is not ('(' or ')'))
        return false;

      this._position = after;
      return true;
    }

    /// <summary>Splits <c>cpu&gt;50</c> into its name, its operator and whatever followed.</summary>
    private static bool TrySplit(string word, out string name, out QueryOperator op, out string value) {
      name = word;
      op = QueryOperator.Contains;
      value = string.Empty;

      for (var i = 0; i < word.Length; ++i) {
        var length = OperatorAt(word, i, out var found);
        if (length == 0)
          continue;

        if (i == 0)
          return false;

        name = word[..i];
        op = found;
        value = word[(i + length)..];
        return true;
      }

      return false;
    }

    /// <summary>
    /// Consumes an operator standing on its own between a field and its value, so that
    /// <c>threads &gt; 1</c> reads the same as <c>threads&gt;1</c>.
    /// </summary>
    private bool TryReadDetachedOperator(out QueryOperator op) {
      op = QueryOperator.Contains;
      this.SkipSpace();
      if (this._position >= text.Length)
        return false;

      var length = OperatorAt(text, this._position, out op);
      if (length == 0)
        return false;

      this._position += length;
      return true;
    }

    private static bool TryLeadingOperator(string value, out QueryOperator op, out string rest) {
      var length = OperatorAt(value, 0, out op);
      rest = length == 0 ? value : value[length..];
      return length != 0 && rest.Length > 0;
    }

    private static int OperatorAt(string word, int index, out QueryOperator op) {
      op = QueryOperator.Contains;
      if (index >= word.Length)
        return 0;

      // Two-character operators first: ">=" must not be read as ">" followed by a value of "=50".
      if (index + 1 < word.Length) {
        switch (word[index], word[index + 1]) {
          case ('>', '='): op = QueryOperator.GreaterOrEqual; return 2;
          case ('<', '='): op = QueryOperator.LessOrEqual; return 2;
          case ('!', '='): op = QueryOperator.NotEqual; return 2;
          case ('=', '='): op = QueryOperator.Equal; return 2;
        }
      }

      switch (word[index]) {
        case ':': op = QueryOperator.Contains; return 1;
        case '=': op = QueryOperator.Equal; return 1;
        case '>': op = QueryOperator.Greater; return 1;
        case '<': op = QueryOperator.Less; return 1;
        default: return 0;
      }
    }

  }

  #endregion

}
