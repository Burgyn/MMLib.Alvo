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
/// <see cref="Parse"/> builds the real <see cref="CelNode"/> records straight off the grammar,
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
    /// parenthesised grouping, each level of ternary (<c>?:</c>) chaining, and each level of
    /// unary-operator (<c>!</c>/<c>-</c>) chaining, the three productions whose depth grows with
    /// adversarial input rather than with the fixed number of precedence levels. <c>MaxDepth =
    /// 32</c> means exactly 32 such levels are accepted, combined across all three productions;
    /// this is what stands between a pathological input and a stack overflow.
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>Parses CEL source into an untyped AST.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <exception cref="CelSyntaxException">The source is too long, nests too deeply, or violates the grammar.</exception>
    public static CelNode Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length > MaxSourceLength)
        {
            throw new CelSyntaxException(
                $"CEL expression is {source.Length} characters long, exceeding the maximum of {MaxSourceLength}.",
                MaxSourceLength,
                $"Split the condition across multiple rules/hooks, or shorten it below {MaxSourceLength} characters.");
        }

        var tokens = CelLexer.Tokenize(source);
        return new RecursiveDescentParser(tokens).ParseProgram();
    }

    private sealed class RecursiveDescentParser(IReadOnlyList<CelToken> tokens)
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
                    "Simplify the expression — reduce parenthesised grouping, ternary chaining, or "
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
            RejectExtraArgument("has");
            Expect(CelTokenKind.RightParen);
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
        /// The closed set of identifiers that may be followed by <c>(</c>. It is a <b>positive</b> list on
        /// purpose: an identifier missing from it is refused, so a future function is unavailable until
        /// somebody adds it deliberately, never available because nobody blocked it. Which profiles may use
        /// each entry is not decided here — the parser is profile-blind and the type checker's own
        /// per-construct allow-list refuses <see cref="CelCall"/> outside <see cref="CelProfile.Mutate"/>.
        /// </summary>
        private CelNode ParseCall(CelToken identifierToken) => identifierToken.Text switch
        {
            "changed" => ParseChangedCall(),
            CelCall.LowerAscii => ParseLowerAsciiCall(),
            _ => throw UnrecognizedFunction(identifierToken),
        };

        private static CelSyntaxException UnrecognizedFunction(CelToken identifierToken) =>
            new(
                $"'{identifierToken.Text}' is not a recognized function.",
                identifierToken.Position,
                identifierToken.Text == "lower" ? LowerAsciiSuggestion : MacroNotSupportedSuggestion);

        private CelChanged ParseChangedCall()
        {
            Expect(CelTokenKind.LeftParen);
            var fieldToken = Expect(CelTokenKind.Identifier);
            RejectExtraArgument("changed");
            Expect(CelTokenKind.RightParen);
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
            RejectExtraArgument(CelCall.LowerAscii);
            Expect(CelTokenKind.RightParen);
            return new CelCall(CelCall.LowerAscii, field);
        }

        private void RejectExtraArgument(string functionName)
        {
            if (Current.Kind == CelTokenKind.Comma)
            {
                throw new CelSyntaxException(
                    $"{functionName}() takes exactly one field argument.", Current.Position);
            }
        }

        private CelFieldRef ParseFieldPath(CelToken identifierToken)
        {
            if (identifierToken.Text is not ("old" or "new"))
            {
                throw new CelSyntaxException(
                    "Alvo has no nested field access; use a single field name.",
                    identifierToken.Position,
                    MacroNotSupportedSuggestion);
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
