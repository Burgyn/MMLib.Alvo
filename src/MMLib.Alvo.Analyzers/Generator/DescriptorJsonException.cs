namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>A document <see cref="DescriptorJson"/> cannot read, with the 1-based line and column where reading stopped.</summary>
internal sealed class DescriptorJsonException(string message, int line, int column) : Exception(message)
{
    internal int Line { get; } = line;

    internal int Column { get; } = column;
}
