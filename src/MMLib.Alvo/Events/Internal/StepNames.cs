namespace MMLib.Alvo.Events.Internal;

/// <summary>What a step's name may be: 1–200 characters, none of them a control character.</summary>
internal static class StepNames
{
    internal const int MaxLength = 200;

    internal static void Check(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > MaxLength)
        {
            throw new ArgumentException($"A step name is at most {MaxLength} characters; this one has {name.Length}.", nameof(name));
        }

        if (name.Any(char.IsControl))
        {
            throw new ArgumentException("A step name holds no control characters.", nameof(name));
        }
    }
}
