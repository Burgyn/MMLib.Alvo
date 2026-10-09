using MMLib.Alvo.Internal;

using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// A recursive-descent CEL parser, the security boundary between a project descriptor's
/// authored CEL text and the AST a later task type-checks and renders. Accepts exactly the
/// grammar Alvo's profiles allow — never a CEL construct Alvo does not itself implement (list
/// comprehensions, arbitrary macros, indexing) — and never crashes on hostile input: every
/// rejection surfaces as a <see cref="CelSyntaxException"/>, never a raw framework exception or
/// a stack overflow.
/// </summary>
/// <remarks>
/// <see cref="Parse(string, CelFunctionCatalog)"/> builds the real <see cref="CelNode"/> records straight off the grammar,
/// with <see cref="CelValueType.Null"/> placeholders on every <see cref="CelFieldRef"/> — only
/// the type checker (a later task) knows a row field's real type. The tree this returns is
/// therefore <b>not</b> a compiled/renderable expression; only a tree that has since been
/// through the type checker is safe to hand to a SQL renderer.
/// </remarks>
internal static class CelParser
{
    /// <summary>The maximum CEL source length, mirroring the descriptor schema's <c>$defs/cel</c> <c>maxLength</c>.</summary>
    public const int MaxSourceLength = 2000;

    /// <summary>
    /// The maximum number of genuine nesting levels — one unit is counted for each level of
    /// parenthesised grouping, each level of ternary (<c>?:</c>) chaining, each level of
    /// unary-operator (<c>!</c>/<c>-</c>) chaining, and each level of a function call's argument list,
    /// the productions whose depth grows with adversarial input rather than with the fixed number of
    /// precedence levels. <c>MaxDepth = 32</c> means exactly 32 such levels are accepted, combined
    /// across all of them;
    /// this is what stands between a pathological input and a stack overflow.
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>Parses CEL source into an untyped AST, knowing the built-in functions only.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <exception cref="CelSyntaxException">The source is too long, nests too deeply, or violates the grammar.</exception>
    public static CelNode Parse(string source) => Parse(source, CelFunctionCatalog.BuiltIns);

    /// <summary>Parses CEL source into an untyped AST; a name <paramref name="catalog"/> knows parses as a call.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <param name="catalog">The functions this compilation knows.</param>
    /// <exception cref="CelSyntaxException">The source is too long, nests too deeply, or violates the grammar.</exception>
    public static CelNode Parse(string source, CelFunctionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);

        if (source.Length > MaxSourceLength)
        {
            throw new CelSyntaxException(
                $"CEL expression is {source.Length} characters long, exceeding the maximum of {MaxSourceLength}.",
                MaxSourceLength,
                $"Split the condition across multiple rules/hooks, or shorten it below {MaxSourceLength} characters.");
        }

