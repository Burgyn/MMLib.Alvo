using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MMLib.Alvo.DocsGen.CSharp;

internal static class Signatures
{
    internal static string Of(MemberInfo member) => member switch
    {
        ConstructorInfo constructor => $"{Modifiers(constructor)}{TypeNames.Simple(constructor.DeclaringType!)}({Parameters(constructor)})",
        MethodInfo method => Method(method),
        PropertyInfo property => Property(property),
        FieldInfo field => Field(field),
        EventInfo @event => $"{Access(@event.DeclaringType!)}{Static(@event.AddMethod!)}event {TypeNames.Display(@event.EventHandlerType!, Context().Create(@event))} {@event.Name};",
        _ => throw new ArgumentException($"No signature for a {member.MemberType} ('{member.Name}').", nameof(member)),
    };

    private static string Method(MethodInfo method)
    {
        var returns = TypeNames.Display(method.ReturnType, Context().Create(method.ReturnParameter));
        var generic = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name))}>" : string.Empty;
        return $"{Modifiers(method)}{returns} {method.Name}{generic}({Parameters(method)})";
    }

    private static string Property(PropertyInfo property)
    {
        var accessor = property.GetMethod ?? property.SetMethod!;
        var required = property.IsDefined(typeof(RequiredMemberAttribute)) ? "required " : string.Empty;
        var type = TypeNames.Display(property.PropertyType, Context().Create(property));
        return $"{Modifiers(accessor)}{required}{type} {property.Name} {{ {Accessors(property)} }}";
    }

    private static string Accessors(PropertyInfo property)
    {
        var get = property.GetMethod is { IsPublic: true } ? "get; " : string.Empty;
        var set = property.SetMethod is { IsPublic: true } setter ? (IsInit(setter) ? "init; " : "set; ") : string.Empty;
        return (get + set).TrimEnd();
    }

    private static bool IsInit(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    private static string Field(FieldInfo field)
    {
        var type = TypeNames.Display(field.FieldType, Context().Create(field));
        var access = Access(field.DeclaringType!);
        if (field.IsLiteral)
        {
            return $"{access}const {type} {field.Name} = {Literal(field.FieldType, field.GetRawConstantValue())};";
        }

        var modifiers = (field.IsStatic ? "static " : string.Empty) + (field.IsInitOnly ? "readonly " : string.Empty);
        return $"{access}{modifiers}{type} {field.Name};";
    }

    private static string Parameters(MethodBase method)
    {
        var context = Context();
        var isExtension = method.IsDefined(typeof(ExtensionAttribute));
        return string.Join(", ", method.GetParameters().Select(parameter => Parameter(parameter, context, isExtension && parameter.Position == 0)));
    }

    private static string Parameter(ParameterInfo parameter, NullabilityInfoContext context, bool isThis)
    {
        var prefix = isThis ? "this " : ParameterModifier(parameter);
        var text = $"{prefix}{TypeNames.Display(parameter.ParameterType, context.Create(parameter))} {parameter.Name}";
        return parameter.HasDefaultValue ? $"{text} = {Literal(parameter.ParameterType, parameter.RawDefaultValue)}" : text;
    }

    private static string ParameterModifier(ParameterInfo parameter) =>
        parameter.IsDefined(typeof(ParamArrayAttribute)) ? "params "
        : !parameter.ParameterType.IsByRef ? string.Empty
        : parameter.IsOut ? "out "
        : parameter.IsIn ? "in "
        : "ref ";

    internal static string Literal(Type type, object? value)
    {
        var target = type.IsByRef ? type.GetElementType()! : type;
        return value switch
        {
            null => target.IsValueType && Nullable.GetUnderlyingType(target) is null ? "default" : "null",
            string text => "\"" + text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            bool flag => flag ? "true" : "false",
            char character => $"'{character}'",
            _ when (Nullable.GetUnderlyingType(target) ?? target).IsEnum => EnumLiteral(Nullable.GetUnderlyingType(target) ?? target, value),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
    }

    private static string EnumLiteral(Type type, object value)
    {
        var name = Enum.GetName(type, Enum.ToObject(type, value));
        var display = TypeNames.Display(type, null);
        return name is null ? $"({display}){Convert.ToString(value, CultureInfo.InvariantCulture)}" : $"{display}.{name}";
    }

    private static string Modifiers(MethodBase method) =>
        Access(method.DeclaringType!) + Static(method);

    private static string Static(MethodBase method) => method.IsStatic ? "static " : string.Empty;

    private static string Access(Type declaringType) => declaringType.IsInterface ? string.Empty : "public ";

    private static NullabilityInfoContext Context() => new();
}
