using Corvus.Json;

namespace MMLib.Alvo.Descriptor.SchemaGen;

/// <summary>
/// Placeholder struct that anchors Corvus.Json.SourceGenerator's build-time code generation for
/// <c>schema/project.schema.json</c>. At compile time, the generator reads that file (registered
/// as an <c>AdditionalFiles</c> item in the csproj) and emits a strongly-typed validator — a
/// plain compiled .NET type at runtime, no Roslyn, no <c>PreserveCompilationContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// This root struct is <see langword="internal"/>, so it (and everything nested inside it) is
/// unreachable from outside <c>MMLib.Alvo</c> — <c>Assembly.GetExportedTypes()</c>, and therefore
/// <c>PublicApiGenerator</c>, never sees it or its $defs-derived children. No Corvus-generated
/// type appears on the public API surface.
/// </para>
/// <para>
/// Deliberately NOT in a <c>*.Internal</c> namespace (unlike <see cref="Internal.DescriptorValidator"/>,
/// the only type that touches this one): Corvus nests every $defs-derived helper type as a
/// <see langword="public"/> member of this struct regardless of
/// <c>CorvusJsonSchemaDefaultAccessibility</c> (confirmed against the generated output — that
/// property only affects independently-attributed root types, not implicit $ref-derived
/// children). Those nested types are already unreachable because their container is internal;
/// placing them in a <c>*.Internal</c> namespace would additionally trip this repo's own
/// <c>Public_types_do_not_live_in_internal_namespaces</c> architecture rule, which checks
/// nested-public accessibility literally. A dedicated non-"Internal" namespace keeps that rule
/// (rightly) scoped to hand-written code.
/// </para>
/// </remarks>
[JsonSchemaTypeGenerator("../../../../schema/project.schema.json")]
internal readonly partial struct GeneratedProjectDescriptor
{
}
