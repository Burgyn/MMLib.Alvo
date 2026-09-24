namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One place in a descriptor that names a field or an entity.</summary>
/// <param name="Place">Where, as a dotted path an operator can find: <c>orders.indexes[0]</c>, <c>orders.rules.list</c>.</param>
/// <param name="Blocks">Whether removing what it names would leave a descriptor the apply refuses.</param>
internal sealed record DescriptorReference(string Place, bool Blocks);
