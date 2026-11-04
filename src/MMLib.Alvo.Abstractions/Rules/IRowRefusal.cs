using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>A refusal a batch reports as one row's, rather than failing the whole call (Ruling 4).</summary>
/// <remarks>Internal, so a driver can recognise the core's rejection without naming a core type.</remarks>
internal interface IRowRefusal
{
    /// <summary>This refusal as the row at <paramref name="index"/>'s.</summary>
    /// <param name="index">The row's place in the request.</param>
    AlvoRowRefusal At(int index);
}
