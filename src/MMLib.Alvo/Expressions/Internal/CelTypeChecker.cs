using MMLib.Alvo.Internal;
using MMLib.Alvo.Schema;
using System.Collections.Immutable;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Walks a parsed CEL tree once, resolving every <see cref="CelFieldRef"/> against an entity's
/// schema, enforcing the type rules for every node family, and rejecting any construct the active
/// <see cref="CelProfile"/> does not allow — all in one pass, collecting every independent error
/// instead of stopping at the first.
/// </summary>
internal static class CelTypeChecker
{
    /// <summary>Checks a parsed tree against an entity's schema and a profile, knowing only the built-in functions.</summary>
    /// <param name="root">The parsed, untyped tree.</param>
    /// <param name="source">The original CEL source, used only to locate positions.</param>
    /// <param name="entity">The entity to resolve row fields against.</param>
    /// <param name="profile">Which constructs are legal.</param>
    /// <returns>The rewritten tree, its result type, the result-type anchor position and every error.</returns>
    public static (CelNode Root, CelValueType ResultType, int Position, IReadOnlyList<CelCompilationError> Errors) Check(
        CelNode root, string source, EntitySchema entity, CelProfile profile) =>
        Check(root, source, entity, profile, CelFunctionCatalog.BuiltIns);

    /// <summary>Checks a parsed tree against an entity's schema, a profile and the functions this compilation knows.</summary>
    /// <param name="root">The parsed, untyped tree.</param>
    /// <param name="source">
    /// The original CEL source. Used only to locate an offending identifier's position for an
    /// error — the tree itself carries no position information.
    /// </param>
    /// <param name="entity">The entity to resolve row fields against.</param>
    /// <param name="profile">Which constructs are legal.</param>
    /// <param name="catalog">The functions a call may bind.</param>
    /// <returns>
    /// The rewritten tree (every <see cref="CelFieldRef"/> carries its resolved type), the whole
    /// expression's result type, the position its result-type check should be anchored to, and
    /// every error found.
    /// </returns>
    public static (CelNode Root, CelValueType ResultType, int Position, IReadOnlyList<CelCompilationError> Errors) Check(
        CelNode root, string source, EntitySchema entity, CelProfile profile, CelFunctionCatalog catalog)
    {
        var visitor = new Visitor(source, entity, profile, catalog);
        var (node, type, _, position) = visitor.CheckNode(root);
        return (node, type, position, visitor.Errors);
    }

    /// <summary>
    /// A construct whose legality varies by <see cref="CelProfile"/>. Every <see cref="CelNode"/>
    /// kind (disambiguated by operator where one node type covers several constructs) maps to
    /// exactly one of these, and <see cref="_allowedProfiles"/> is the single positive table that
    /// decides where each one is legal — deny by default, so a kind missing from the table (a
    /// future construct nobody wired up yet) compiles in no profile rather than every profile.
    /// </summary>
    private enum CelConstructKind
    {
        Literal,
        FieldRefCurrent,
        FieldRefPastFuture,
        ContextRefUser,
        ContextRefTenant,
        Logical,
        Comparison,
        In,
        Has,
        Arithmetic,
        Concatenation,
        Conditional,
        Changed,
        Call,

        /// <summary>A call to a catalogued function; the row is the ceiling, each function's own profiles narrow it.</summary>
        FunctionCall,
    }

    /// <summary>
    /// Rule, Computed and Condition — the three profiles that predate <see cref="CelProfile.Mutate"/>.
    /// Deliberately <b>not</b> "every profile": <see cref="CelProfile.Mutate"/> joins a row of
    /// <see cref="_allowedProfiles"/> one at a time, with the fact that needs it, so the table never grants
    /// a construct to a profile no test has exercised there.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _ruleComputedCondition =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Computed, CelProfile.Condition };

    /// <summary>
    /// Rule, Computed, Condition <em>and</em> <see cref="CelProfile.Access"/> — the profiles that may hold a
    /// whole predicate's worth of operators. <see cref="CelProfile.Mutate"/> is deliberately absent, exactly
    /// as it is from <see cref="_ruleComputedCondition"/>.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _ruleComputedConditionAndAccess =
        new HashSet<CelProfile>
        {
            CelProfile.Rule, CelProfile.Computed, CelProfile.Condition, CelProfile.Access,
        };

    private static readonly IReadOnlySet<CelProfile> _everyProfile =
        new HashSet<CelProfile>
        {
            CelProfile.Rule, CelProfile.Computed, CelProfile.Condition, CelProfile.Mutate, CelProfile.Access,
        };

    /// <summary>
    /// The four profiles evaluated against a row. <see cref="CelProfile.Access"/> is deliberately absent:
    /// an access level is a predicate over the caller alone, so a field reference there would compile and
    /// then have nothing to read.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _rowProfiles =
        new HashSet<CelProfile>
        {
            CelProfile.Rule, CelProfile.Computed, CelProfile.Condition, CelProfile.Mutate,
        };

    private static readonly IReadOnlySet<CelProfile> _computedOnly = new HashSet<CelProfile> { CelProfile.Computed };

    private static readonly IReadOnlySet<CelProfile> _conditionOnly = new HashSet<CelProfile> { CelProfile.Condition };

    private static readonly IReadOnlySet<CelProfile> _mutateOnly = new HashSet<CelProfile> { CelProfile.Mutate };

