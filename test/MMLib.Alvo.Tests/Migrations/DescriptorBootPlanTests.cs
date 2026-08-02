using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
using NSubstitute;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// Stage 0 of the boot: everything the descriptor decides on its own, before any database is consulted.
/// </summary>
/// <remarks>
/// Every fact here is written with <b>no database registered at all</b> — no
/// <c>ISchemaMigrator</c>, no <c>IAppliedSchemaStore</c>, no <c>ISchemaIntrospector</c>, not even a
/// connection string. That is the deliverable, not a convenience of the fixture: a stage that
/// compiles the descriptor must be runnable on a host whose database is unreachable, and the only way
/// to prove it needs nothing is to give it nothing.
/// </remarks>
public sealed class DescriptorBootPlanTests
{
    private const string FleetDescriptorJson = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "fleet",
          "entities": {
            "vehicles": {
              "fields": {
                "vin": { "type": "string", "required": true, "maxLength": 17 },
                "make": { "type": "string" }
              },
              "rules": {
                "list": "'authenticated' in @user.roles"
              }
            }
          }
        }
        """;

    [Fact]
    public async Task A_boot_plan_is_produced_with_no_database_registered()
    {
        var plan = await Subject(FleetDescriptorJson).LoadAsync(TestContext.Current.CancellationToken);

        plan.Descriptor.Name.ShouldBe("fleet");
        plan.Desired.Entities.ShouldNotBeEmpty();
        plan.DescriptorJson.ShouldBe(FleetDescriptorJson);
        plan.Catalog.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_invalid_descriptor_is_refused_before_anything_else_happens()
        => await Should.ThrowAsync<DescriptorValidationException>(
            () => Subject("""{ "not": "a descriptor" }""").LoadAsync(TestContext.Current.CancellationToken));

    /// <summary>
    /// The refusal <c>MapAlvoDataApi</c> raises at map time, raised at stage 0 instead — so it stays a
    /// failure to <em>start</em> once route mapping becomes lazy, rather than a 500 on the first request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The validator is substituted, and that substitution <b>is</b> the scenario rather than a shortcut.
    /// <c>DescriptorValidator</c> refuses a reserved field name itself, with a
    /// <see cref="DescriptorValidationException"/>, so no descriptor driven through the real validator can
    /// reach this guard — which is exactly why the guard was pinned by nothing at stage 0. It exists for the
    /// descriptors that skip that validation: <see cref="IDescriptorValidator"/> is a replaceable port, and a
    /// host that registered its own gets stage 0's belt instead of a route it cannot filter by.
    /// </para>
    /// <para>
    /// It asserts on the wording, because the wording is the deliverable: a refusal that does not name the
    /// field and the fix leaves an author reading a stack trace about a query string they never wrote.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_field_named_after_a_reserved_query_key_is_refused_at_stage_zero()
    {
        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => WithoutValidation(DescriptorDeclaringAFieldNamed(ReservedQueryKeys.Limit))
                .LoadAsync(TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain(ReservedQueryKeys.Limit);
        refusal.Message.ShouldContain("rename the field", Shouldly.Case.Insensitive);
    }

    /// <summary>
    /// A rule that does not compile rejects the boot at stage 0, before the store is read and before any
    /// DDL could run.
    /// </summary>
    /// <remarks>
    /// The validator is substituted for the same reason as above, and here it is what makes the fact
    /// discriminating: <c>DescriptorValidator</c> already compiles every rule in its own pass, so with the
    /// real validator this would pass whether or not stage 0 built a catalog at all. Bypassing it leaves
    /// <c>PolicyCatalog.Build</c> as the only thing that can refuse this descriptor.
    /// </remarks>
    [Fact]
    public async Task An_uncompilable_rule_is_refused_at_stage_zero()
        => await Should.ThrowAsync<DescriptorValidationException>(
            () => WithoutValidation(DescriptorWithRule("no_such_column == 1"))
                .LoadAsync(TestContext.Current.CancellationToken));

    /// <summary>
    /// A descriptor declaring a block this build honours nowhere warns on <b>every</b> boot, including the
    /// restart that changes nothing — the reason the warning sits at stage 0 rather than on an apply.
    /// </summary>
    /// <remarks>
    /// A warning emitted only by a genuine apply tells an author about their unhonoured webhooks exactly
    /// once, on the deploy where they are least surprised by them, and never again. Stage 0 runs on every
    /// boot, so this is the moment that property is decided; the fact is here as well as on the runner
    /// because the runner is no longer the only caller.
    /// </remarks>
    [Fact]
    public async Task A_declared_but_unhonoured_block_warns_on_every_boot_naming_it()
    {
        using var capturing = new CapturingLogger();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capturing));

        await Subject(FleetWithUnhonouredBlockJson, loggers.CreateLogger<DescriptorBootPlan>())
            .LoadAsync(TestContext.Current.CancellationToken);

        capturing.Warnings.ShouldHaveSingleItem("one line for the whole set")
            .ShouldContain(
                "webhooks",
                Shouldly.Case.Sensitive,
                "a warning that does not name the block leaves the author debugging the endpoint they think "
                + "is down");
    }

    private const string FleetWithUnhonouredBlockJson = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "fleet",
          "entities": {
            "vehicles": {
              "fields": {
                "vin": { "type": "string", "required": true, "maxLength": 17 }
              }
            }
          },
          "webhooks": {
            "endpoints": {
              "vehicle-changed": {
                "url": "https://example.test/hooks/vehicle-changed",
                "secretRef": "vehicle-changed-secret"
              }
            }
          }
        }
        """;

    private static string DescriptorDeclaringAFieldNamed(string field) => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "fleet",
          "entities": {
            "vehicles": {
              "fields": {
                "{{field}}": { "type": "integer" }
              }
            }
          }
        }
        """;

    private static string DescriptorWithRule(string rule) => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "fleet",
          "entities": {
            "vehicles": {
              "fields": {
                "vin": { "type": "string" }
              },
              "rules": {
                "list": "{{rule}}"
              }
            }
          }
        }
        """;

    private static DescriptorBootPlan Subject(string descriptorJson, ILogger<DescriptorBootPlan>? logger = null)
        => new(
            SourceOf(descriptorJson),
            new DescriptorValidator(),
            new CelCompiler(),
            logger ?? NullLogger<DescriptorBootPlan>.Instance);

    /// <summary>
    /// The same stage 0 over a validator that approves everything — the shape a host replacing
    /// <see cref="IDescriptorValidator"/> produces, and the only path to stage 0's own guards.
    /// </summary>
    private static DescriptorBootPlan WithoutValidation(string descriptorJson)
    {
        var validator = Substitute.For<IDescriptorValidator>();
        validator.Validate(Arg.Any<string>()).Returns(DescriptorValidationResult.Valid);

        return new DescriptorBootPlan(
            SourceOf(descriptorJson), validator, new CelCompiler(), NullLogger<DescriptorBootPlan>.Instance);
    }

    private static IDescriptorSource SourceOf(string descriptorJson)
    {
        var source = Substitute.For<IDescriptorSource>();
        source.LoadAsync(Arg.Any<CancellationToken>()).Returns(descriptorJson);
        return source;
    }
}