        var tokens = CelLexer.Tokenize(source);
        return new RecursiveDescentParser(tokens, catalog).ParseProgram();
    }

    private sealed class RecursiveDescentParser(IReadOnlyList<CelToken> tokens, CelFunctionCatalog catalog)
    {
        private const string RolesMembershipSuggestion =
            "A caller holds a set of roles — test membership instead: 'editor' in @user.roles";

        private const string ClaimsNotAvailableSuggestion =
            "Typed custom claims are not available yet; they arrive with RBAC (#37). Use @user.roles for now.";

        private const string MacroNotSupportedSuggestion =
            "CEL comprehension macros (all, exists, map, filter) are optional extensions and are not part of any Alvo profile; express row conditions in hooks.beforeUpdate instead.";

        /// <summary>
        /// The fix for <c>lower(x)</c>, which no CEL dialect has: the standard library's own name for the
        /// fold is <c>lowerAscii</c>, and the name is the contract — an ASCII-only fold, not a
        /// culture-sensitive one that would rewrite a stored value beyond recovery.
        /// </summary>
        private const string LowerAsciiSuggestion =
            "CEL spells a lower-case fold lowerAscii, and it folds A-Z only: write lowerAscii(field). A "
            + "Unicode-wide fold also rewrites non-ASCII letters ('Ä' becomes 'ä', 'ẞ' becomes 'ß'), and a "
            + "stored value folded that way is permanently wrong.";

        private static readonly Dictionary<CelTokenKind, CelBinaryOperator> _relationOperators = new()
        {
            [CelTokenKind.Equal] = CelBinaryOperator.Equal,
            [CelTokenKind.NotEqual] = CelBinaryOperator.NotEqual,
            [CelTokenKind.Less] = CelBinaryOperator.Less,
            [CelTokenKind.LessOrEqual] = CelBinaryOperator.LessOrEqual,
            [CelTokenKind.Greater] = CelBinaryOperator.Greater,
            [CelTokenKind.GreaterOrEqual] = CelBinaryOperator.GreaterOrEqual,
            [CelTokenKind.In] = CelBinaryOperator.In,
        };

        private int _index;
        private int _depth;

        public CelNode ParseProgram()
        {
            var node = ParseConditional();
            Expect(CelTokenKind.EndOfInput);
            return node;
        }

        private CelToken Current => tokens[_index];

        private bool Match(CelTokenKind kind)
        {
            if (Current.Kind != kind)
            {
                return false;
            }

            _index++;
            return true;
        }

        private CelToken Expect(CelTokenKind kind)
        {
            if (Current.Kind != kind)
            {
                throw new CelSyntaxException($"Expected {kind} but found {Current.Kind}.", Current.Position);
            }

            var token = Current;
            _index++;
            return token;
        }

        private void EnterNestedProduction()
        {
            if (_depth >= MaxDepth)
            {
                throw new CelSyntaxException(
                    $"CEL expression nests {_depth + 1} levels deep, exceeding the maximum of {MaxDepth}.",
                    Current.Position,
                    "Simplify the expression — reduce parenthesised grouping, nested function calls, ternary chaining, or "
                    + "repeated negation, or split the condition across multiple rules/hooks.");
            }

            _depth++;
        }

        private void ExitNestedProduction() => _depth--;

        private CelNode ParseConditional()
        {
            var condition = ParseOr();
            if (!Match(CelTokenKind.Question))
            {
                return condition;
            }

            var whenTrue = ParseNestedConditional();
            Expect(CelTokenKind.Colon);
            var whenFalse = ParseNestedConditional();
            return new CelConditional(condition, whenTrue, whenFalse);
        }

        private CelNode ParseNestedConditional()
        {
            EnterNestedProduction();
            try
            {
                return ParseConditional();
            }
            finally
            {
                ExitNestedProduction();
            }
        }

        private CelNode ParseOr()
        {
            var left = ParseAnd();
            while (Match(CelTokenKind.Or))
            {
                left = new CelBinary(CelBinaryOperator.Or, left, ParseAnd());
            }

            return left;
        }

        private CelNode ParseAnd()
        {
            var left = ParseRelation();
            while (Match(CelTokenKind.And))
            {
                left = new CelBinary(CelBinaryOperator.And, left, ParseRelation());
            }

            return left;
        }

        private CelNode ParseRelation()
        {
            var left = ParseAdditive();
            if (!_relationOperators.TryGetValue(Current.Kind, out var op))
            {
                return left;
            }

            _index++;
            var result = new CelBinary(op, left, ParseAdditive());

            if (_relationOperators.ContainsKey(Current.Kind))
            {
                throw new CelSyntaxException(
                    "Relational operators do not associate; parenthesize each comparison.", Current.Position);
            }

            return result;
        }

        private CelNode ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (Match(CelTokenKind.Plus))
                {
                    left = new CelBinary(CelBinaryOperator.Add, left, ParseMultiplicative());
                    continue;
                }

                if (Match(CelTokenKind.Minus))
                {
                    left = new CelBinary(CelBinaryOperator.Subtract, left, ParseMultiplicative());
                    continue;
                }

                return left;
            }
        }

        private CelNode ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match(CelTokenKind.Star))
                {
                    left = new CelBinary(CelBinaryOperator.Multiply, left, ParseUnary());
                    continue;
                }

                if (Match(CelTokenKind.Slash))
                {
                    left = new CelBinary(CelBinaryOperator.Divide, left, ParseUnary());
                    continue;
                }

                return left;
            }
        }

        private CelNode ParseUnary()
        {
            if (Match(CelTokenKind.Not))
            {
                return new CelUnary(CelUnaryOperator.Not, ParseNestedUnary());
            }

            if (Match(CelTokenKind.Minus))
            {
                return new CelUnary(CelUnaryOperator.Negate, ParseNestedUnary());
            }

            return ParsePrimary();
        }

        private CelNode ParseNestedUnary()
        {
            EnterNestedProduction();
            try
            {
                return ParseUnary();
            }
            finally
            {
                ExitNestedProduction();
            }
        }

        private CelNode ParsePrimary() => Current.Kind switch
        {
            CelTokenKind.IntLiteral => ParseIntLiteral(),
            CelTokenKind.DecimalLiteral => ParseDecimalLiteral(),
            CelTokenKind.StringLiteral => ParseStringLiteral(),
            CelTokenKind.True => ParseBoolLiteral(CelTokenKind.True, true),
            CelTokenKind.False => ParseBoolLiteral(CelTokenKind.False, false),
            CelTokenKind.Null => ParseNullLiteral(),
            CelTokenKind.ContextReference => ParseContextReference(),
            CelTokenKind.Has => ParseHas(),
            CelTokenKind.Identifier => ParseIdentifierExpression(),
            CelTokenKind.LeftParen => ParseParenthesized(),
            CelTokenKind.LeftBracket => throw new CelSyntaxException(
                "Alvo has no list literals.",
                Current.Position,
                "Use an equality chain instead, e.g. status == 'draft' || status == 'review'."),
            var unexpected => throw new CelSyntaxException($"Unexpected token {unexpected}.", Current.Position),
        };

        private CelNode ParseParenthesized()
        {
            Expect(CelTokenKind.LeftParen);
            var node = ParseNestedGroup();
            Expect(CelTokenKind.RightParen);
            return node;
        }

        private CelNode ParseNestedGroup()
        {
            EnterNestedProduction();
            try
            {
                return ParseConditional();
            }
            finally
            {
                ExitNestedProduction();
            }
        }

        private CelLiteral ParseIntLiteral()
        {
            var token = Expect(CelTokenKind.IntLiteral);
            if (!long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new CelSyntaxException($"Integer literal '{token.Text}' is out of range.", token.Position);
            }

            return new CelLiteral(CelValueType.Int, value);
        }

        private CelLiteral ParseDecimalLiteral()
        {
            var token = Expect(CelTokenKind.DecimalLiteral);
            if (!decimal.TryParse(token.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                throw new CelSyntaxException($"Decimal literal '{token.Text}' is out of range.", token.Position);
            }

            return new CelLiteral(CelValueType.Decimal, value);
        }

        private CelLiteral ParseStringLiteral() => new CelLiteral(CelValueType.String, Expect(CelTokenKind.StringLiteral).Text);

        private CelLiteral ParseBoolLiteral(CelTokenKind kind, bool value)
        {
            Expect(kind);
            return new CelLiteral(CelValueType.Bool, value);
        }

        private CelLiteral ParseNullLiteral()
        {
            Expect(CelTokenKind.Null);
            return new CelLiteral(CelValueType.Null, null);
        }

        private CelContextRef ParseContextReference()
        {
            var contextToken = Expect(CelTokenKind.ContextReference);
            Expect(CelTokenKind.Dot);
            var memberToken = Expect(CelTokenKind.Identifier);
            return BuildContextRef(contextToken, memberToken);
        }

        private static CelContextRef BuildContextRef(CelToken contextToken, CelToken memberToken) =>
            (contextToken.Text, memberToken.Text) switch
            {
                ("user", "id") => new CelContextRef(CelContextValue.UserId, CelValueType.Uuid),
                ("user", "roles") => new CelContextRef(CelContextValue.UserRoles, CelValueType.StringList),
                ("tenant", "id") => new CelContextRef(CelContextValue.TenantId, CelValueType.Uuid),
                ("user", "role") => throw ContextMemberError(contextToken, memberToken, RolesMembershipSuggestion),
                ("user", "claims") => throw ContextMemberError(contextToken, memberToken, ClaimsNotAvailableSuggestion),
                ("user", "teams") => throw ContextMemberError(contextToken, memberToken, ClaimsNotAvailableSuggestion),
                _ => throw ContextMemberError(contextToken, memberToken, null),
            };

        private static CelSyntaxException ContextMemberError(CelToken contextToken, CelToken memberToken, string? suggestion) =>
            new(
                $"'@{contextToken.Text}.{memberToken.Text}' is not a recognized context member.",
                memberToken.Position,
                suggestion);

        private CelHas ParseHas()
        {
            Expect(CelTokenKind.Has);
            Expect(CelTokenKind.LeftParen);
            var field = ParseFieldRefArgument();
            ExpectFieldArgumentEnd("has");
            return new CelHas(field);
        }

        private CelNode ParseIdentifierExpression()
        {
            var identifierToken = Expect(CelTokenKind.Identifier);

            if (Current.Kind == CelTokenKind.LeftParen)
            {
                return ParseCall(identifierToken);
            }

            return ResolveFieldReference(identifierToken);
        }

        private CelFieldRef ParseFieldRefArgument() => ResolveFieldReference(Expect(CelTokenKind.Identifier));

        private CelFieldRef ResolveFieldReference(CelToken identifierToken)
        {
            if (Current.Kind == CelTokenKind.Dot)
            {
                return ParseFieldPath(identifierToken);
            }

            return new CelFieldRef(identifierToken.Text, CelValueType.Null, CelRecordState.Current);
        }

        /// <summary>
        /// The closed set of identifiers that may be followed by <c>(</c>: the three calls with their own grammar, then
        /// whatever the catalog knows. A <b>positive</b> list on purpose — a name missing from it is refused, so a
        /// function is unavailable until somebody catalogues it. Profiles are not decided here: the parser is
        /// profile-blind and the type checker gates every call.
        /// </summary>
        private CelNode ParseCall(CelToken identifierToken) => identifierToken.Text switch
        {
            "changed" => ParseChangedCall(),
            CelCall.LowerAscii => ParseLowerAsciiCall(),
            CelCall.Now => ParseNowCall(),
            var name when catalog.Contains(name) => ParseCatalogCall(identifierToken),
            _ => throw UnrecognizedFunction(identifierToken),
        };

        /// <summary>
        /// Parses <c>name(argument, …)</c> for a catalogued function. Each argument is a whole expression parsed as one
        /// nested level, so call nesting counts against <see cref="MaxDepth"/>; arity is the type checker's question.
        /// </summary>
        private CelCall ParseCatalogCall(CelToken nameToken)
        {
            Expect(CelTokenKind.LeftParen);
            IReadOnlyList<CelNode> arguments = Current.Kind == CelTokenKind.RightParen ? [] : ParseArguments();
            Expect(CelTokenKind.RightParen);
            return new CelCall(nameToken.Text, arguments);
        }

        private List<CelNode> ParseArguments()
        {
            var arguments = new List<CelNode> { ParseNestedGroup() };
            while (Match(CelTokenKind.Comma))
            {
                arguments.Add(ParseNestedGroup());
            }

            return arguments;
        }

        private CelSyntaxException UnrecognizedFunction(CelToken identifierToken) =>
            new(
                $"'{identifierToken.Text}' is not a recognized function.",
                identifierToken.Position,
                UnrecognizedFunctionFix(identifierToken.Text));

        private string UnrecognizedFunctionFix(string name) => name switch
        {
            "lower" => LowerAsciiSuggestion,
            "all" or "exists" or "exists_one" or "map" or "filter" => MacroNotSupportedSuggestion,
            _ => KnownFunctionsSuggestion(name),
        };

        /// <summary>The closest catalogued name, when one is within two edits, and every known name (review G5).</summary>
        private string KnownFunctionsSuggestion(string name)
        {
            var closest = NameSuggestion.Closest(name, catalog.Names);
            var lead = closest is null ? string.Empty : $"Did you mean '{closest}'? ";
            return lead + $"Known functions: {string.Join(", ", catalog.Names)}. A function a host registers with "
                + "AddCelFunction exists only in that host; the standalone image and the CLI know the built-in ones only.";
        }

        private CelChanged ParseChangedCall()
        {
            Expect(CelTokenKind.LeftParen);
            var fieldToken = Expect(CelTokenKind.Identifier);
            ExpectFieldArgumentEnd("changed");
            return new CelChanged(fieldToken.Text);
        }

        /// <summary>
        /// Parses <c>lowerAscii(field)</c>. The argument is a field reference — optionally
        /// <c>old.</c>/<c>new.</c>-qualified — and not an arbitrary expression, the same narrowing
        /// <c>has(field)</c> and <c>changed(field)</c> already use. Admitting a general expression later
        /// accepts strictly more source than this does and so cannot break an authored descriptor; starting
        /// general and narrowing afterwards would.
        /// </summary>
        private CelCall ParseLowerAsciiCall()
        {
            Expect(CelTokenKind.LeftParen);
            var field = ParseFieldRefArgument();
            ExpectFieldArgumentEnd(CelCall.LowerAscii);
            return new CelCall(CelCall.LowerAscii, [field]);
        }

        /// <summary>
        /// Parses <c>now()</c>, which takes no arguments — and says so rather than reporting a missing
        /// identifier, because "now() takes no arguments" is the sentence that tells an author the value is
        /// the write's own instant and not something they get to choose.
        /// </summary>
        private CelCall ParseNowCall()
        {
            Expect(CelTokenKind.LeftParen);

            if (Current.Kind != CelTokenKind.RightParen)
            {
                throw new CelSyntaxException(
                    $"{CelCall.Now}() takes no arguments.",
                    Current.Position,
                    $"Write {CelCall.Now}() — it resolves to the instant the write itself is stamped with.");
            }

            Expect(CelTokenKind.RightParen);
            return new CelCall(CelCall.Now, []);
        }

        /// <summary>Closes a field-only call (<c>has</c>, <c>changed</c>, <c>lowerAscii</c>) after its one field.</summary>
        /// <param name="functionName">The field-only call being parsed.</param>
        private void ExpectFieldArgumentEnd(string functionName)
        {
            RejectNestedCall(functionName);
            RejectExtraArgument(functionName);
            Expect(CelTokenKind.RightParen);
        }

        /// <summary>
        /// Refuses a call where a field-only call wants its field — <c>lowerAscii(trim(name))</c>. The message and
        /// position are exactly the token mismatch this always reported (the corpus pins them); the fix is the point.
        /// </summary>
        /// <param name="functionName">The field-only call being parsed.</param>
        private void RejectNestedCall(string functionName)
        {
            if (Current.Kind == CelTokenKind.LeftParen)
            {
                throw new CelSyntaxException(
                    $"Expected {CelTokenKind.RightParen} but found {CelTokenKind.LeftParen}.",
                    Current.Position,
                    FieldOnlyCallFix(functionName, tokens[_index - 1].Text));
            }
        }

        /// <summary>What to write instead of a call inside a field-only call; names the inner call when it can be nested.</summary>
        /// <param name="functionName">The field-only call.</param>
        /// <param name="inner">The name written where the field belongs.</param>
        private string FieldOnlyCallFix(string functionName, string inner)
        {
            var nestable = catalog.Contains(inner) && inner is not (CelCall.LowerAscii or CelCall.Now);
            var reads = nestable ? $"the field {inner}(...) reads" : "the field itself";
            return functionName switch
            {
                "has" => $"has takes one field reference, never a call: write has(field) for {reads}; a call's result is "
                    + "compared, never tested with has.",
                "changed" => $"changed takes one field reference, never a call: write changed(field) for {reads}"
                    + (nestable ? $", or compare the results directly, e.g. {inner}(old.field) != {inner}(new.field)." : "."),
                _ => $"lowerAscii takes a field, never a call: write lowerAscii(field) for {reads}"
                    + (nestable
                        ? ". A function takes any expression, so nest the other way when that means the same, e.g. "
                            + $"{inner}(lowerAscii(field)), or write {inner}(...)'s result into a field with a mutate and fold that field."
                        : "."),
            };
        }

        private void RejectExtraArgument(string functionName)
        {
            if (Current.Kind == CelTokenKind.Comma)
            {
                throw new CelSyntaxException(
                    $"{functionName}() takes exactly one field argument.", Current.Position);
            }
        }

        /// <summary>
        /// The fix for <c>x.trim()</c> or <c>math.abs(x)</c> — CEL's receiver and namespaced spellings (deviation F1):
        /// when the member after the dot is a catalogued function followed by <c>(</c>, say how Alvo spells the call.
        /// </summary>
        private string NestedAccessFix() =>
            ReceiverCallName() is { } function && catalog.Contains(function)
                ? $"Write {function}(...) with the value as an argument: Alvo calls a function as {function}(x), never "
                    + $"as x.{function}() or with a namespace such as math.{function}(x)."
                : MacroNotSupportedSuggestion;

        private string? ReceiverCallName() =>
            _index + 2 < tokens.Count
            && tokens[_index + 1].Kind == CelTokenKind.Identifier
            && tokens[_index + 2].Kind == CelTokenKind.LeftParen
                ? tokens[_index + 1].Text
                : null;

        private CelFieldRef ParseFieldPath(CelToken identifierToken)
        {
            if (identifierToken.Text is not ("old" or "new"))
            {
                throw new CelSyntaxException(
                    "Alvo has no nested field access; use a single field name.",
                    identifierToken.Position,
                    NestedAccessFix());
            }

            Expect(CelTokenKind.Dot);
            var fieldToken = Expect(CelTokenKind.Identifier);

            if (Current.Kind == CelTokenKind.Dot)
            {
                throw new CelSyntaxException(
                    "Alvo has no nested field access beyond old./new.; use a single field name.", Current.Position);
            }

            var state = identifierToken.Text == "old" ? CelRecordState.Old : CelRecordState.New;
            return new CelFieldRef(fieldToken.Text, CelValueType.Null, state);
        }
    }
}
