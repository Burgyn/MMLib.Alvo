using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using NSubstitute;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>A primed catalog of two entities and the registrations the binder facts compile against it.</summary>
internal static class HandlerCatalog
{
    internal static SchemaModel Schema { get; } = new([
        new EntitySchema
        {
            Name = "tickets",
            Tenancy = TenancyMode.Global,
            EventImageExclusions = ["secret"],
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid },
                new FieldSchema { Name = "title", Type = FieldType.String },
                new FieldSchema { Name = "code", Type = FieldType.String, Nullable = true },
                new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "resolved"] },
                new FieldSchema { Name = "secret", Type = FieldType.String, Nullable = true },
            ],
        },
        new EntitySchema
        {
            Name = "notes",
            Tenancy = TenancyMode.Global,
            Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }, new FieldSchema { Name = "text", Type = FieldType.String }],
        },
    ]);

    internal static PolicyCatalog Catalog() =>
        PolicyCatalog.Build(
            new AlvoDescriptor
            {
                ApiVersion = "alvo.dev/v1",
                Name = "handlers",
                Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
                {
                    ["tickets"] = new() { Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) },
                    ["notes"] = new() { Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) },
                },
            },
            Schema,
            CelFixtures.Compiler);

    internal static ExtensionRegistry Registry(Action<IAlvoBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure);
        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<ExtensionRegistry>();
    }

    internal static ExtensionBindingResult Compile(Action<IAlvoBuilder> configure, IServiceProviderIsService? services = null) =>
        ExtensionBindingCompiler.Compile(Registry(configure), Catalog(), CelFixtures.Compiler, services ?? EveryService());

    internal static IServiceProviderIsService EveryService()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(true);

        return services;
    }

    internal static Func<AlvoCreated<AlvoRecord>, Task> OnCreated { get; } = _ => Task.CompletedTask;

    internal static Func<AlvoUpdated<AlvoRecord>, Task> OnUpdated { get; } = _ => Task.CompletedTask;

    internal static Func<AlvoDeleted<AlvoRecord>, Task> OnDeleted { get; } = _ => Task.CompletedTask;

    internal static Func<AlvoSaved<AlvoRecord>, Task> OnSaved { get; } = _ => Task.CompletedTask;

    internal static Func<AlvoRecord, Task> OnRecord { get; } = _ => Task.CompletedTask;
}
