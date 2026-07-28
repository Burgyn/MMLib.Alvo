using MMLib.Alvo.Schema;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Every <c>format</c> an applied schema can name, as a compiled, anchored, timeout-bounded
/// <see cref="Regex"/> — the built-ins the framework owns plus the patterns the descriptor declared.
/// </summary>
/// <remarks>
/// <para>
/// <b>A caller-supplied value matched against a descriptor-authored pattern is a ReDoS surface, and the
/// pattern's author is not the attacker.</b> A descriptor is written by whoever owns the backend, so the
/// realistic failure is not malice but a pattern with nested quantifiers — <c>(a+)+$</c> is the textbook
/// one — that a hostile <em>value</em> then drives into exponential backtracking on a request path
/// reachable before authorization has any say. Three defences, in order of preference:
/// </para>
/// <list type="number">
///   <item>
///   <b><see cref="RegexOptions.NonBacktracking"/> where the pattern allows it.</b> The .NET
///   non-backtracking engine matches in time linear in the input, so catastrophic backtracking is not
///   slowed down, it is <em>unrepresentable</em>. It refuses to compile a pattern using lookaround,
///   backreferences or atomic groups, which is why it cannot simply be demanded of every pattern.
///   </item>
///   <item>
///   <b>A match timeout where it does not.</b> A pattern the fast engine rejects still compiles on the
///   backtracking one, bounded by <see cref="MatchTimeout"/> — a bound on the damage rather than a
///   prevention of it, which is why it is the fallback and not the default.
///   </item>
///   <item>
///   <b>Anchored, always.</b> A format is a statement about the whole value; an unanchored
///   <c>^[0-9]{8}$</c>-shaped pattern written without its anchors would accept
///   <c>12345678'; DROP …</c> because a substring matched. Anchoring is applied here rather than trusted
///   to the author, and the pattern is wrapped in a non-capturing group first so a top-level alternation
///   (<c>a|b</c>) cannot escape the anchors it was given.
///   </item>
/// </list>
/// <para>
/// <b>Compiled once per applied descriptor, at mapping time.</b> <c>MapAlvoDataApi</c> builds exactly one
/// catalogue from the applied schema and hands it to every endpoint it maps, so no request compiles a
/// pattern and a format shared by twenty fields is one <see cref="Regex"/>. It is captured for the lifetime
/// of the endpoint table, which is the same lifetime the route literals and the <see cref="EntitySchema"/>
/// the endpoints bind against already have — a descriptor re-applied at runtime cannot change any of the
/// three, exactly as <c>EntityRouteCatalog</c> records.
/// </para>
/// <para>
/// A pattern that reaches here is already known to parse: <c>DescriptorToSchemaMapper</c> refuses an
/// unparseable one at apply, where a descriptor mistake belongs. That is why nothing here has a "bad
/// pattern" branch — it would be unreachable, and an unreachable branch is a claim no fact can hold.
/// </para>
/// </remarks>
internal sealed class FormatCatalog
{
    /// <summary>
    /// How long one value may be matched against one pattern before the attempt is abandoned. Only the
    /// backtracking fallback can approach it; a <see cref="RegexOptions.NonBacktracking"/> match is linear
    /// and finishes far inside it.
    /// </summary>
    /// <remarks>
    /// Generous enough that no legitimate value on a machine under load is refused for being slow, and
    /// short enough that a request cannot be made to hold a thread: a pathological pattern driven by a
    /// hostile value would otherwise run until the request was cancelled, which for a keep-alive client is
    /// "never".
    /// </remarks>
    internal static TimeSpan MatchTimeout { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The built-in formats, exactly the enum branch of the descriptor schema's <c>field.format</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one is deliberately written in the subset <see cref="RegexOptions.NonBacktracking"/> accepts
    /// and with no nested quantifier, so the framework's own patterns cannot be the ReDoS the fallback
    /// exists for.
    /// </para>
    /// <para>
    /// They are also deliberately <em>permissive</em>. A format is a typo-catcher, not a deliverability
    /// oracle: RFC 5322's grammar admits addresses no shipped validator accepts, and a stricter pattern
    /// rejects real values — a cost paid by the descriptor author's own users, silently, with a 422 they
    /// cannot argue with. A caller who needs a stricter rule declares a named format and owns it.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> _builtIns = new(StringComparer.Ordinal)
    {
        // A local part, one '@', a dotted domain — the shape a mistyped address fails and a real one passes.
        ["email"] = @"[^@\s]+@[^@\s.]+(\.[^@\s.]+)+",

        // RFC 3986 §3.1's scheme, then anything but whitespace: enough to refuse "example.com" typed into
        // a URI field, without re-litigating the grammar of every scheme.
        ["uri"] = @"[A-Za-z][A-Za-z0-9+.\-]*:\S+",

        // E.164-shaped: an optional '+', then digits and the separators humans type.
        ["phone"] = @"\+?[0-9][0-9 ()./\-]{3,30}",
    };

    private readonly Dictionary<string, Regex> _formats;

    private FormatCatalog(Dictionary<string, Regex> formats) => _formats = formats;

    /// <summary>Compiles every format the applied schema's fields name.</summary>
    /// <param name="entities">The applied schema's entities.</param>
    internal static FormatCatalog Build(IReadOnlyList<EntitySchema> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var formats = new Dictionary<string, Regex>(StringComparer.Ordinal);
        foreach (var field in entities.SelectMany(entity => entity.Fields))
        {
            AddFormatOf(field, formats);
        }

        return new FormatCatalog(formats);
    }

    /// <summary>
    /// Whether <paramref name="value"/> satisfies <paramref name="field"/>'s declared format. A field with
    /// no format, or one whose format this build does not know, is not constrained here.
    /// </summary>
    /// <remarks>
    /// "Does not know" is unreachable in a running host — the mapper refuses an unresolvable format name at
    /// apply — and answering <see langword="true"/> for it is nevertheless the only honest option at this
    /// layer: a validator cannot invent a rule it was never given, and refusing every value would turn a
    /// descriptor mistake into an entity nobody can write to. The fail-closed answer for that condition is
    /// the apply-time refusal, which is where it lives.
    /// </remarks>
    /// <param name="field">The declared field the value was supplied for.</param>
    /// <param name="value">The caller-supplied text.</param>
    internal bool Satisfies(FieldSchema field, string value)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Format is not { } format || !_formats.TryGetValue(format, out var pattern))
        {
            return true;
        }

        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // The backtracking fallback ran out of time on a caller-supplied value. Refusing is the only
            // safe answer: "I could not decide" must not become "it passed", and it must not become a 500
            // either — the request is refused with a violation naming the format, exactly as a value that
            // provably did not match would be. Unreachable for a pattern the linear-time engine accepted.
            return false;
        }
    }

    /// <summary>
    /// Adds one field's format, keyed by name so a format used by twenty fields is compiled once.
    /// </summary>
    /// <remarks>
    /// A field's own <see cref="FieldSchema.FormatPattern"/> wins over a built-in of the same name, and the
    /// mapper already guarantees the two cannot collide: a name it recognises as a built-in never resolves
    /// a pattern. Reading the field's pattern first keeps this method independent of that guarantee rather
    /// than a second statement of it.
    /// </remarks>
    private static void AddFormatOf(FieldSchema field, Dictionary<string, Regex> formats)
    {
        if (field.Format is not { } format || formats.ContainsKey(format))
        {
            return;
        }

        var pattern = field.FormatPattern ?? _builtIns.GetValueOrDefault(format);
        if (pattern is not null)
        {
            formats[format] = Compile(pattern);
        }
    }

    /// <summary>
    /// Compiles one pattern anchored over the whole value, on the non-backtracking engine when it accepts
    /// the pattern and on the timeout-bounded backtracking engine when it does not.
    /// </summary>
    /// <remarks>
    /// <c>\A</c>/<c>\z</c> rather than <c>^</c>/<c>$</c>: the latter pair is line-relative the moment
    /// anything sets <see cref="RegexOptions.Multiline"/>, and <c>$</c> matches before a trailing newline
    /// even without it — so a value ending in <c>\n</c> would satisfy a <c>$</c>-anchored format. Both are
    /// still applied even when the author already wrote their own anchors; an anchor asserted twice matches
    /// exactly what it matched once.
    /// </remarks>
    private static Regex Compile(string pattern)
    {
        var anchored = $@"\A(?:{pattern})\z";
        try
        {
            return new Regex(anchored, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (NotSupportedException)
        {
            // The pattern uses a construct the linear-time engine cannot express (lookaround, a
            // backreference, an atomic group). It is still a valid regular expression — the mapper proved
            // that at apply — so it runs on the backtracking engine, where the timeout is the only bound.
            return new Regex(anchored, RegexOptions.CultureInvariant, MatchTimeout);
        }
    }
}
