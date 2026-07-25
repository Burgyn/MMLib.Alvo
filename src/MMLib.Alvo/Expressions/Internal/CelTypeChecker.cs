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
    /// expression's result type, and every error found.
    /// </returns>
    public static (CelNode Root, CelValueType ResultType, IReadOnlyList<CelCompilationError> Errors) Check(
        CelNode root, string source, EntitySchema entity, CelProfile profile)
    {
        var visitor = new Visitor(source, entity, profile);
        var (node, type, _) = visitor.CheckNode(root);
        return (node, type, visitor.Errors);
    }

    private sealed class Visitor(string source, EntitySchema entity, CelProfile profile)
    {
        private const string RoleMembershipFixSuggestion =
            "A caller holds a set of roles; test membership instead, e.g. 'editor' in @user.roles.";

        private const string ComputedNoContextMessage =
            "A computed column is evaluated by the database with no caller context.";

        private int _cursor;

        public List<CelCompilationError> Errors { get; } = [];

        public (CelNode Node, CelValueType Type, bool HasError) CheckNode(CelNode node) => node switch
        {
            CelLiteral literal => (literal, literal.Type, false),
            CelFieldRef fieldRef => CheckFieldRef(fieldRef),
            CelContextRef contextRef => CheckContextRef(contextRef),
            CelUnary unary => CheckUnary(unary),
            CelBinary binary => CheckBinary(binary),
            CelHas has => CheckHas(has),
            CelConditional conditional => CheckConditional(conditional),
            CelChanged changed => CheckChanged(changed),
            _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown CEL node kind."),
        };

        private (CelNode, CelValueType, bool) CheckFieldRef(CelFieldRef fieldRef)
        {
            var position = FindPosition(fieldRef.FieldName);
            var stateBad = CheckProfileConstraint(
                fieldRef.State == CelRecordState.Current || profile == CelProfile.Condition,
                $"'{StatePrefix(fieldRef.State)}{fieldRef.FieldName}' is legal only in the Condition profile (a hook condition).",
                "Reference the current row instead, or move this check into a hook condition.",
                position);

            var field = ResolveField(fieldRef.FieldName);
            if (field is null)
            {
                Errors.Add(new CelCompilationError(
                    $"'{fieldRef.FieldName}' is not a field of entity '{entity.Name}'.",
                    BuildUnknownFieldSuggestion(fieldRef.FieldName),
                    position));
                return (fieldRef, CelValueType.Null, true);
            }

            var type = MapFieldType(field.Type);
            return (fieldRef with { Type = type }, type, stateBad);
        }

        private (CelNode, CelValueType, bool) CheckContextRef(CelContextRef contextRef)
        {
            var position = FindPosition(ContextRefText(contextRef));
            var profileBad = CheckProfileConstraint(
                profile != CelProfile.Computed,
                ComputedNoContextMessage,
                "Move the caller-dependent check into a rule or a hook condition.",
                position);

            return (contextRef, contextRef.Type, profileBad);
        }

        private (CelNode, CelValueType, bool) CheckUnary(CelUnary unary)
        {
            var (operand, operandType, operandError) = CheckNode(unary.Operand);
            var rewritten = unary with { Operand = operand };

            return unary.Operator switch
            {
                CelUnaryOperator.Not => CheckLogicalNot(rewritten, operandType, operandError),
                CelUnaryOperator.Negate => CheckNegate(rewritten, operandType, operandError),
                _ => throw new ArgumentOutOfRangeException(nameof(unary), unary.Operator, "Unknown CEL unary operator."),
            };
        }

        private (CelNode, CelValueType, bool) CheckLogicalNot(CelUnary unary, CelValueType operandType, bool operandError)
        {
            var hasError = RequireBool(operandType, operandError, "'!' operand");
            return (unary, CelValueType.Bool, hasError);
        }

        private (CelNode, CelValueType, bool) CheckNegate(CelUnary unary, CelValueType operandType, bool operandError)
        {
            var profileBad = CheckProfileConstraint(
                profile == CelProfile.Computed,
                "Arithmetic negation ('-') is legal only in the Computed profile.",
                "Move this calculation into a computed field.",
                0);
            var operandBad = RequireNumeric(operandType, operandError, "Unary '-' operand");
            var resultType = operandError ? CelValueType.Decimal : operandType;
            return (unary, resultType, profileBad || operandBad);
        }

        private (CelNode, CelValueType, bool) CheckBinary(CelBinary binary)
        {
            var (left, leftType, leftError) = CheckNode(binary.Left);
            var (right, rightType, rightError) = CheckNode(binary.Right);
            var rewritten = binary with { Left = left, Right = right };

            return binary.Operator switch
            {
                CelBinaryOperator.And or CelBinaryOperator.Or =>
                    CheckLogical(rewritten, leftType, rightType, leftError, rightError),
                CelBinaryOperator.In =>
                    CheckIn(rewritten, leftType, rightType, leftError, rightError),
                CelBinaryOperator.Add or CelBinaryOperator.Subtract or CelBinaryOperator.Multiply or CelBinaryOperator.Divide =>
                    CheckArithmetic(rewritten, leftType, rightType, leftError, rightError),
                CelBinaryOperator.Equal or CelBinaryOperator.NotEqual or CelBinaryOperator.Less
                    or CelBinaryOperator.LessOrEqual or CelBinaryOperator.Greater or CelBinaryOperator.GreaterOrEqual =>
                    CheckComparison(rewritten, binary.Operator, leftType, rightType, leftError, rightError),
                _ => throw new ArgumentOutOfRangeException(nameof(binary), binary.Operator, "Unknown CEL binary operator."),
            };
        }

        private (CelNode, CelValueType, bool) CheckLogical(
            CelBinary binary, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError)
        {
            var leftBad = RequireBool(leftType, leftError, "'&&'/'||' left operand");
            var rightBad = RequireBool(rightType, rightError, "'&&'/'||' right operand");
            return (binary, CelValueType.Bool, leftBad || rightBad);
        }

        private (CelNode, CelValueType, bool) CheckIn(
            CelBinary binary, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError)
        {
            var profileBad = CheckProfileConstraint(
                profile != CelProfile.Computed,
                $"'in' (role membership) is not available in the Computed profile: {ComputedNoContextMessage}",
                "Move this check into a rule or a hook condition.",
                0);

            if (leftError || rightError)
            {
                return (binary, CelValueType.Bool, true);
            }

            if (leftType == CelValueType.String && rightType == CelValueType.StringList)
            {
                return (binary, CelValueType.Bool, profileBad);
            }

            Errors.Add(new CelCompilationError(
                $"'in' requires a string on the left and a role list (@user.roles) on the right; found {leftType} and {rightType}.",
                null,
                0));
            return (binary, CelValueType.Bool, true);
        }

        private (CelNode, CelValueType, bool) CheckArithmetic(
            CelBinary binary, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError)
        {
            var profileBad = CheckProfileConstraint(
                profile == CelProfile.Computed,
                $"Arithmetic is legal only in the Computed profile; '{OperatorText(binary.Operator)}' is not allowed here.",
                "Move this calculation into a computed field.",
                0);

            var leftBad = RequireNumeric(leftType, leftError, "Arithmetic left operand");
            var rightBad = RequireNumeric(rightType, rightError, "Arithmetic right operand");
            var resultType = leftType == CelValueType.Decimal || rightType == CelValueType.Decimal
                ? CelValueType.Decimal
                : CelValueType.Int;

            return (binary, resultType, profileBad || leftBad || rightBad);
        }

        private (CelNode, CelValueType, bool) CheckComparison(
            CelBinary binary, CelBinaryOperator op, CelValueType leftType, CelValueType rightType, bool leftError, bool rightError)
        {
            if (leftError || rightError)
            {
                return (binary, CelValueType.Bool, true);
            }

            var error = ValidateComparisonTypes(op, leftType, rightType);
            if (error is not null)
            {
                Errors.Add(error);
                return (binary, CelValueType.Bool, true);
            }

            return (binary, CelValueType.Bool, false);
        }

        private static CelCompilationError? ValidateComparisonTypes(CelBinaryOperator op, CelValueType left, CelValueType right)
        {
            if (left == CelValueType.Json || right == CelValueType.Json)
            {
                return new CelCompilationError(
                    "Json fields cannot be compared directly.", "Compare a scalar field, or defer to a hook.", 0);
            }

            if (left == CelValueType.StringList || right == CelValueType.StringList)
            {
                return new CelCompilationError(
                    "A role list cannot be compared with equality.", RoleMembershipFixSuggestion, 0);
            }

            if (left != CelValueType.Null && right != CelValueType.Null
                && left != right && !(IsNumeric(left) && IsNumeric(right)))
            {
                return new CelCompilationError($"Cannot compare {left} to {right}.", null, 0);
            }

            return IsRelational(op) && (IsRelationRejected(left) || IsRelationRejected(right))
                ? new CelCompilationError(
                    "Relational operators (<, <=, >, >=) do not support boolean or UUID operands; use == or != instead.",
                    null,
                    0)
                : null;
        }

        private (CelNode, CelValueType, bool) CheckHas(CelHas has)
        {
            var (field, _, fieldError) = CheckFieldRef(has.Field);
            return (has with { Field = (CelFieldRef)field }, CelValueType.Bool, fieldError);
        }

        private (CelNode, CelValueType, bool) CheckConditional(CelConditional conditional)
        {
            var profileBad = CheckProfileConstraint(
                profile == CelProfile.Computed,
                "The ternary conditional is legal only in the Computed profile.",
                "Split this into separate computed fields, or move the branching into a hook.",
                0);

            var (condition, conditionType, conditionError) = CheckNode(conditional.Condition);
            var (whenTrue, trueType, trueError) = CheckNode(conditional.WhenTrue);
            var (whenFalse, falseType, falseError) = CheckNode(conditional.WhenFalse);
            var rewritten = conditional with { Condition = condition, WhenTrue = whenTrue, WhenFalse = whenFalse };

            var conditionBad = RequireBool(conditionType, conditionError, "The ternary condition");
            var branchesBad = RequireMatchingBranches(trueType, falseType, trueError, falseError);

            return (rewritten, trueError ? falseType : trueType, profileBad || conditionBad || branchesBad);
        }

        private bool RequireMatchingBranches(CelValueType trueType, CelValueType falseType, bool trueError, bool falseError)
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
                $"The ternary's branches must have the same type; found {trueType} and {falseType}.", null, 0));
            return true;
        }

        private (CelNode, CelValueType, bool) CheckChanged(CelChanged changed)
        {
            var position = FindPosition(changed.FieldName);
            var profileBad = CheckProfileConstraint(
                profile == CelProfile.Condition,
                "changed(...) is legal only in the Condition profile (a hook condition).",
                "Move this check into hooks.beforeUpdate/afterUpdate.",
                position);

            if (ResolveField(changed.FieldName) is not null)
            {
                return (changed, CelValueType.Bool, profileBad);
            }

            Errors.Add(new CelCompilationError(
                $"'{changed.FieldName}' is not a field of entity '{entity.Name}'.",
                BuildUnknownFieldSuggestion(changed.FieldName),
                position));
            return (changed, CelValueType.Bool, true);
        }

        private bool RequireBool(CelValueType type, bool childError, string subject)
        {
            if (childError)
            {
                return true;
            }

            if (type == CelValueType.Bool)
            {
                return false;
            }

            Errors.Add(new CelCompilationError($"{subject} must be boolean; found {type}.", null, 0));
            return true;
        }

        private bool RequireNumeric(CelValueType type, bool childError, string subject)
        {
            if (childError)
            {
                return true;
            }

            if (IsNumeric(type))
            {
                return false;
            }

            Errors.Add(new CelCompilationError($"{subject} must be numeric; found {type}.", null, 0));
            return true;
        }

        private bool CheckProfileConstraint(bool allowedInProfile, string message, string? fixSuggestion, int position)
        {
            if (allowedInProfile)
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
            var closest = entity.Fields
                .Select(field => (field.Name, Distance: LevenshteinDistance(fieldName, field.Name)))
                .Where(candidate => candidate.Distance <= 2)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .Select(candidate => candidate.Name)
                .FirstOrDefault();

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
            _ => throw new ArgumentOutOfRangeException(nameof(contextRef), contextRef.Value, "Unknown CEL context value."),
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

        private static CelValueType MapFieldType(FieldType type) => type switch
        {
            FieldType.String or FieldType.Text or FieldType.Enum => CelValueType.String,
            FieldType.Integer => CelValueType.Int,
            FieldType.Decimal => CelValueType.Decimal,
            FieldType.Boolean => CelValueType.Bool,
            FieldType.Date or FieldType.DateTime => CelValueType.Timestamp,
            FieldType.Uuid or FieldType.Ref => CelValueType.Uuid,
            FieldType.Json => CelValueType.Json,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown field type."),
        };

        private static int LevenshteinDistance(string a, string b)
        {
            var distances = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++)
            {
                distances[i, 0] = i;
            }

            for (var j = 0; j <= b.Length; j++)
            {
                distances[0, j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    distances[i, j] = Math.Min(Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1), distances[i - 1, j - 1] + cost);
                }
            }

            return distances[a.Length, b.Length];
        }
    }
}
