namespace MMLib.Alvo.Expressions.Internal;

/// <summary>A single lexical token produced by <see cref="CelLexer"/>.</summary>
/// <param name="Kind">The token's syntactic category.</param>
/// <param name="Text">
/// The token's decoded text — an unescaped string literal's contents, a context reference's name
/// without the leading <c>@</c>, or the raw source substring for everything else.
/// </param>
/// <param name="Position">The zero-based character offset of the token's first character in the source.</param>
internal sealed record CelToken(CelTokenKind Kind, string Text, int Position);

/// <summary>The syntactic category of a <see cref="CelToken"/>.</summary>
internal enum CelTokenKind
{
    /// <summary>A bare name, e.g. a row field or the <c>old</c>/<c>new</c>/<c>changed</c> keywords-by-position.</summary>
    Identifier,

    /// <summary>An Alvo context reference's name without the leading <c>@</c> (<c>user</c> or <c>tenant</c>).</summary>
    ContextReference,

    /// <summary>A quoted, escape-decoded string literal.</summary>
    StringLiteral,

    /// <summary>An integer literal.</summary>
    IntLiteral,

    /// <summary>A decimal literal.</summary>
    DecimalLiteral,

    /// <summary>The <c>true</c> keyword.</summary>
    True,

    /// <summary>The <c>false</c> keyword.</summary>
    False,

    /// <summary>The <c>null</c> keyword.</summary>
    Null,

    /// <summary>The <c>in</c> membership operator.</summary>
    In,

    /// <summary>The <c>has</c> presence-test keyword.</summary>
    Has,

    /// <summary><c>.</c></summary>
    Dot,

    /// <summary><c>,</c></summary>
    Comma,

    /// <summary><c>(</c></summary>
    LeftParen,

    /// <summary><c>)</c></summary>
    RightParen,

    /// <summary>
    /// <c>[</c>. Alvo's profiles have no indexing/list-literal syntax; this exists only so a
    /// bracket anywhere in the source tokenizes instead of aborting the whole scan, letting the
    /// parser reject its actual use with a normal syntax error (or, for <c>@user.claims[...]</c>,
    /// with the dedicated RBAC fix suggestion).
    /// </summary>
    LeftBracket,

    /// <summary><c>]</c>. See <see cref="LeftBracket"/>.</summary>
    RightBracket,

    /// <summary><c>?</c></summary>
    Question,

    /// <summary><c>:</c></summary>
    Colon,

    /// <summary><c>==</c></summary>
    Equal,

    /// <summary><c>!=</c></summary>
    NotEqual,

    /// <summary><c>&lt;</c></summary>
    Less,

    /// <summary><c>&lt;=</c></summary>
    LessOrEqual,

    /// <summary><c>&gt;</c></summary>
    Greater,

    /// <summary><c>&gt;=</c></summary>
    GreaterOrEqual,

    /// <summary><c>&amp;&amp;</c></summary>
    And,

    /// <summary><c>||</c></summary>
    Or,

    /// <summary><c>!</c></summary>
    Not,

    /// <summary><c>+</c></summary>
    Plus,

    /// <summary><c>-</c></summary>
    Minus,

    /// <summary><c>*</c></summary>
    Star,

    /// <summary><c>/</c></summary>
    Slash,

    /// <summary>The end of the source — always the last token <see cref="CelLexer.Tokenize"/> produces.</summary>
    EndOfInput,
}
