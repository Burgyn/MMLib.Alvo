using Microsoft.Extensions.Logging;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The function action: the host functions AddFunction registered, picked by name, and a form for the input (HX-15). */
public partial class HooksTab
{
    private const string HostFunctionSelectId = "hook-function";

    /// <summary>The functions the host registered, from the extension catalog; empty until it is read, or when it could not be.</summary>
    private IReadOnlyList<ManagementExtension> _hostFunctions = [];

    /// <summary>Reads the host functions when a sheet opens, fire-and-forget as <see cref="LoadFunctions"/>.</summary>
    private void LoadHostFunctions() => _ = LoadHostFunctionsAsync();

    private async Task LoadHostFunctionsAsync()
    {
        try
        {
            var extensions = await Gateway.ExtensionsAsync(CancellationToken.None);
            if (_lifetime.Ended)
            {
                return;
            }

            _hostFunctions = [.. extensions.Where(extension => extension.Kind == AlvoExtensionKind.Function).OrderBy(f => f.Id, StringComparer.Ordinal)];
            DescribeOpenedFunction();
            Redraw();
        }
        catch (Exception ex)
        {
            HostFunctionsFailed(Logger, ex);
        }
    }

    /// <summary>Fixed text and the exception only, as <see cref="FunctionsFailed"/>.</summary>
    [LoggerMessage(EventId = 24, Level = LogLevel.Warning, Message = "Reading the host functions for the hook editor failed")]
    private static partial void HostFunctionsFailed(ILogger logger, Exception exception);

    /// <summary>An opened function hook learns its rows' types and the inputs it did not name, once the catalog is read.</summary>
    private void DescribeOpenedFunction()
    {
        if (Current is { Kind: HookBuilder.Function } builder && HostFunction(builder.FunctionName)?.InputSchema is { } schema)
        {
            builder.Describe(schema);
        }
    }

    private ManagementExtension? HostFunction(string name) =>
        _hostFunctions.FirstOrDefault(function => string.Equals(function.Id, name, StringComparison.Ordinal));

    private Task ChooseHostFunctionAsync(string? name)
    {
        var chosen = name ?? string.Empty;
        Current.ChooseFunction(chosen, HostFunction(chosen)?.InputSchema);
        return RefocusSelectAsync(HostFunctionSelectId);
    }

    private void ChooseInputMode(int index, string word)
    {
        if (index < Current.InputRows.Count)
        {
            Current.InputRows[index].Mode = word == ExpressionWord ? MutateMode.Expression : MutateMode.Literal;
        }
    }

    private void TypeInput(int index, string? text)
    {
        if (index < Current.InputRows.Count)
        {
            Current.InputRows[index].Text = text ?? string.Empty;
        }
    }

    /// <summary>The functions the picker offers: the registered ones, and the hook's own name first when none is registered by it.</summary>
    private IReadOnlyList<string> HostFunctionNames
    {
        get
        {
            var names = _hostFunctions.Select(function => function.Id).ToList();
            if (Current.FunctionName.Length > 0 && !names.Contains(Current.FunctionName, StringComparer.Ordinal))
            {
                names.Insert(0, Current.FunctionName);
            }

            return names;
        }
    }

    private string HostFunctionLabel(string name) => HostFunction(name) is null ? $"{name} (not registered by this host)" : name;

    private string HostFunctionHint => _hostFunctions.Count == 0
        ? "This host registers no function. A host registers one in C# with AddFunction<T>(name); it then appears here, with a form for its input."
        : "A function the host registered with AddFunction. It runs after the write commits, as its own delivery, retried on failure.";

    private static string InputId(int index) => $"hook-input-{index}";

    /// <summary>Why a literal row cannot be written, from the moment it is typed.</summary>
    private static string? InputFit(FunctionInputRow row) =>
        row.Mode == MutateMode.Literal && row.Text.Trim().Length > 0 && !FunctionInputRows.TryLiteral(row, out _, out var refusal)
            ? refusal
            : null;
}
