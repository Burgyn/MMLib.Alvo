using MMLib.Alvo.Schema;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <see cref="SchemaModel"/>, used by
/// <see cref="AppliedSchemaStore"/> to (de)serialize the <c>schema_json</c> column without
/// reflection-based System.Text.Json (avoids trim/AOT warnings under TreatWarningsAsErrors).
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(SchemaModel))]
internal sealed partial class AppliedSchemaJsonContext : JsonSerializerContext;
