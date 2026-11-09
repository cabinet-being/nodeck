using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyApp.Api.Cards;

public sealed record CardCreationRequest(string Scope, string Key, byte[] Hash);

public enum CardCreationStatus { Created, Replayed, Conflict, Gone, Unknown }

public sealed record CardCreationResult(CardCreationStatus Status, CardDetails? Card);

public static class CardCreationHash
{
    public static async Task<byte[]> MediaAsync(
        string type, string? title, string? propertiesJson,
        IReadOnlyList<CreateRelationData> relations, IFormFile? file,
        CancellationToken cancellationToken)
    {
        string? fileHash = null;
        if (file is not null)
        {
            await using var stream = file.OpenReadStream();
            fileHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        return Hash(new
        {
            type,
            title,
            properties = CanonicalJson(propertiesJson),
            relations = relations.Select(relation => new
            {
                relation.ToCardId,
                relation.RelationType,
                properties = CanonicalJson(relation.PropertiesJson),
            }).ToArray(),
            file = file is null ? null : new { file.FileName, file.Length, sha256 = fileHash },
        });
    }

    public static byte[] Collection(CreateExistingCollectionRequest request) => Hash(new
    {
        request.Type,
        title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim(),
        request.ChildCardIds,
    });

    private static byte[] Hash<T>(T value) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value));

    private static string? CanonicalJson(string? json)
    {
        if (json is null) return null;
        return CanonicalNode(JsonNode.Parse(json)).ToJsonString();
    }

    private static JsonNode CanonicalNode(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => KeyValuePair.Create(item.Key, item.Value is null ? null : CanonicalNode(item.Value)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : CanonicalNode(item)).ToArray()),
        null => JsonValue.Create((string?)null)!,
        _ => node.DeepClone(),
    };
}
