using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Parses one leaf of a filter — <c>&lt;field&gt;</c> plus <c>&lt;operator&gt;.&lt;value&gt;</c> — into an
/// <see cref="AlvoComparison"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The field is resolved first, before the operator and before the value.</b> That ordering is a
/// confidentiality property, not a style choice: a caller probing <c>salary=nosuchop.1</c> must not learn
/// from an "unknown operator" refusal that <c>salary</c> exists, so the name is settled — and refused
/// indistinguishably — before anything else about the term is examined.
/// </para>
/// <para>
/// The <see cref="AlvoComparison.Field"/> written is the <em>schema's</em> string, never the caller's. Both
/// compare equal ordinally, but a field name is the one caller-supplied value that reaches SQL as an
/// identifier, and handing on the declared instance is what makes "no caller bytes become an identifier"
/// true by construction rather than by argument.
/// </para>
/// <para>
/// Three operator/type rules are enforced here because the port's two implementations would otherwise
/// <em>disagree</em> about them, which §0 principle 3 forbids on a channel a caller controls per request: a
/// pattern match is a string operation by definition, <c>is true</c>/<c>is false</c> is a boolean identity
/// test, and an ordering comparison needs a type this port defines an order over.
/// </para>
/// </remarks>
internal static class FilterTermParser
{
    private const string NullOperand = "null";

    private const string TrueOperand = "true";

    private const string FalseOperand = "false";

    /// <summary>Parses one filter term.</summary>
    /// <param name="field">The caller-supplied field name.</param>
    /// <param name="operatorAndValue">The caller-supplied <c>&lt;operator&gt;.&lt;value&gt;</c> text.</param>
    /// <param name="scope">The request's resolvable fields and node budget.</param>
    /// <param name="filter">The parsed comparison.</param>
    /// <param name="violation">Why the term was refused.</param>
    internal static bool TryParse(
        string field,
        string operatorAndValue,
        FilterParseScope scope,
        out AlvoFilter? filter,
        out AlvoViolation? violation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(operatorAndValue);

        filter = null;
        if (!scope.TryChargeNode())
        {
            violation = QueryViolations.FilterTooWide();
            return false;
        }

        if (scope.Fields.Resolve(field) is not { } declared)
        {
            violation = QueryViolations.UnavailableField(QueryViolations.FilterPointer);
            return false;
        }

        return TryCompare(declared, operatorAndValue, scope, out filter, out violation);
    }

    private static bool TryCompare(
        FieldSchema declared,
        string operatorAndValue,
        FilterParseScope scope,
        out AlvoFilter? filter,
        out AlvoViolation? violation)
    {
        filter = null;
        var separator = operatorAndValue.IndexOf('.');
        if (separator < 0)
        {
            violation = QueryViolations.MalformedTerm();
            return false;
        }

        if (!FilterOperators.TryResolve(operatorAndValue[..separator], out var @operator))
        {
            violation = QueryViolations.UnknownOperator();
            return false;
        }

        if (!TryReadOperand(
                declared, @operator, operatorAndValue[(separator + 1)..], scope, out var value, out violation))
        {
            return false;
        }

        filter = new AlvoComparison(declared.Name, @operator, value);
        return true;
    }

    private static bool TryReadOperand(
        FieldSchema declared,
        AlvoFilterOperator @operator,
        string operand,
        FilterParseScope scope,
        out object? value,
        out AlvoViolation? violation)
    {
        value = null;
        if (@operator == AlvoFilterOperator.In)
        {
            return TryReadCandidates(declared, operand, scope, out value, out violation);
        }

        if (@operator == AlvoFilterOperator.Is)
        {
            return TryReadIdentity(declared, operand, out value, out violation);
        }

        if (!IsApplicable(@operator, declared))
        {
            violation = QueryViolations.UnsupportedOperatorForField(@operator, declared);
            return false;
        }

        return FilterOperators.IsPatternMatch(@operator)
            ? TryReadPattern(operand, out value, out violation)
            : FilterValueReader.TryRead(declared, operand, out value, out violation);
    }

    /// <summary>
    /// Whether <paramref name="operator"/>'s own meaning admits the type <paramref name="declared"/> is
    /// compared at. The type comes from <see cref="CelFieldType"/> — the same table the CEL type checker and
    /// every driver's value repair read, so this rule cannot drift from the comparison it is describing.
    /// </summary>
    private static bool IsApplicable(AlvoFilterOperator @operator, FieldSchema declared)
    {
        var type = CelFieldType.Of(declared);
        return FilterOperators.IsPatternMatch(@operator)
            ? type == CelValueType.String
            : !FilterOperators.IsOrdering(@operator) || FilterOperators.IsOrderable(type);
    }

    private static bool TryReadPattern(string operand, out object? value, out AlvoViolation? violation)
    {
        var read = FilterValueReader.TryReadPattern(operand, out var pattern, out violation);
        value = pattern;
        return read;
    }

    /// <summary>
    /// The three operands SQL's own <c>IS</c> accepts, and no coercion. <c>is.null</c> applies to any field;
    /// <c>is.true</c>/<c>is.false</c> only to a boolean one, because the port renders them as an identity test
    /// against a boolean literal and the reference evaluator answers a definite <see langword="false"/> for a
    /// non-boolean field — one input, two different answers.
    /// </summary>
    private static bool TryReadIdentity(
        FieldSchema declared, string operand, out object? value, out AlvoViolation? violation)
    {
        value = null;
        violation = null;
        if (operand == NullOperand)
        {
            return true;
        }

        if (CelFieldType.Of(declared) != CelValueType.Bool || operand is not (TrueOperand or FalseOperand))
        {
            violation = QueryViolations.MalformedIsOperand();
            return false;
        }

        value = operand == TrueOperand;
        return true;
    }

    /// <summary>
    /// An <c>in</c> list, capped before it is read rather than after: each candidate becomes its own bind
    /// parameter, so an unbounded list is a statement the engine may refuse outright.
    /// </summary>
    private static bool TryReadCandidates(
        FieldSchema declared,
        string operand,
        FilterParseScope scope,
        out object? value,
        out AlvoViolation? violation)
    {
        value = null;
        if (!ParenthesisedList.TrySplit(operand, out var candidates))
        {
            violation = QueryViolations.MalformedInList();
            return false;
        }

        if (candidates.Count > AlvoFilter.MaxInCandidates || !scope.TryChargeCandidates(candidates.Count))
        {
            violation = QueryViolations.TooManyInCandidates();
            return false;
        }

        return TryReadEach(declared, candidates, out value, out violation);
    }

    private static bool TryReadEach(
        FieldSchema declared, IReadOnlyList<string> candidates, out object? value, out AlvoViolation? violation)
    {
        value = null;
        var read = new List<object?>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!FilterValueReader.TryRead(declared, candidate, out var single, out violation))
            {
                return false;
            }

            read.Add(single);
        }

        violation = null;
        value = read;
        return true;
    }
}
