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
/// <b>An unparseable pattern is refused twice, and the second time is not redundant.</b>
/// <c>DescriptorToSchemaMapper</c> refuses one at apply, where a descriptor mistake belongs — but
/// <see cref="FieldSchema.FormatPattern"/> is a <em>public</em> member, so a <see cref="SchemaModel"/> the
/// mapper never built (a host with its own <c>ISchemaRegistry</c>, a hand-assembled model, F7's dynamic
/// registry) can carry a pattern nothing checked. An earlier remark here claimed that made a bad-pattern
/// branch unreachable; making the member public falsified it, and the branch was <see cref="Regex"/>'s own
/// <see cref="ArgumentException"/> escaping <c>MapAlvoDataApi</c> with no mention of which format was at
/// fault. So <see cref="Build"/> refuses at <em>catalogue-build</em> time — startup, once, naming the format
/// — rather than at the first request that happens to reach the field.
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
    /// <b>The one authority on the built-in formats</b> — which names they are and what each one means —
    /// exactly the enum branch of the descriptor schema's <c>field.format</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Names and patterns together, in one place, because they are one fact.</b> The mapper needs the
    /// <em>names</em> (a name it recognises resolves no pattern from the descriptor's <c>formats</c> block)
    /// and this catalogue needs the <em>patterns</em>, and for one commit they were two hand-written lists
    /// with nothing tying them: deleting <c>uri</c> and <c>phone</c> from the pattern list left the mapper
    /// still accepting both names, so both formats silently validated nothing — a clean build, a green suite,
    /// and a fail-open on caller input. <c>DescriptorToSchemaMapper</c> reads this dictionary's keys, the same
    /// way <c>DescriptorValidator</c> reads <see cref="ReservedQueryKeys"/> rather than restating it.
    /// </para>
    /// <para>
    /// Every pattern is deliberately written in the subset <see cref="RegexOptions.NonBacktracking"/> accepts
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
    internal static IReadOnlyDictionary<string, string> BuiltIns => _builtIns;

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
    /// <remarks>
    /// Called once, from <c>MapAlvoDataApi</c>, so a pattern this build cannot compile fails the host at
    /// startup beside the route-mapping guards rather than per request.
    /// </remarks>
    /// <param name="entities">The applied schema's entities.</param>
    /// <exception cref="InvalidOperationException">
    /// A field carries a <see cref="FieldSchema.FormatPattern"/> that is not a regular expression. Not
    /// reachable through <c>DescriptorToSchemaMapper</c>, which refuses one at apply — but reachable through
    /// any other producer of a <see cref="SchemaModel"/>, since the member is public.
    /// </exception>
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
            // provably did not match would be.
            //
            // Reached only by a pattern NonBacktracking would not compile (a lookaround, a backreference, an
            // atomic group) whose core is catastrophic. That shape is in the suite on purpose — see
            // ValidationTests' 'lookahead-greedy' format — because for one round it was not, and this arm and
            // the fallback that leads to it were both unreached.
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

        if (PatternOf(field) is { } pattern)
        {
            formats[format] = Compile(format, pattern);
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
    private static Regex Compile(string format, string pattern)
    {
        var anchored = $@"\A(?:{pattern})\z";
        try
        {
            return new Regex(anchored, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (NotSupportedException)
        {
            // The pattern uses a construct the linear-time engine cannot express (lookaround, a
            // backreference, an atomic group). It is still a valid regular expression, so it runs on the
            // backtracking engine, where MatchTimeout is the only bound — and Satisfies refuses a value it
            // cannot decide in time rather than admitting it.
            return Backtracking(format, anchored);
        }
        catch (ArgumentException exception)
        {
            throw NotARegularExpression(format, exception);
        }
    }

    /// <summary>
    /// The same anchored pattern, spelled as the ECMA-262 regular expression JSON Schema draft 2020-12
    /// defines <c>pattern</c> in terms of — for the generated OpenAPI document to publish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here, next to <see cref="Compile"/>, because the anchoring is one decision with two spellings.</b>
    /// A published <c>pattern</c> that anchored differently from the pattern this catalogue matches with would
    /// document a rule the API does not enforce — and the pair most likely to drift is exactly the one where
    /// the same semantics need different syntax.
    /// </para>
    /// <para>
    /// <c>^</c>/<c>$</c> rather than <c>\A</c>/<c>\z</c>: those two escapes are not ECMA-262 and would make
    /// the published pattern refuse to compile in a JavaScript, Python or Go validator. The difference
    /// <see cref="Compile"/>'s remarks warn about — <c>$</c> also matching before a trailing newline — is
    /// unavoidable in that dialect, and it errs towards a client accepting a value Alvo will refuse with a
    /// 422, which is the safe direction for a document: it never rejects a value the API would take.
    /// </para>
    /// </remarks>
    /// <param name="pattern">The format's declared pattern, exactly as the schema carries it.</param>
    internal static string AsJsonSchemaPattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return $"^(?:{pattern})$";
    }

    /// <summary>The pattern that enforces one field's format, or <see langword="null"/> when it has none.</summary>
    /// <remarks>
    /// The same resolution <see cref="AddFormatOf"/> performs — the field's own
    /// <see cref="FieldSchema.FormatPattern"/> first, then a built-in of that name — so the document publishes
    /// the pattern this catalogue actually compiled rather than a second guess at which one that was.
    /// </remarks>
    /// <param name="field">The declared field.</param>
    internal static string? PatternOf(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Format is not { } format ? null : field.FormatPattern ?? _builtIns.GetValueOrDefault(format);
    }

    /// <summary>The backtracking fallback, kept separate so its own construction failure is named too.</summary>
    private static Regex Backtracking(string format, string anchored)
    {
        try
        {
            return new Regex(anchored, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException exception)
        {
            throw NotARegularExpression(format, exception);
        }
    }

    /// <summary>
    /// The refusal for a pattern that is not a regular expression, naming the format so the descriptor
    /// author knows which one — family 3 in <c>IAlvoData</c>'s table: an invariant of whoever composed the
    /// schema, never a caller's mistake.
    /// </summary>
    /// <param name="format">The format whose pattern would not compile.</param>
    /// <param name="cause">What <see cref="Regex"/> said about it.</param>
    private static InvalidOperationException NotARegularExpression(string format, Exception cause) => new(
        $"The applied schema declares format '{format}' with a pattern that is not a valid regular "
        + $"expression: {cause.Message} A descriptor is refused at apply, so this schema was composed by "
        + "something else — fix the pattern at its source.",
        cause);
}
