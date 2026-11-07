namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>
/// One descriptor as the generator emits from it: strings, flags and <see cref="EquatableArray{T}"/> only, so an edit that
/// leaves it equal reuses every cached step after the read.
/// </summary>
/// <param name="File">The descriptor's file name, for diagnostics.</param>
/// <param name="Project">The project's <c>name</c>.</param>
/// <param name="Namespace">The namespace the generated types go in.</param>
/// <param name="Roles">The built-in roles, then the declared ones, each once.</param>
/// <param name="Entities">The entities, in descriptor order.</param>
internal sealed record DescriptorModel(
    string File, string Project, string Namespace, EquatableArray<string> Roles, EquatableArray<EntityModel> Entities);

/// <summary>One entity: its typed fields, managed columns first, and the fields reachable only untyped.</summary>
/// <param name="Name">The entity's name in the descriptor.</param>
/// <param name="ClassName">The generated class's name.</param>
/// <param name="Description">The entity's <c>description</c>.</param>
/// <param name="Fields">The fields that get a property, managed columns first, then descriptor order.</param>
/// <param name="Untyped">The hidden, <c>events.exclude</c>d and <c>json</c> fields, in descriptor order.</param>
internal sealed record EntityModel(string Name, string ClassName, string? Description, EquatableArray<FieldModel> Fields, EquatableArray<string> Untyped);

/// <summary>One typed field.</summary>
/// <param name="Name">The field's name in the descriptor.</param>
/// <param name="PropertyName">The generated property's name.</param>
/// <param name="ParameterName">The generated parameter's name, escaped when it is a keyword.</param>
/// <param name="Kind">What the field holds.</param>
/// <param name="Nullable">Whether a stored record may hold null in it.</param>
/// <param name="Required">Whether the descriptor marks it <c>required</c>.</param>
/// <param name="HasDefault">Whether the descriptor declares a <c>default</c>.</param>
/// <param name="CallerWritable">Whether a caller's create or update may send it.</param>
/// <param name="Description">The field's <c>description</c>.</param>
/// <param name="EnumValues">An enum field's values, in order.</param>
/// <param name="EnumTypeName">An enum field's generated type name.</param>
/// <param name="Origin">Who fills the field: a caller, Alvo, the store or the host's enricher.</param>
internal sealed record FieldModel(
    string Name,
    string PropertyName,
    string ParameterName,
    FieldKind Kind,
    bool Nullable,
    bool Required,
    bool HasDefault,
    bool CallerWritable,
    string? Description,
    EquatableArray<EnumValueModel> EnumValues,
    string? EnumTypeName,
    FieldOrigin Origin);

/// <summary>One value of an enum field and the generated member that stands for it.</summary>
/// <param name="Value">The declared value.</param>
/// <param name="MemberName">The generated member's name.</param>
internal sealed record EnumValueModel(string Value, string MemberName);

/// <summary>What a typed field holds: <c>text</c> reads as <see cref="String"/>, <c>ref</c> as <see cref="Uuid"/>.</summary>
internal enum FieldKind
{
    String,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Uuid,
    Enum,
}

/// <summary>Who fills a field's value.</summary>
internal enum FieldOrigin
{
    Declared,
    Managed,
    Computed,
    Rollup,
    Sequence,
    Virtual,
}
