namespace MMLib.Alvo.Expressions.Internal;

/// <summary>One parameter of a catalogued function overload: what the type checker matches and the marshaller converts to.</summary>
/// <param name="Name">The parameter's name, as discovery shows it.</param>
/// <param name="Type">The CEL type an argument must have; an Int argument also binds a Decimal parameter.</param>
/// <param name="Nullable">
/// Whether the parameter receives a null argument. When it does not, a null argument makes the whole call null and the
/// body is never invoked — CEL's strictness, SQL's <c>NULL</c> in, <c>NULL</c> out.
/// </param>
/// <param name="ClrType">The CLR type the body receives, never a <see cref="Nullable{T}"/>; nullability is <paramref name="Nullable"/>.</param>
internal sealed record CelFunctionArgument(string Name, CelValueType Type, bool Nullable, System.Type ClrType);

/// <summary>One overload of a function the CEL compiler knows — a built-in or a host registration.</summary>
/// <remarks>
/// Overloads of one name share <see cref="Profiles"/> and provenance (<see cref="CelFunctionCatalog"/> enforces it); a
/// host function has exactly one overload. The <see cref="Body"/> receives arguments already converted to each
/// parameter's <see cref="CelFunctionArgument.ClrType"/>.
/// </remarks>
/// <param name="Name">The function's CEL spelling.</param>
/// <param name="Parameters">The typed parameters, in call order.</param>
/// <param name="ResultType">The CEL type of the value the call yields.</param>
/// <param name="ResultNullable">Whether the call may yield null even when every argument is present.</param>
/// <param name="Summary">One sentence for discovery; empty when the host gave none.</param>
/// <param name="IsHost">Whether the embedding host registered it (as opposed to a built-in).</param>
/// <param name="Profiles">The profiles the function may appear in, inside the type checker's ceiling.</param>
/// <param name="Body">
/// The implementation, or <see langword="null"/> for <c>now</c>, which keeps its own grammar and is evaluated by name.
/// </param>
internal sealed record CelFunction(
    string Name,
    IReadOnlyList<CelFunctionArgument> Parameters,
    CelValueType ResultType,
    bool ResultNullable,
    string Summary,
    bool IsHost,
    IReadOnlySet<CelProfile> Profiles,
    Func<object?[], object?>? Body)
{
    /// <summary>Gets a value indicating whether this is <c>now</c>, the one call with its own grammar, evaluated by name.</summary>
    public bool IsLegacy => Body is null;

    /// <summary>
    /// Gets a check of the literal arguments alone, run at apply (spec §6.4): given each argument's literal value — or
    /// <see langword="null"/> where the argument is not a literal — it answers why the call would always fail, or
    /// <see langword="null"/>. Built-ins only; a host function never declares one, because host code never runs at apply.
    /// </summary>
    public Func<IReadOnlyList<object?>, string?>? ConstantCheck { get; init; }

    /// <summary>
    /// Calls the body with <paramref name="arguments"/> converted to each parameter's CLR type. A null argument for a
    /// parameter that takes none makes the call null without invoking the body; a nullable parameter receives it
    /// (spec R3). A present argument that does not convert fails the call — never null, which a condition would read as
    /// <c>false</c> and a before-hook <c>reject</c> would let through.
    /// </summary>
    /// <param name="arguments">The evaluated arguments, one per parameter.</param>
    /// <returns>The normalised result, or <see langword="null"/>.</returns>
    /// <exception cref="CelFunctionException">
    /// An argument does not fit its parameter (the reason names the parameter and its type, never the value), or the
    /// body failed (whatever it threw is the inner exception).
    /// </exception>
    public object? Invoke(IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new object?[Parameters.Count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = Bind(arguments[index], Parameters[index]);
            if (values[index] is null && !Parameters[index].Nullable)
            {
                return null;
            }
        }

        return Run(values);
    }

    /// <summary>This overload as discovery shows it.</summary>
    /// <returns>The public description.</returns>
    public CelFunctionInfo Describe() => new()
    {
        Name = Name,
        Parameters = [.. Parameters.Select(p => new CelFunctionParameter { Name = p.Name, Type = p.Type, AcceptsNull = p.Nullable })],
        Result = ResultType,
        ResultMayBeNull = ResultNullable,
        Summary = Summary,
        Provenance = IsHost ? CelFunctionProvenance.Host : CelFunctionProvenance.BuiltIn,
        Profiles = [.. Profiles.Order()],
    };

    /// <summary>The overload as one line, e.g. <c>replace(text: String, search: String, replacement: String) -> String</c>.</summary>
    /// <returns>The signature text refusals and fixes quote.</returns>
    public string Signature() =>
        $"{Name}({string.Join(", ", Parameters.Select(DescribeParameter))}) -> {ResultType}{(ResultNullable ? "?" : string.Empty)}";

    /// <summary>One argument as its parameter's CLR type: null stays null, anything else converts or fails the call.</summary>
    /// <param name="argument">The evaluated argument.</param>
    /// <param name="parameter">The parameter it binds.</param>
    /// <returns>The converted value, or <see langword="null"/> for a null argument.</returns>
    /// <exception cref="CelFunctionException">The argument is present but does not convert.</exception>
    private object? Bind(object? argument, CelFunctionArgument parameter)
    {
        if (argument is null)
        {
            return null;
        }

        return CelArgumentMarshaller.TryConvert(argument, parameter.ClrType, out var converted)
            ? converted
            : throw new CelFunctionException(Name, IsHost, $"an argument does not fit parameter '{parameter.Name}' ({parameter.ClrType.Name})");
    }

    private object? Run(object?[] values)
    {
        var body = Body ?? throw new InvalidOperationException($"'{Name}' has its own grammar and is evaluated by name, never invoked.");
        try
        {
            return CelArgumentMarshaller.Normalize(body(values));
        }
        catch (CelFunctionException)
        {
            throw;
        }
#pragma warning disable CA1031 // A body may throw anything; every failure becomes the one fail-closed type.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            throw new CelFunctionException(Name, IsHost, failure);
        }
    }

    private static string DescribeParameter(CelFunctionArgument parameter) =>
        $"{parameter.Name}: {parameter.Type}{(parameter.Nullable ? "?" : string.Empty)}";
}
