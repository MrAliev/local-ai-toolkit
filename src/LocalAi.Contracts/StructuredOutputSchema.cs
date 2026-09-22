using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<StructuredFieldType>))]
public enum StructuredFieldType { String, Integer, Number, Boolean }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StructuredOutputField(
    [property: JsonRequired, JsonPropertyName("name")] string Name,
    [property: JsonRequired, JsonPropertyName("type")] StructuredFieldType Type,
    [property: JsonPropertyName("required")] bool Required = true,
    [property: JsonPropertyName("allowedValues")] IReadOnlyList<string>? AllowedValues = null);

/// <summary>Version-one flat-object contract, not a general JSON Schema implementation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StructuredOutputSchema(
    [property: JsonRequired, JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonRequired, JsonPropertyName("fields")] IReadOnlyList<StructuredOutputField> Fields)
{
    public const int MaximumSchemaCharacters = 32_768;

    public static StructuredOutputSchema Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumSchemaCharacters) throw new ArgumentException("structured_schema_too_large", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        CheckObject(document.RootElement);
        if (document.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
            foreach (var field in fields.EnumerateArray())
            {
                CheckObject(field);
                if (field.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String ||
                    !Enum.GetNames<StructuredFieldType>().Contains(type.GetString(), StringComparer.OrdinalIgnoreCase)))
                    throw new ArgumentException("structured_schema_type_must_be_named", nameof(json));
            }
        var schema = JsonSerializer.Deserialize<StructuredOutputSchema>(json)
            ?? throw new ArgumentException("structured_schema_invalid", nameof(json));
        schema.EnsureValid();
        return schema;
    }

    public void EnsureValid()
    {
        if (SchemaVersion != 1 || Fields is null || Fields.Count is < 1 or > 64)
            throw new ArgumentException("structured_schema_invalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (field is null || string.IsNullOrEmpty(field.Name) || field.Name.Length > 128 ||
                field.Name.Any(char.IsControl) || !names.Add(field.Name) || !Enum.IsDefined(field.Type))
                throw new ArgumentException("structured_schema_field_invalid");
            if (field.AllowedValues is not { } values) continue;
            if (field.Type != StructuredFieldType.String || values.Count is < 1 or > 128 ||
                values.Any(value => value is null || value.Length > 256) ||
                values.Distinct(StringComparer.Ordinal).Count() != values.Count)
                throw new ArgumentException("structured_schema_enum_invalid");
        }
        if (JsonSerializer.Serialize(this).Length > MaximumSchemaCharacters)
            throw new ArgumentException("structured_schema_too_large");
    }

    private static void CheckObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("structured_schema_object_required");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new ArgumentException("structured_schema_duplicate_property");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<StructuredValidationStatus>))]
public enum StructuredValidationStatus { NotRequested, Valid, Empty, InvalidJson, SchemaMismatch }

public sealed record StructuredValidationError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("path")] string Path);

public sealed record StructuredValidationResult(
    [property: JsonPropertyName("status")] StructuredValidationStatus Status,
    [property: JsonPropertyName("errors")] IReadOnlyList<StructuredValidationError> Errors,
    [property: JsonPropertyName("value")] JsonElement? Value = null)
{
    [JsonPropertyName("isValid")]
    public bool IsValid => Status == StructuredValidationStatus.Valid;
}

public static class StructuredOutputValidator
{
    public const int MaximumResponseCharacters = 1_048_576;

    public static StructuredValidationResult Validate(string? answer, StructuredOutputSchema? schema,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (schema is null) return new(StructuredValidationStatus.NotRequested, []);
        schema.EnsureValid();
        if (string.IsNullOrWhiteSpace(answer)) return new(StructuredValidationStatus.Empty, [new("empty_response", "")]);
        if (answer.Length > MaximumResponseCharacters)
            return new(StructuredValidationStatus.SchemaMismatch, [new("response_too_large", "")]);
        JsonDocument document;
        try { document = JsonDocument.Parse(answer, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { return new(StructuredValidationStatus.InvalidJson, [new("invalid_json", "")]); }
        using (document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(StructuredValidationStatus.SchemaMismatch, [new("object_required", "")]);
            var fields = schema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var errors = new List<StructuredValidationError>();
            void Error(string code, string name)
            {
                if (errors.Count < 64) errors.Add(new(code, "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
            }
            foreach (var property in root.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(property.Name)) Error("duplicate_field", property.Name);
                if (seen.Count > 256) { Error("too_many_fields", property.Name); break; }
                if (!fields.TryGetValue(property.Name, out var field)) { Error("unexpected_field", property.Name); continue; }
                var value = property.Value;
                var typeMatches = field.Type switch
                {
                    StructuredFieldType.String => value.ValueKind == JsonValueKind.String,
                    StructuredFieldType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                    StructuredFieldType.Number => value.ValueKind == JsonValueKind.Number,
                    StructuredFieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => false,
                };
                if (!typeMatches) Error("wrong_type", property.Name);
                else if (field.AllowedValues is { } values && !values.Contains(value.GetString(), StringComparer.Ordinal))
                    Error("value_not_allowed", property.Name);
            }
            foreach (var field in schema.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (field.Required && !seen.Contains(field.Name)) Error("required_field_missing", field.Name);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return errors.Count == 0
                ? new(StructuredValidationStatus.Valid, [], root.Clone())
                : new(StructuredValidationStatus.SchemaMismatch, errors.AsReadOnly());
        }
    }
}
