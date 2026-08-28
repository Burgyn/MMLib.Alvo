using MMLib.Alvo.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Walks a parsed CEL tree once, resolving every <see cref="CelFieldRef"/> against an entity's
/// schema, enforcing the type rules for every node family, and rejecting any construct the active
/// <see cref="CelProfile"/> does not allow — all in one pass, collecting every independent error
/// instead of stopping at the first.
/// </summary>
internal static class CelTypeChecker
{
    /// <summary>Checks a parsed tree against an entity's schema and a profile.</summary>
    /// <param name="root">The parsed, untyped tree.</param>
    /// <param name="source">
    /// The original CEL source. Used only to locate an offending identifier's position for an
    /// error — the tree itself carries no position information.
    /// </param>
    /// <param name="entity">The entity to resolve row fields against.</param>
    /// <param name="profile">Which constructs are legal.</param>
    /// <returns>
    /// The rewritten tree (every <see cref="CelFieldRef"/> carries its resolved type), the whole
    /// expression's result type, the position its result-type check should be anchored to, and
    /// every error found.
    /// </returns>
    public static (CelNode Root, CelValueType ResultType, int Position, IReadOnlyList<CelCompilationError> Errors) Check(
        CelNode root, string source, EntitySchema entity, CelProfile profile)
    {
        var visitor = new Visitor(source, entity, profile);
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
        ContextRef,
        Logical,
        Comparison,
        In,
        Has,
        Arithmetic,
        Conditional,
        Changed,
        Call,
    }

    /// <summary>
    /// Rule, Computed and Condition — the three profiles that predate <see cref="CelProfile.Mutate"/>.
    /// Deliberately <b>not</b> "every profile": <see cref="CelProfile.Mutate"/> joins a row of
    /// <see cref="_allowedProfiles"/> one at a time, with the fact that needs it, so the table never grants
    /// a construct to a profile no test has exercised there.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _ruleComputedCondition =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Computed, CelProfile.Condition };