    private static readonly IReadOnlySet<CelProfile> _ruleAndCondition =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Condition };

    /// <summary>
    /// The two caller-aware profiles plus <see cref="CelProfile.Access"/>. It carries <c>@user</c> and
    /// <c>in</c> (role membership), which are the whole of what an access level reads; <c>@tenant</c> has
    /// its own row and stays on <see cref="_ruleAndCondition"/>.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _ruleConditionAndAccess =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Condition, CelProfile.Access };

    /// <summary>
    /// A hook <c>condition</c> and a before-hook <c>mutate</c> are the two slots evaluated against a
    /// candidate row, so they are the two that may name the row's before/after images.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _conditionAndMutate =
        new HashSet<CelProfile> { CelProfile.Condition, CelProfile.Mutate };

    /// <summary>
    /// The one positive table that decides where each construct is legal. <see cref="CelProfile.Mutate"/>
    /// holds five rows today — literals, current-row and <c>old.</c>/<c>new.</c> field references, the
    /// allow-listed legacy call and catalogued function calls — which is exactly what its functions and their
    /// arguments need; <see cref="CelProfile.Condition"/> also holds the catalogued function call. The
    /// remaining rows (logical, comparison, <c>in</c>, <c>has</c>, arithmetic, ternary, <c>changed</c>,
    /// context references) are <b>not</b> a decision that <c>mutate</c> may never use them; they are simply
    /// not admitted yet, and each arrives with the fact that needs it — a before-hook <c>mutate</c> like
    /// <c>new.stage == 'won'</c> will bring the comparison row with it. Deny-by-default is what makes that
    /// safe to defer: an unlisted pairing compiles in no profile rather than in every one.
    /// </summary>
    private static readonly Dictionary<CelConstructKind, IReadOnlySet<CelProfile>> _allowedProfiles =
        new()
        {
            [CelConstructKind.Literal] = _everyProfile,
            [CelConstructKind.FieldRefCurrent] = _rowProfiles,
            [CelConstructKind.FieldRefPastFuture] = _conditionAndMutate,
            [CelConstructKind.ContextRefUser] = _ruleConditionAndAccess,
            [CelConstructKind.ContextRefTenant] = _ruleAndCondition,
            [CelConstructKind.Logical] = _ruleComputedConditionAndAccess,
            [CelConstructKind.Comparison] = _ruleComputedConditionAndAccess,
            [CelConstructKind.In] = _ruleConditionAndAccess,
            [CelConstructKind.Has] = _ruleComputedCondition,
            [CelConstructKind.Arithmetic] = _computedOnly,
            [CelConstructKind.Concatenation] = _computedOnly,
            [CelConstructKind.Conditional] = _computedOnly,
            [CelConstructKind.Changed] = _conditionOnly,
            [CelConstructKind.Call] = _mutateOnly,
            [CelConstructKind.FunctionCall] = _conditionAndMutate,
        };

    /// <summary>
    /// The profiles <see cref="SqlPredicateRenderer"/> turns into SQL — a rule becomes a <c>WHERE</c> clause, a
    /// computed field a generated column. Only these two are held to the operand-shape rules below; the
    /// interpreter-only profiles evaluate any well-typed tree, so narrowing them would refuse a working hook for
    /// a backend it never meets.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _sqlRenderedProfiles =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Computed };

    private static bool IsAllowed(CelProfile profile, CelConstructKind kind) =>
        _allowedProfiles.TryGetValue(kind, out var profiles) && profiles.Contains(profile);

    private sealed class Visitor(string source, EntitySchema entity, CelProfile profile, CelFunctionCatalog catalog)
    {
        private const string RoleMembershipFixSuggestion =
            "A caller holds a set of roles; test membership instead, e.g. 'editor' in @user.roles.";

        private const string ComputedNoContextMessage =
            "A computed column is evaluated by the database with no caller context.";

        private const string NullPresenceFixSuggestion =
            "Use has(field) to test presence, or !has(field) to test absence.";

        private const string AccessNoRowMessage =
            "An access level is a predicate over the caller alone; a field reference has no row to read here.";

        private const string AccessProjectScopedMessage =
            "'@tenant.id' is not legal in an access level: an access level is project-scoped, not tenant-scoped.";

        private int _cursor;

        /// <summary>
        /// The fields a presence test has established inside the branch being checked — <c>f</c> within the
        /// <c>WhenTrue</c> of <c>has(f) ? … : …</c>, or the <c>WhenFalse</c> of <c>!has(f) ? … : …</c>. Scoped to that
        /// branch: <see cref="CheckGuardedBranch"/> restores it on the way out, so a read after the ternary is
        /// unguarded again.
        /// </summary>
        private ImmutableHashSet<string> _knownPresent = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

        public List<CelCompilationError> Errors { get; } = [];

        public (CelNode Node, CelValueType Type, bool HasError, int Position) CheckNode(CelNode node) => node switch
        {
            CelLiteral literal => CheckLiteral(literal),
            CelFieldRef fieldRef => CheckFieldRef(fieldRef),
            CelContextRef contextRef => CheckContextRef(contextRef),
            CelUnary unary => CheckUnary(unary),
            CelBinary binary => CheckBinary(binary),
            CelHas has => CheckHas(has),
            CelConditional conditional => CheckConditional(conditional),
            CelChanged changed => CheckChanged(changed),
            CelCall call => CheckCall(call),
            _ => UnrecognizedNode(node),
        };

        private (CelNode, CelValueType, bool, int) UnrecognizedNode(CelNode node)
        {
            Errors.Add(new CelCompilationError(
                $"'{node.GetType().Name}' is not a supported CEL construct in this compiler.",
                null,
                _cursor));
            return (node, CelValueType.Null, true, _cursor);
        }

        private (CelNode, CelValueType, bool, int) CheckLiteral(CelLiteral literal)
        {
            var profileBad = CheckConstruct(CelConstructKind.Literal, "Literals are not legal in this profile.", null, _cursor);
            var textBad = profile == CelProfile.Computed && RequireDdlText(literal);
            return (literal, literal.Type, profileBad || textBad, _cursor);
        }

        /// <summary>
        /// Refuses a text constant a generated column's DDL cannot carry: a control character (C0, DEL, C1 — a
        /// line break or a tab included), the Unicode line and paragraph separators (U+2028, U+2029 — line breaks
        /// that are not controls), or an unpaired UTF-16 surrogate, which is not text at all.
        /// </summary>
        /// <remarks>
        /// <b>Engine-neutral, and before any dialect sees it.</b> A computed field's constants are written inline
        /// into DDL (a column definition has no bind-parameter form), so each dialect's literal quoting refuses the
        /// same characters as a belt; stating the rule here is what makes the refusal a structured one at the
        /// field's pointer, identical on every engine, instead of a driver's silence.
        /// </remarks>
        private bool RequireDdlText(CelLiteral literal)
        {
            if (literal is not { Type: CelValueType.String, Value: string text } || FirstNonText(text) is not { } offender)
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                "A text constant in a computed field is written into the column's DDL, which carries no control "
                + $"character, line or paragraph separator, or unpaired surrogate; this one holds U+{(int)offender:X4}.",
                "Remove that character from the constant: a line break (U+2028 and U+2029 included), a tab or a "
                + "control code cannot be part of a computed text value.",
                _cursor));
            return true;
        }

        private static char? FirstNonText(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (char.IsControl(text[index]) || IsLineOrParagraphSeparator(text[index]) || IsUnpairedSurrogate(text, index))
                {
                    return text[index];
                }

                index += char.IsHighSurrogate(text[index]) ? 1 : 0;
            }

            return null;
        }

        private static bool IsUnpairedSurrogate(string text, int index) =>
            char.IsSurrogate(text[index]) && !char.IsSurrogatePair(text, index);

        // Zl and Zp: each is a line break to a reader, but neither is a control, so IsControl alone admits them.
        private static bool IsLineOrParagraphSeparator(char character) => character is '\u2028' or '\u2029';

        /// <summary>
        /// Whether this profile admits a field reference in <em>any</em> state. Only
        /// <see cref="CelProfile.Access"/> admits none, and that is what lets the field-reference and
        /// <c>changed(...)</c> arms stop before resolving a name against an entity there is none of —
        /// which would otherwise add a second, misleading "not a field of entity '&lt;project&gt;'" error
        /// to every access level that names a column.
        /// </summary>
        private bool ProfileReadsARow =>
            IsAllowed(profile, CelConstructKind.FieldRefCurrent)
            || IsAllowed(profile, CelConstructKind.FieldRefPastFuture);

        private (CelNode, CelValueType, bool, int) CheckFieldRef(CelFieldRef fieldRef)
        {
            var position = FindPosition(fieldRef.FieldName);
            var kind = fieldRef.State == CelRecordState.Current
                ? CelConstructKind.FieldRefCurrent
                : CelConstructKind.FieldRefPastFuture;
            var stateBad = CheckConstruct(kind, FieldRefRefusal(fieldRef), FieldRefFix(), position);

            if (!ProfileReadsARow)
            {
                return (fieldRef, CelValueType.Null, true, position);
            }

            var field = ResolveField(fieldRef.FieldName);
            if (field is null)
            {
                Errors.Add(new CelCompilationError(
                    $"'{fieldRef.FieldName}' is not a field of entity '{entity.Name}'.",
                    BuildUnknownFieldSuggestion(fieldRef.FieldName),
                    position));
                return (fieldRef, CelValueType.Null, true, position);
            }

            if (!IsKnownFieldType(field.Type))
            {
                Errors.Add(new CelCompilationError(
                    $"Field '{fieldRef.FieldName}' has an unrecognized type ({field.Type}) in the schema.",
                    "This indicates a corrupt or unsupported schema; fix the entity's field type.",
                    position));
                return (fieldRef, CelValueType.Null, true, position);
            }

            var type = CelFieldType.Of(field.Type);
            return (fieldRef with { Type = type }, type, stateBad, position);
        }

        private string FieldRefRefusal(CelFieldRef fieldRef) =>
            ProfileReadsARow
                ? $"'{StatePrefix(fieldRef.State)}{fieldRef.FieldName}' is legal only in the "
                    + $"{CelProfile.Condition} and {CelProfile.Mutate} profiles (a hook condition and a "
                    + "before-hook mutate value) — the two slots evaluated against a candidate row."
                : AccessNoRowMessage;

        private string FieldRefFix() =>
            ProfileReadsARow
                ? "Reference the current row instead, or move this into a hook condition or a before-hook mutate."
                : "Test the caller instead, e.g. 'manager' in @user.roles.";

        private (CelNode, CelValueType, bool, int) CheckContextRef(CelContextRef contextRef)
        {
            if (ContextRefKind(contextRef.Value) is not { } kind)
            {
                return UnrecognizedNode(contextRef);
            }

            var position = FindPosition(ContextRefText(contextRef));
            var profileBad = CheckConstruct(kind, ContextRefRefusal(), ContextRefFix(), position);

            return (contextRef, contextRef.Type, profileBad, position);
        }

        /// <summary>
        /// Which construct row a context value sits on, or <see langword="null"/> when it sits on none.
        /// </summary>
        /// <remarks>
        /// <b>An unmapped value falls out as unrecognised rather than onto the user row, and the direction
        /// is the whole point.</b> A two-way test against <see cref="CelContextValue.TenantId"/> would put
        /// every value added later on <see cref="CelConstructKind.ContextRefUser"/> — which
        /// <see cref="CelProfile.Access"/> <em>admits</em> — so a tenant-shaped member (<c>@tenant.plan</c>,
        /// an organisation id) would become silently legal in an access level, making a project-scoped
        /// predicate answer differently per request by default instead of by decision. Refusing the
        /// unmapped case in every profile is the deny-by-default this table exists to enforce.
        /// </remarks>
        private static CelConstructKind? ContextRefKind(CelContextValue value) => value switch
        {
            CelContextValue.UserId or CelContextValue.UserRoles => CelConstructKind.ContextRefUser,
            CelContextValue.TenantId => CelConstructKind.ContextRefTenant,
            _ => null,
        };

        private string ContextRefRefusal() =>
            profile == CelProfile.Access ? AccessProjectScopedMessage : ComputedNoContextMessage;

        private string ContextRefFix() =>
            profile == CelProfile.Access
                ? "Test role membership or the caller's identity instead, e.g. 'manager' in @user.roles."
                : "Move the caller-dependent check into a rule or a hook condition.";

        private (CelNode, CelValueType, bool, int) CheckUnary(CelUnary unary)
        {
            var (operand, operandType, operandError, position) = CheckNode(unary.Operand);
            var rewritten = unary with { Operand = operand };

            return unary.Operator switch
            {
                CelUnaryOperator.Not => CheckLogicalNot(rewritten, operandType, operandError, position),
                CelUnaryOperator.Negate => CheckNegate(rewritten, operandType, operandError, position),
                _ => UnrecognizedNode(rewritten),
            };
        }

        private (CelNode, CelValueType, bool, int) CheckLogicalNot(CelUnary unary, CelValueType operandType, bool operandError, int position)
        {
            var profileBad = CheckConstruct(CelConstructKind.Logical, "'!' is not legal in this profile.", null, position);
            var operandBad = RequireBool(operandType, operandError, "'!' operand", position);
            return (unary, CelValueType.Bool, profileBad || operandBad, position);
        }

        private (CelNode, CelValueType, bool, int) CheckNegate(CelUnary unary, CelValueType operandType, bool operandError, int position)
        {
            var profileBad = CheckConstruct(
                CelConstructKind.Arithmetic,
                "Arithmetic negation ('-') is legal only in the Computed profile.",
                "Move this calculation into a computed field.",
                position);
            var operandBad = RequireNumeric(operandType, operandError, "Unary '-' operand", position);
            var resultType = operandError ? CelValueType.Decimal : operandType;
            return (unary, resultType, profileBad || operandBad, position);
        }

        private (CelNode, CelValueType, bool, int) CheckBinary(CelBinary binary)
        {
            var (left, leftType, leftError, leftPosition) = CheckNode(binary.Left);
            var (right, rightType, rightError, rightPosition) = CheckNode(binary.Right);
            var rewritten = binary with { Left = left, Right = right };

            return binary.Operator switch
            {
                CelBinaryOperator.And or CelBinaryOperator.Or =>
                    CheckLogical(rewritten, leftType, rightType, leftError, rightError, leftPosition, rightPosition),
                CelBinaryOperator.In =>
                    CheckIn(rewritten, leftType, rightType, leftError, rightError, rightPosition),
                CelBinaryOperator.Add or CelBinaryOperator.Subtract or CelBinaryOperator.Multiply or CelBinaryOperator.Divide =>
                    CheckArithmetic(rewritten, leftType, rightType, leftError, rightError, leftPosition, rightPosition),
                CelBinaryOperator.Equal or CelBinaryOperator.NotEqual or CelBinaryOperator.Less
                    or CelBinaryOperator.LessOrEqual or CelBinaryOperator.Greater or CelBinaryOperator.GreaterOrEqual =>
                    CheckComparison(rewritten, binary.Operator, leftType, rightType, leftError, rightError, rightPosition),
                _ => UnrecognizedNode(rewritten),
            };
        }

        private (CelNode, CelValueType, bool, int) CheckLogical(
            CelBinary binary, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError, int leftPosition, int rightPosition)
        {
            var profileBad = CheckConstruct(CelConstructKind.Logical, "'&&'/'||' are not legal in this profile.", null, rightPosition);
            var leftBad = RequireBool(leftType, leftError, "'&&'/'||' left operand", leftPosition);
            var rightBad = RequireBool(rightType, rightError, "'&&'/'||' right operand", rightPosition);
            return (binary, CelValueType.Bool, profileBad || leftBad || rightBad, rightPosition);
        }

        private (CelNode, CelValueType, bool, int) CheckIn(
            CelBinary binary, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError, int position)
        {
            var profileBad = CheckConstruct(
                CelConstructKind.In,
                $"'in' (role membership) is not available in the Computed profile: {ComputedNoContextMessage}",
                "Move this check into a rule or a hook condition.",
                position);

            if (leftError || rightError)
            {
                return (binary, CelValueType.Bool, true, position);
            }

            if (leftType == CelValueType.String && rightType == CelValueType.StringList)
            {
                return (binary, CelValueType.Bool, profileBad, position);
            }

            Errors.Add(new CelCompilationError(
                $"'in' requires a string on the left and a role list (@user.roles) on the right; found {leftType} and {rightType}.",
                "Compare a string field or literal on the left against @user.roles on the right.",
                position));
            return (binary, CelValueType.Bool, true, position);
        }

        private (CelNode, CelValueType, bool, int) CheckArithmetic(
            CelBinary binary, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError, int leftPosition, int rightPosition)
        {
            if (IsConcatenation(binary.Operator, leftType, rightType, leftError, rightError))
            {
                return CheckConcatenation(binary, leftType, rightType, leftError, rightError, leftPosition, rightPosition);
            }

            var profileBad = CheckConstruct(
                CelConstructKind.Arithmetic,
                $"Arithmetic is legal only in the Computed profile; '{OperatorText(binary.Operator)}' is not allowed here.",
                "Move this calculation into a computed field.",
                rightPosition);

            var leftBad = RequireNumeric(leftType, leftError, "Arithmetic left operand", leftPosition);
            var rightBad = RequireNumeric(rightType, rightError, "Arithmetic right operand", rightPosition);
            var resultType = leftType == CelValueType.Decimal || rightType == CelValueType.Decimal
                ? CelValueType.Decimal
                : CelValueType.Int;

            return (binary, resultType, profileBad || leftBad || rightBad, rightPosition);
        }

        /// <summary>
        /// Whether this <c>+</c> is CEL's <c>(string, string) → string</c> overload rather than arithmetic: a healthy
        /// string operand on either side makes it one, so a mixed pair is reported as the concatenation it was
        /// meant to be, with a conversion fix, rather than as "the left operand must be numeric".
        /// </summary>
        private static bool IsConcatenation(
            CelBinaryOperator op, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError) =>
            op == CelBinaryOperator.Add
            && ((leftType == CelValueType.String && !leftError) || (rightType == CelValueType.String && !rightError));

        /// <summary>
        /// CEL's <c>+</c> over two strings. Left-associative like every <c>+</c>, so <c>a + ' ' + b</c> is
        /// <c>(a + ' ') + b</c> and each join is checked on its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>No implicit conversion (CEL spec: there is no <c>(int, string)</c> overload).</b> A mixed pair is a type
        /// error, and its fix says this profile has no <c>string()</c> to reach for.
        /// </para>
        /// <para>
        /// <b>The null rule is a refusal.</b> CEL's <c>+</c> has no null overload — a null operand is an evaluation
        /// error — while SQL's <c>||</c> answers <c>NULL</c> for the whole value when any operand is. Rather than
        /// picking one of the two and diverging from the other, an operand that can be null is refused here, with
        /// the explicit fallback in the profile's own syntax as the fix; see <see cref="IsNeverNull"/> for what counts
        /// as never null.
        /// </para>
        /// </remarks>
        private (CelNode, CelValueType, bool, int) CheckConcatenation(
            CelBinary binary, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError, int leftPosition, int rightPosition)
        {
            var profileBad = CheckConstruct(
                CelConstructKind.Concatenation,
                "String concatenation ('+' over two strings) is legal only in the Computed profile.",
                "Join the text in a computed field, and compare that field here instead.",
                rightPosition);
            var mismatch = RequireTwoStrings(leftType, rightType, leftError, rightError, rightPosition);
            var nullBad = !profileBad && !mismatch
                && (RequireNeverNull(binary.Left, leftError, leftPosition) | RequireNeverNull(binary.Right, rightError, rightPosition));

            return (binary, CelValueType.String, profileBad || mismatch || nullBad || leftError || rightError, rightPosition);
        }

        private bool RequireTwoStrings(CelValueType leftType, CelValueType rightType, bool leftError, bool rightError, int position)
        {
            if (leftError || rightError || (leftType == CelValueType.String && rightType == CelValueType.String))
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"'+' joins two strings or adds two numbers; found {leftType} and {rightType}, and CEL converts "
                + "neither implicitly.",
                "Join two string fields or string constants (first_name + ' ' + last_name). A computed field has no "
                + "string() conversion, so keep the number in a field of its own.",
                position));
            return true;
        }

        private bool RequireNeverNull(CelNode operand, bool operandError, int position)
        {
            if (operandError || IsNeverNull(operand))
            {
                return false;
            }

            Errors.Add(operand is CelFieldRef fieldRef ? NullableFieldOperand(fieldRef.FieldName, position) : NullableOperand(position));
            return true;
        }

        private static CelCompilationError NullableFieldOperand(string fieldName, int position) => new(
            $"'+' would join '{fieldName}', which may be null: CEL's '+' has no null overload, and SQL's '||' makes "
            + "the whole value NULL when any part is.",
            $"Make '{fieldName}' required, or write the fallback explicitly: (has({fieldName}) ? {fieldName} : ''), or "
            + $"(has({fieldName}) ? ' ' + {fieldName} : '') to join a separator only when it is there.",
            position);

        private static CelCompilationError NullableOperand(int position) => new(
            "An operand of '+' may be null: CEL's '+' has no null overload, and SQL's '||' makes the whole value NULL "
            + "when any part is.",
            "Give every branch a value that is never null, e.g. (has(middle_name) ? middle_name : '').",
            position);

        /// <summary>
        /// Whether an already-checked string operand can never be null: a constant; a field whose column is
        /// <c>NOT NULL</c>; a healthy join (its own operands passed this same test); or a ternary both of whose
        /// branches are — where a field on the branch its own presence test guards
        /// (<c>has(f) ? f : …</c>, <c>!has(f) ? … : f</c>) counts, because that is exactly the coalescing construct
        /// this profile already has — and a field read anywhere inside the branch its presence test guards
        /// (<see cref="_knownPresent"/>), so <c>has(m) ? a + ' ' + m : a</c> is accepted too.
        /// </summary>
        private bool IsNeverNull(CelNode node) => node switch
        {
            CelLiteral literal => literal.Value is not null,
            CelFieldRef fieldRef =>
                _knownPresent.Contains(fieldRef.FieldName) || ResolveField(fieldRef.FieldName) is { Nullable: false },
            CelBinary { Operator: CelBinaryOperator.Add } => true,
            CelConditional conditional =>
                IsNeverNullWhen(conditional.WhenTrue, conditional.Condition, conditionHolds: true)
                && IsNeverNullWhen(conditional.WhenFalse, conditional.Condition, conditionHolds: false),
            _ => false,
        };

        private bool IsNeverNullWhen(CelNode branch, CelNode condition, bool conditionHolds) =>
            IsNeverNull(branch)
            || (branch is CelFieldRef fieldRef && PresenceTested(condition, conditionHolds) == fieldRef.FieldName);

        private (CelNode, CelValueType, bool, int) CheckComparison(
            CelBinary binary, CelBinaryOperator op, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError, int position)
        {
            var profileBad = CheckConstruct(CelConstructKind.Comparison, "Comparisons are not legal in this profile.", null, position);

            if (leftError || rightError)
            {
                return (binary, CelValueType.Bool, true, position);
            }

            var error = ValidateComparisonTypes(op, leftType, rightType, position)
                ?? ValidateEnumLiteral(op, binary.Left, binary.Right, position)
                ?? ValidateSqlOperandShape(binary.Left, binary.Right, position);
            if (error is not null)
            {
                Errors.Add(error);
                return (binary, CelValueType.Bool, true, position);
            }

            return (binary, CelValueType.Bool, profileBad, position);
        }

        private CelCompilationError? ValidateComparisonTypes(CelBinaryOperator op, CelValueType left, CelValueType right, int position)
        {
            if (left == CelValueType.Json || right == CelValueType.Json)
            {
                return new CelCompilationError(
                    "Json fields cannot be compared directly.", "Compare a scalar field, or defer to a hook.", position);
            }

            if (left == CelValueType.StringList || right == CelValueType.StringList)
            {
                return new CelCompilationError(
                    "A role list cannot be compared with equality.", RoleMembershipFixSuggestion, position);
            }

            if (IsRelational(op) && (left == CelValueType.Null || right == CelValueType.Null))
            {
                return new CelCompilationError(
                    "Relational operators (<, <=, >, >=) cannot be compared against null.",
                    NullPresenceFixSuggestion,
                    position);
            }

            if (IsEqualityAgainstNullLiteral(op, left, right))
            {
                return new CelCompilationError(
                    "'==' and '!=' cannot be compared against a null literal — every comparison already treats a missing " +
                    "value as false, so this always evaluates the same way regardless of the field's actual value.",
                    NullPresenceFixSuggestion,
                    position);
            }

            if (IsRelational(op) && profile != CelProfile.Computed && (left == CelValueType.String || right == CelValueType.String))
            {
                return new CelCompilationError(
                    $"Relational operators (<, <=, >, >=) on a string are collation-dependent and are not available in the {profile} profile.",
                    "Compare with == or != instead, or move this comparison into a computed field, which only the database evaluates.",
                    position);
            }

            if (left != CelValueType.Null && right != CelValueType.Null
                && left != right && !(IsNumeric(left) && IsNumeric(right)))
            {
                return new CelCompilationError(
                    $"Cannot compare {left} to {right}.",
                    "Compare operands of the same type, or two numeric (Int/Decimal) operands.",
                    position);
            }

            return IsRelational(op) && (IsRelationRejected(left) || IsRelationRejected(right))
                ? new CelCompilationError(
                    "Relational operators (<, <=, >, >=) do not support boolean or UUID operands; use == or != instead.",
                    "Use == or != instead.",
                    position)
                : null;
        }

        /// <summary>
        /// Refuses, in a SQL-rendered profile, a comparison whose operand is itself an expression —
        /// <c>(amount &gt; 5) == true</c>, <c>has(x) == flag</c>, <c>(price + 1) &gt; 100</c>. The renderer
        /// composes a comparison over a field, a literal or a context value only, so such a tree type-checked,
        /// was saved, and then threw on every read of the entity; refusing it here is what keeps the save the
        /// point of failure (the fail-fast compile invariant).
        /// </summary>
        /// <remarks>
        /// <b>Refused rather than rendered, deliberately.</b> Rendering a predicate as an operand
        /// (<c>(&lt;pred&gt;) = TRUE</c>) needs a boolean <em>value</em> where the renderer produces a
        /// <em>predicate</em>: a dialect with no boolean type (T-SQL, which §0 principle 3 requires) cannot
        /// compare one, and it reopens the three-valued fold every comparison already collapses once. Nothing is
        /// lost in expressiveness — every refused shape has a direct spelling the fix names — and widening this
        /// later is additive, whereas an engine-divergent render would be a silent disagreement between backends.
        /// </remarks>
        private CelCompilationError? ValidateSqlOperandShape(CelNode left, CelNode right, int position)
        {
            if (!_sqlRenderedProfiles.Contains(profile) || (IsSqlOperand(left) && IsSqlOperand(right)))
            {
                return null;
            }

            var offender = IsSqlOperand(left) ? right : left;
            return new CelCompilationError(
                $"A comparison operand in the {profile} profile must be a field, a literal or a context value; "
                + $"this one is {DescribeOperand(offender)}, which cannot be rendered to SQL as an operand.",
                SqlOperandShapeFix(offender),
                position);
        }

        private static bool IsSqlOperand(CelNode node) => node is CelLiteral or CelFieldRef or CelContextRef;

        private static string DescribeOperand(CelNode node) => node switch
        {
            CelBinary { Operator: CelBinaryOperator.And or CelBinaryOperator.Or } => "a logical expression ('&&'/'||')",
            CelUnary { Operator: CelUnaryOperator.Not } => "a negation ('!')",
            CelHas => "has(...)",
            CelConditional => "a ternary",
            _ when IsArithmetic(node) => "arithmetic",
            _ => "another comparison",
        };

        private static string SqlOperandShapeFix(CelNode offender) =>
            IsArithmetic(offender) || offender is CelConditional
                ? "Compare a field or a literal directly, moving the constant to the other side — e.g. 'price > 99' rather than '(price + 1) > 100'."
                : "Use the condition itself rather than comparing it — e.g. 'total > 5' rather than '(total > 5) == true', "
                    + "'!(total > 5)' rather than '(total > 5) == false' — and combine conditions with &&, || and !.";

        private static bool IsEqualityAgainstNullLiteral(CelBinaryOperator op, CelValueType left, CelValueType right) =>
            (op is CelBinaryOperator.Equal or CelBinaryOperator.NotEqual) && (left == CelValueType.Null || right == CelValueType.Null);

        private CelCompilationError? ValidateEnumLiteral(CelBinaryOperator op, CelNode left, CelNode right, int position)
        {
            if (op is not (CelBinaryOperator.Equal or CelBinaryOperator.NotEqual))
            {
                return null;
            }

            return ValidateEnumLiteralSide(left, right, position) ?? ValidateEnumLiteralSide(right, left, position);
        }

        private CelCompilationError? ValidateEnumLiteralSide(CelNode enumSide, CelNode literalSide, int position)
        {
            var enumValues = EnumValuesOf(enumSide);
            if (enumValues is null || literalSide is not CelLiteral { Type: CelValueType.String, Value: string text })
            {
                return null;
            }

            if (enumValues.Contains(text, StringComparer.Ordinal))
            {
                return null;
            }

            return new CelCompilationError(
                $"'{text}' is not a declared value of this enum field.",
                BuildEnumSuggestion(text, enumValues),
                position);
        }

        private IReadOnlyList<string>? EnumValuesOf(CelNode node)
        {
            if (node is not CelFieldRef fieldRef)
            {
                return null;
            }

            var field = ResolveField(fieldRef.FieldName);
            return field is { Type: FieldType.Enum } ? field.EnumValues : null;
        }

        private static string BuildEnumSuggestion(string value, IReadOnlyList<string> enumValues)
        {
            var closest = NameSuggestion.Closest(value, enumValues);
            var known = string.Join(", ", enumValues.OrderBy(candidate => candidate, StringComparer.Ordinal));
            return closest is not null ? $"Did you mean '{closest}'? Declared values: {known}." : $"Declared values: {known}.";
        }

        private (CelNode, CelValueType, bool, int) CheckHas(CelHas has)
        {
            var (field, _, fieldError, position) = CheckFieldRef(has.Field);
            var profileBad = CheckConstruct(CelConstructKind.Has, "has(...) is not legal in this profile.", null, position);
            return (has with { Field = (CelFieldRef)field }, CelValueType.Bool, fieldError || profileBad, position);
        }

        private (CelNode, CelValueType, bool, int) CheckConditional(CelConditional conditional)
        {
            var (condition, conditionType, conditionError, conditionPosition) = CheckNode(conditional.Condition);
            var (whenTrue, trueType, trueError, _) =
                CheckGuardedBranch(conditional.WhenTrue, PresenceTested(conditional.Condition, conditionHolds: true));
            var (whenFalse, falseType, falseError, falsePosition) =
                CheckGuardedBranch(conditional.WhenFalse, PresenceTested(conditional.Condition, conditionHolds: false));
            var rewritten = conditional with { Condition = condition, WhenTrue = whenTrue, WhenFalse = whenFalse };

            var profileBad = CheckConstruct(
                CelConstructKind.Conditional,
                "The ternary conditional is legal only in the Computed profile.",
                "Split this into separate computed fields, or move the branching into a hook.",
                conditionPosition);
            var conditionBad = RequireBool(conditionType, conditionError, "The ternary condition", conditionPosition);
            var branchesBad = RequireMatchingBranches(trueType, falseType, trueError, falseError, falsePosition)
                || RequireValueBranches(whenTrue, whenFalse, falsePosition);

            return (rewritten, trueError ? falseType : trueType, profileBad || conditionBad || branchesBad, conditionPosition);
        }

        /// <summary>Checks one ternary branch with <paramref name="presentField"/>, when there is one, known present in it.</summary>
        private (CelNode Node, CelValueType Type, bool HasError, int Position) CheckGuardedBranch(CelNode branch, string? presentField)
        {
            if (presentField is null)
            {
                return CheckNode(branch);
            }

            var outside = _knownPresent;
            _knownPresent = outside.Add(presentField);
            try
            {
                return CheckNode(branch);
            }
            finally
            {
                _knownPresent = outside;
            }
        }

        /// <summary>
        /// The field a ternary's condition establishes as present on one of its branches: <c>has(f)</c> on the branch
        /// taken when it holds, <c>!has(f)</c> on the other one. Nothing else is read as a guard.
        /// </summary>
        private static string? PresenceTested(CelNode condition, bool conditionHolds) => (condition, conditionHolds) switch
        {
            (CelHas has, true) => has.Field.FieldName,
            (CelUnary { Operator: CelUnaryOperator.Not, Operand: CelHas has }, false) => has.Field.FieldName,
            _ => null,
        };

        private bool RequireMatchingBranches(CelValueType trueType, CelValueType falseType, bool trueError, bool falseError, int position)
        {
            if (trueError || falseError)
            {
                return true;
            }

            if (trueType == falseType)
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"The ternary's branches must have the same type; found {trueType} and {falseType}.",
                "Make both branches the same type, e.g. both numbers or both strings.",
                position));
            return true;
        }

        /// <summary>
        /// Refuses, in a SQL-rendered profile, a ternary branch that is a predicate — <c>c ? total &gt; 5 : false</c>.
        /// A branch renders as a <c>CASE</c> result, which is a value slot: a comparison, <c>has</c>, <c>!</c>
        /// or <c>&amp;&amp;</c>/<c>||</c> there has no SQL form (and none at all on an engine without a
        /// boolean type), so it is refused for the same fail-fast reason as
        /// <see cref="ValidateSqlOperandShape"/>. A boolean field, a boolean literal and a nested ternary stay
        /// legal: each renders as a value.
        /// </summary>
        private bool RequireValueBranches(CelNode whenTrue, CelNode whenFalse, int position)
        {
            var predicate = new[] { whenTrue, whenFalse }.FirstOrDefault(IsPredicateBranch);
            if (!_sqlRenderedProfiles.Contains(profile) || predicate is null)
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"A ternary branch in the {profile} profile must be a value; {DescribeOperand(predicate)} is a condition, "
                + "which cannot be rendered to SQL as a branch result.",
                "Fold the branch into the condition with && and || instead — e.g. 'is_public && total > 5' rather "
                + "than 'is_public ? total > 5 : false'.",
                position));
            return true;
        }

        private static bool IsPredicateBranch(CelNode branch) =>
            branch is CelHas or CelUnary { Operator: CelUnaryOperator.Not } || (branch is CelBinary && !IsArithmetic(branch));

        private static bool IsArithmetic(CelNode node) =>
            node is CelUnary { Operator: CelUnaryOperator.Negate } or CelBinary { Operator: CelBinaryOperator.Add or CelBinaryOperator.Subtract or CelBinaryOperator.Multiply or CelBinaryOperator.Divide };

        private (CelNode, CelValueType, bool, int) CheckChanged(CelChanged changed)
        {
            var position = FindPosition(changed.FieldName);
            var profileBad = CheckConstruct(
                CelConstructKind.Changed,
                "changed(...) is legal only in the Condition profile (a hook condition).",
                "Move this check into hooks.beforeUpdate/afterUpdate.",
                position);

            if (!ProfileReadsARow || ResolveField(changed.FieldName) is not null)
            {
                return (changed, CelValueType.Bool, profileBad, position);
            }

            Errors.Add(new CelCompilationError(
                $"'{changed.FieldName}' is not a field of entity '{entity.Name}'.",
                BuildUnknownFieldSuggestion(changed.FieldName),
                position));
            return (changed, CelValueType.Bool, true, position);
        }

        /// <summary>
        /// Checks one of the two legacy <see cref="CelProfile.Mutate"/> calls (<c>lowerAscii</c>, <c>now</c>). The profile gate
        /// runs first and unconditionally, so a call outside <see cref="CelProfile.Mutate"/> is reported for
        /// the profile it is in even when its argument is also wrong — one error per independent problem,
        /// which is this checker's whole contract.
        /// </summary>
        private (CelNode, CelValueType, bool, int) CheckLegacyCall(CelCall call)
        {
            var position = FindPosition(call.Name);
            var profileBad = CheckConstruct(
                CelConstructKind.Call,
                $"'{call.Name}(...)' is legal only in the {CelProfile.Mutate} profile (a before-hook mutate value).",
                "Move this into hooks.before*.mutate, or write the value without a function call.",
                position);

            return call switch
            {
                { Name: CelCall.LowerAscii, Arguments: [var argument] } => CheckLowerAsciiCall(call, argument, profileBad, position),
                { Name: CelCall.Now, Arguments: [] } =>
                    (call with { ResultType = CelValueType.Timestamp }, CelValueType.Timestamp, profileBad, position),
                _ => UnrecognizedNode(call),
            };
        }

        private (CelNode, CelValueType, bool, int) CheckLowerAsciiCall(
            CelCall call, CelNode argument, bool profileBad, int position)
        {
            var (checkedArgument, argumentType, argumentError, argumentPosition) = CheckNode(argument);
            var argumentBad = RequireString(
                argumentType, argumentError, $"{call.Name}(...)'s argument", argumentPosition);

            return (call with { Arguments = [checkedArgument], ResultType = CelValueType.String }, CelValueType.String, profileBad || argumentBad, position);
        }

        private (CelNode, CelValueType, bool, int) CheckCall(CelCall call) =>
            call.Name is CelCall.LowerAscii or CelCall.Now ? CheckLegacyCall(call) : CheckCatalogCall(call);

        /// <summary>
        /// Checks a call to a catalogued function: the profile gate first (as the legacy calls do), then every argument,
        /// then overload resolution. A bad argument stops the call from adding a second, cascading error.
        /// </summary>
        private (CelNode, CelValueType, bool, int) CheckCatalogCall(CelCall call)
        {
            if (!catalog.Contains(call.Name))
            {
                return UnrecognizedNode(call);
            }

            var position = FindPosition(call.Name);
            var profileBad = CheckFunctionProfile(call.Name, position);
            var arguments = call.Arguments.Select(CheckNode).ToList();
            var rewritten = call with { Arguments = [.. arguments.Select(argument => argument.Node)] };

            return arguments.Any(argument => argument.HasError)
                ? Unbound(rewritten, position)
                : Bind(rewritten, ResolveOverload(call.Name, arguments, position), profileBad, position);
        }

        /// <summary>
        /// A call that could not be bound still has the type its name promises, so the result-type check does not add
        /// a second error on top of the one already reported.
        /// </summary>
        private (CelNode, CelValueType, bool, int) Unbound(CelCall call, int position) =>
            (call, catalog.Overloads(call.Name)[0].ResultType, true, position);

        private (CelNode, CelValueType, bool, int) Bind(CelCall call, CelFunction? overload, bool profileBad, int position) =>
            overload is null
                ? Unbound(call, position)
                : (call with { ResultType = overload.ResultType, Function = overload }, overload.ResultType, profileBad, position);

        /// <summary>
        /// The two deny-by-default gates: the <see cref="CelConstructKind.FunctionCall"/> row is the ceiling, and the
        /// function's own profiles narrow it. Overloads of one name share profiles, so the first one answers.
        /// </summary>
        private bool CheckFunctionProfile(string name, int position)
        {
            var function = catalog.Overloads(name)[0];
            if (IsAllowed(profile, CelConstructKind.FunctionCall) && function.Profiles.Contains(profile))
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"'{name}(...)' is not available in the {profile} profile; it is available in "
                + $"{AvailableIn(function)}. {FunctionProfileReason()}",
                FunctionProfileFix(),
                position));
            return true;
        }

        /// <summary>
        /// The profiles a call may actually appear in: the function's own set inside the ceiling, so a refused profile
        /// is never listed. Joined as "A", "A and B" or "A, B and C"; "no profile" when none remains.
        /// </summary>
        private static string AvailableIn(CelFunction function)
        {
            var names = function.Profiles
                .Where(candidate => IsAllowed(candidate, CelConstructKind.FunctionCall))
                .Order()
                .Select(candidate => candidate.ToString())
                .ToList();

            return names switch
            {
                [] => "no profile",
                [var only] => only,
                _ => $"{string.Join(", ", names[..^1])} and {names[^1]}",
            };
        }

        private string FunctionProfileReason() => profile switch
        {
            CelProfile.Rule => "A rule becomes a SQL filter, and this function runs only in-process; "
                + "authorization is never a filter applied after the query.",
            CelProfile.Computed => "A computed field is a column the database computes, and this function runs only in-process.",
            CelProfile.Access => "An access level is a predicate over the caller alone and calls no function.",
            _ => $"This function is not enabled for the {profile} profile.",
        };

        private string FunctionProfileFix() => profile switch
        {
            CelProfile.Rule => "Store the value in a field with a before-hook mutate (hooks.beforeCreate / beforeUpdate), "
                + "then compare that field here.",
            CelProfile.Computed => "Write the value with a before-hook mutate into a regular field instead of computing it.",
            CelProfile.Access => "Test the caller instead, e.g. 'admin' in @user.roles.",
            _ => "Call it only in a profile listed above, or write the value without a function call.",
        };

        /// <summary>
        /// Arity, then an exact type match, then a match that lets an Int stand for a Decimal (deviation F7); a null
        /// literal fits only a parameter that receives null. No match is one error naming every signature.
        /// </summary>
        private CelFunction? ResolveOverload(
            string name, List<(CelNode Node, CelValueType Type, bool HasError, int Position)> arguments, int position)
        {
            var overloads = catalog.Overloads(name);
            var types = arguments.Select(argument => argument.Type).ToList();
            var chosen = overloads.FirstOrDefault(o => Accepts(o, types, widen: false))
                ?? overloads.FirstOrDefault(o => Accepts(o, types, widen: true));
            if (chosen is null)
            {
                Errors.Add(SignatureMismatch(name, overloads, types, position));
            }

            return chosen;
        }

        private static bool Accepts(CelFunction overload, List<CelValueType> types, bool widen) =>
            overload.Parameters.Count == types.Count
            && overload.Parameters.Zip(types).All(pair => Fits(pair.First, pair.Second, widen));

        private static bool Fits(CelFunctionArgument parameter, CelValueType argument, bool widen) =>
            argument == parameter.Type
            || (argument == CelValueType.Null && parameter.Nullable)
            || (widen && argument == CelValueType.Int && parameter.Type == CelValueType.Decimal);

        private static CelCompilationError SignatureMismatch(
            string name, IReadOnlyList<CelFunction> overloads, List<CelValueType> types, int position)
        {
            var signatures = string.Join(" or ", overloads.Select(overload => overload.Signature()));
            if (overloads.All(overload => overload.Parameters.Count != types.Count))
            {
                return new CelCompilationError(
                    $"'{name}' takes {Arity(overloads)}; this call passes {types.Count}.", $"Call it as {signatures}.", position);
            }

            return new CelCompilationError(
                $"'{name}(...)' accepts no ({string.Join(", ", types)}); it accepts {signatures}.",
                "Pass values of those types: string, text and enum fields are String, date and datetime fields are "
                + "Timestamp, and an Int may stand where a Decimal is expected.",
                position);
        }

        private static string Arity(IReadOnlyList<CelFunction> overloads)
        {
            var counts = overloads.Select(overload => overload.Parameters.Count).Distinct().Order().ToList();
            var noun = counts is [1] ? "argument" : "arguments";
            return $"{string.Join(" or ", counts)} {noun}";
        }

        private bool RequireBool(CelValueType type, bool childError, string subject, int position)
        {
            if (childError)
            {
                return true;
            }

            if (type == CelValueType.Bool)
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"{subject} must be boolean; found {type}.",
                "Use a comparison (field == value) or has(field) so this operand evaluates to true/false.",
                position));
            return true;
        }

        private bool RequireString(CelValueType type, bool childError, string subject, int position)
        {
            if (childError)
            {
                return true;
            }

            if (type == CelValueType.String)
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"{subject} must be a string; found {type}.",
                "Pass a string, text or enum field, or drop the fold.",
                position));
            return true;
        }

        private bool RequireNumeric(CelValueType type, bool childError, string subject, int position)
        {
            if (childError)
            {
                return true;
            }

            if (IsNumeric(type))
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"{subject} must be numeric; found {type}.",
                "Use an Integer/Decimal field or literal, or convert this value before the arithmetic.",
                position));
            return true;
        }

        private bool CheckConstruct(CelConstructKind kind, string message, string? fixSuggestion, int position)
        {
            if (IsAllowed(profile, kind))
            {
                return false;
            }

            Errors.Add(new CelCompilationError(message, fixSuggestion, position));
            return true;
        }

        private FieldSchema? ResolveField(string fieldName) =>
            entity.Fields.FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));

        private string BuildUnknownFieldSuggestion(string fieldName)
        {
            var closest = NameSuggestion.Closest(fieldName, entity.Fields.Select(field => field.Name));

            if (closest is not null)
            {
                return $"Did you mean '{closest}'?";
            }

            var known = string.Join(", ", entity.Fields.Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal));
            return $"Known fields: {known}.";
        }

        private int FindPosition(string text)
        {
            var searchFrom = _cursor;
            while (true)
            {
                var index = source.IndexOf(text, searchFrom, StringComparison.Ordinal);
                if (index < 0)
                {
                    return _cursor;
                }

                if (IsBoundaryMatch(index, text.Length))
                {
                    _cursor = index + text.Length;
                    return index;
                }

                searchFrom = index + 1;
            }
        }

        private bool IsBoundaryMatch(int index, int length)
        {
            if (index > 0 && IsIdentifierChar(source[index - 1]))
            {
                return false;
            }

            var end = index + length;
            return end >= source.Length || !IsIdentifierChar(source[end]);
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static string StatePrefix(CelRecordState state) => state switch
        {
            CelRecordState.New => "new.",
            CelRecordState.Old => "old.",
            _ => string.Empty,
        };

        private static string ContextRefText(CelContextRef contextRef) => contextRef.Value switch
        {
            CelContextValue.UserId => "@user.id",
            CelContextValue.UserRoles => "@user.roles",
            CelContextValue.TenantId => "@tenant.id",
            _ => "@" + contextRef.Value,
        };

        private static string OperatorText(CelBinaryOperator op) => op switch
        {
            CelBinaryOperator.Add => "+",
            CelBinaryOperator.Subtract => "-",
            CelBinaryOperator.Multiply => "*",
            CelBinaryOperator.Divide => "/",
            _ => op.ToString(),
        };

        private static bool IsRelational(CelBinaryOperator op) => op is
            CelBinaryOperator.Less or CelBinaryOperator.LessOrEqual or CelBinaryOperator.Greater or CelBinaryOperator.GreaterOrEqual;

        private static bool IsRelationRejected(CelValueType type) => type is CelValueType.Bool or CelValueType.Uuid;

        private static bool IsNumeric(CelValueType type) => type is CelValueType.Int or CelValueType.Decimal;

        private static bool IsKnownFieldType(FieldType type) => type is
            FieldType.String or FieldType.Text or FieldType.Integer or FieldType.Decimal or FieldType.Boolean
            or FieldType.Date or FieldType.DateTime or FieldType.Uuid or FieldType.Json or FieldType.Enum or FieldType.Ref;
    }
}
