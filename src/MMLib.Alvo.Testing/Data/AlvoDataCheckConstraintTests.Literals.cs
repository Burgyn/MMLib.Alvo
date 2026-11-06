using CsCheck;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using Shouldly;
using System.Text;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// HX-11 acceptance (5) through the real apply path (Ruling 51): a validation holding a hostile text literal, planned and
/// applied by this engine's <c>ISchemaMigrator</c>, becomes a CHECK that refuses exactly that text and
/// admits a different one, and the table a payload names survives. Text the literal writer must decline is refused at the
/// apply and changes nothing.
/// </summary>
public abstract partial class AlvoDataCheckConstraintTests
{
    /// <summary>The first draw's seed, shared with the renderer-level properties; a failure prints the seed that replays it.</summary>
    private const string LiteralSeed = "0N0XIzNsQ0O2";

    /// <summary>Samples per run: every one re-plans and re-applies a CHECK, which on SQLite rebuilds the table.</summary>
    private const int LiteralSamples = 25;

    private const string Notes = "notes";

    private SchemaModel _literalSchema = NotesSchema(check: null);

    private int _held;

    private int _declined;

    /// <summary>Every hostile literal round-trips through the migrator's CHECK, and nothing else executes.</summary>
    [Fact]
    public async Task A_text_literal_applied_through_the_migrator_never_escapes_its_check()
    {
        var data = await CreateAsync(_literalSchema, NotesDescriptor);
        (await ExecuteOutsideThePortAsync("CREATE TABLE \"sentinel\" (\"id\" INTEGER)")).ShouldBeNull();

        await LiteralText.Hostile().SampleAsync(text => RoundTripsAsync(data, text), iter: LiteralSamples, threads: 1, seed: LiteralSeed);

        _held.ShouldBeGreaterThanOrEqualTo(8, "the property must reach text a literal carries");
        _declined.ShouldBeGreaterThanOrEqualTo(1, "and text it declines");
    }

    private async Task<bool> RoundTripsAsync(IAlvoData data, string text)
    {
        var desired = NotesSchema($"title != {CelString(text)}");
        if (LiteralText.MustBeDeclined(text))
        {
            _declined++;
            return await Record.ExceptionAsync(() => ApplyAsync(_literalSchema, desired)) is DescriptorValidationException
                && await SentinelSurvivesAsync();
        }

        _held++;
        await ApplyAsync(_literalSchema, desired);
        _literalSchema = desired;

        return await RefusedAsync(data, text) && await AdmittedAndRemovedAsync(data, text + "x") && await SentinelSurvivesAsync();
    }

    private static async Task<bool> RefusedAsync(IAlvoData data, string title) =>
        await Record.ExceptionAsync(() => data.CreateAsync(Notes, Title(title), Caller, cancellationToken: Ct))
            is AlvoConstraintViolationException { Kind: AlvoConstraintKind.Check };

    /// <summary>Admits <paramref name="title"/>, then deletes the row so no later sample's CHECK meets it at its apply.</summary>
    private static async Task<bool> AdmittedAndRemovedAsync(IAlvoData data, string title)
    {
        var row = await data.CreateAsync(Notes, Title(title), Caller, cancellationToken: Ct);
        await data.DeleteAsync(Notes, (Guid)row[AlvoManagedColumns.Id]!, Caller, cancellationToken: Ct);
        return Equals(row["title"], title);
    }

    private async Task<bool> SentinelSurvivesAsync() =>
        await ExecuteOutsideThePortAsync("SELECT COUNT(*) FROM \"sentinel\"") is null;

    private static Dictionary<string, object?> Title(string title) => new(StringComparer.Ordinal) { ["title"] = title };

    /// <summary>A CEL string literal: the escapes the CEL lexer reads back, everything else verbatim.</summary>
    private static string CelString(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('\'');
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '\\' => @"\\",
                '\'' => @"\'",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => character.ToString(),
            });
        }

        return builder.Append('\'').ToString();
    }

    private static SchemaModel NotesSchema(string? check) => new([
        new EntitySchema
        {
            Name = Notes,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true, MaxLength = 2000, CheckExpression = check },
            ],
        },
    ]);

    private static AlvoDescriptor NotesDescriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "check-literals",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Notes] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String, MaxLength = 2000 },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };
}