    private static readonly IReadOnlySet<CelProfile> _everyProfile =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Computed, CelProfile.Condition, CelProfile.Mutate };

    private static readonly IReadOnlySet<CelProfile> _computedOnly = new HashSet<CelProfile> { CelProfile.Computed };

    private static readonly IReadOnlySet<CelProfile> _conditionOnly = new HashSet<CelProfile> { CelProfile.Condition };

    private static readonly IReadOnlySet<CelProfile> _mutateOnly = new HashSet<CelProfile> { CelProfile.Mutate };

    private static readonly IReadOnlySet<CelProfile> _ruleAndCondition =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Condition };

    /// <summary>
    /// A hook <c>condition</c> and a before-hook <c>mutate</c> are the two slots evaluated against a
    /// candidate row, so they are the two that may name the row's before/after images.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _conditionAndMutate =
        new HashSet<CelProfile> { CelProfile.Condition, CelProfile.Mutate };

    /// <summary>
    /// The one positive table that decides where each construct is legal. <see cref="CelProfile.Mutate"/>
    /// holds four rows today — literals, current-row and <c>old.</c>/<c>new.</c> field references, and the
    /// allow-listed function call — which is exactly what its two functions and their arguments need. The
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
            [CelConstructKind.FieldRefCurrent] = _everyProfile,
            [CelConstructKind.FieldRefPastFuture] = _conditionAndMutate,
            [CelConstructKind.ContextRef] = _ruleAndCondition,
            [CelConstructKind.Logical] = _ruleComputedCondition,
            [CelConstructKind.Comparison] = _ruleComputedCondition,
            [CelConstructKind.In] = _ruleAndCondition,
            [CelConstructKind.Has] = _ruleComputedCondition,
            [CelConstructKind.Arithmetic] = _computedOnly,
            [CelConstructKind.Conditional] = _computedOnly,
            [CelConstructKind.Changed] = _conditionOnly,
            [CelConstructKind.Call] = _mutateOnly,
        };

    private static bool IsAllowed(CelProfile profile, CelConstructKind kind) =>
        _allowedProfiles.TryGetValue(kind, out var profiles) && profiles.Contains(profile);

    private sealed class Visitor(string source, EntitySchema entity, CelProfile profile)
    {
        private const string RoleMembershipFixSuggestion =
            "A caller holds a set of roles; test membership instead, e.g. 'editor' in @user.roles.";

        private const string ComputedNoContextMessage =
            "A computed column is evaluated by the database with no caller context.";

        private const string NullPresenceFixSuggestion =
            "Use has(field) to test presence, or !has(field) to test absence.";

        private int _cursor;

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
            return (literal, literal.Type, profileBad, _cursor);
        }

        private (CelNode, CelValueType, bool, int) CheckFieldRef(CelFieldRef fieldRef)
        {
            var position = FindPosition(fieldRef.FieldName);
            var kind = fieldRef.State == CelRecordState.Current
                ? CelConstructKind.FieldRefCurrent
                : CelConstructKind.FieldRefPastFuture;
            var stateBad = CheckConstruct(
                kind,
                $"'{StatePrefix(fieldRef.State)}{fieldRef.FieldName}' is legal only in the {CelProfile.Condition} and "
                + $"{CelProfile.Mutate} profiles (a hook condition and a before-hook mutate value) — the two slots "
                + "evaluated against a candidate row.",
                "Reference the current row instead, or move this into a hook condition or a before-hook mutate.",
                position);

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

        private (CelNode, CelValueType, bool, int) CheckContextRef(CelContextRef contextRef)
        {
            var position = FindPosition(ContextRefText(contextRef));
            var profileBad = CheckConstruct(
                CelConstructKind.ContextRef,
                ComputedNoContextMessage,
                "Move the caller-dependent check into a rule or a hook condition.",
                position);

            return (contextRef, contextRef.Type, profileBad, position);
        }

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

        private (CelNode, CelValueType, bool, int) CheckComparison(
            CelBinary binary, CelBinaryOperator op, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError, int position)
        {
            var profileBad = CheckConstruct(CelConstructKind.Comparison, "Comparisons are not legal in this profile.", null, position);

            if (leftError || rightError)
            {
                return (binary, CelValueType.Bool, true, position);
            }

            var error = ValidateComparisonTypes(op, leftType, rightType, position)
                ?? ValidateEnumLiteral(op, binary.Left, binary.Right, position);
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
            var (whenTrue, trueType, trueError, _) = CheckNode(conditional.WhenTrue);
            var (whenFalse, falseType, falseError, falsePosition) = CheckNode(conditional.WhenFalse);
            var rewritten = conditional with { Condition = condition, WhenTrue = whenTrue, WhenFalse = whenFalse };

            var profileBad = CheckConstruct(
                CelConstructKind.Conditional,
                "The ternary conditional is legal only in the Computed profile.",
                "Split this into separate computed fields, or move the branching into a hook.",
                conditionPosition);
            var conditionBad = RequireBool(conditionType, conditionError, "The ternary condition", conditionPosition);
            var branchesBad = RequireMatchingBranches(trueType, falseType, trueError, falseError, falsePosition);

            return (rewritten, trueError ? falseType : trueType, profileBad || conditionBad || branchesBad, conditionPosition);
        }

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

        private (CelNode, CelValueType, bool, int) CheckChanged(CelChanged changed)
        {
            var position = FindPosition(changed.FieldName);
            var profileBad = CheckConstruct(
                CelConstructKind.Changed,
                "changed(...) is legal only in the Condition profile (a hook condition).",
                "Move this check into hooks.beforeUpdate/afterUpdate.",
                position);

            if (ResolveField(changed.FieldName) is not null)
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
        /// Checks one of the two allow-listed <see cref="CelProfile.Mutate"/> functions. The profile gate
        /// runs first and unconditionally, so a call outside <see cref="CelProfile.Mutate"/> is reported for
        /// the profile it is in even when its argument is also wrong — one error per independent problem,
        /// which is this checker's whole contract.
        /// </summary>
        private (CelNode, CelValueType, bool, int) CheckCall(CelCall call)
        {
            var position = FindPosition(call.Name);
            var profileBad = CheckConstruct(
                CelConstructKind.Call,
                $"'{call.Name}(...)' is legal only in the {CelProfile.Mutate} profile (a before-hook mutate value).",
                "Move this into hooks.before*.mutate, or write the value without a function call.",
                position);

            return call switch
            {
                { Name: CelCall.LowerAscii, Argument: { } argument } => CheckLowerAsciiCall(call, argument, profileBad, position),
                { Name: CelCall.Now, Argument: null } => (call, CelValueType.Timestamp, profileBad, position),
                _ => UnrecognizedNode(call),
            };
        }

        private (CelNode, CelValueType, bool, int) CheckLowerAsciiCall(
            CelCall call, CelNode argument, bool profileBad, int position)
        {
            var (checkedArgument, argumentType, argumentError, argumentPosition) = CheckNode(argument);
            var argumentBad = RequireString(
                argumentType, argumentError, $"{call.Name}(...)'s argument", argumentPosition);

            return (call with { Argument = checkedArgument }, CelValueType.String, profileBad || argumentBad, position);
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
