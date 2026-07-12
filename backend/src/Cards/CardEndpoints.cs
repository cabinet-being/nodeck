using System.Text.Json;
using System.Text.Json.Nodes;
using MyApp.Api.Storage;

namespace MyApp.Api.Cards;

public static class CardEndpoints
{
    public static IEndpointRouteBuilder MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cards");

        group.MapPost("", CreateCardAsync);
        group.MapGet("", ListCardsAsync);
        group.MapGet("/{id:long}", GetCardAsync);
        group.MapPut("/{id:long}", UpdateCardAsync);
        group.MapDelete("/{id:long}", DeleteCardAsync);
        group.MapGet("/{id:long}/content", GetCardContentAsync);
        group.MapGet("/{id:long}/preview", GetCardPreviewAsync);

        return app;
    }

    private static async Task<IResult> CreateCardAsync(
        HttpRequest request,
        CardRepository repository,
        PreviewService previewService)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Card creation requires multipart form data." });
        }

        var form = await request.ReadFormAsync();
        var type = form["type"].ToString().Trim();

        if (!CardTypes.IsSupportedCardType(type))
        {
            return Results.BadRequest(new { error = $"Unsupported card type '{type}'." });
        }

        var title = NormalizeOptionalString(form["title"].ToString());

        if (!TryReadOptionalJsonObject(form["properties"].ToString(), out var propertiesJson, out var propertiesError))
        {
            return Results.BadRequest(new { error = propertiesError });
        }

        if (!TryReadRelations(form["relations"].ToString(), out var relations, out var relationsError))
        {
            return Results.BadRequest(new { error = relationsError });
        }

        var metadata = new JsonObject
        {
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };

        try
        {
            if (type is "comic" or "set")
            {
                var images = ReadImageFiles(form);

                if (images.Count < 2)
                {
                    return Results.BadRequest(new { error = $"{type} cards require at least two images." });
                }

                foreach (var uploadedImage in images)
                {
                    if (!IsSupportedStaticImage(uploadedImage))
                    {
                        return Results.BadRequest(new { error = $"Unsupported image file '{uploadedImage.FileName}'." });
                    }
                }

                var createdCollection = await repository.CreateCollectionAsync(
                    new CreateCardCollectionData(type, title, propertiesJson, metadata, images, relations),
                    previewService.SaveImageAndCreatePreviewAsync,
                    previewService.DeleteCardFiles);

                return Results.Created($"/api/cards/{createdCollection.Id}", createdCollection);
            }

            var image = form.Files.GetFile("image") ?? form.Files.GetFile("file");

            if (type is "tag" or "source")
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    return Results.BadRequest(new { error = $"{type} cards require a title." });
                }

                if (form.Files.Count > 0)
                {
                    return Results.BadRequest(new { error = $"{type} cards do not support image uploads." });
                }
            }

            if (image is not null && type != "media")
            {
                return Results.BadRequest(new { error = "Image uploads are only supported for media cards." });
            }

            if (image is not null && !IsSupportedStaticImage(image))
            {
                return Results.BadRequest(new { error = $"Unsupported image file '{image.FileName}'." });
            }

            var created = await repository.CreateAsync(
                new CreateCardData(type, title, propertiesJson, metadata, relations),
                async cardId => image is null
                    ? null
                    : await previewService.SaveImageAndCreatePreviewAsync(cardId, image),
                previewService.DeleteCardFiles);

            return Results.Created($"/api/cards/{created.Id}", created);
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> ListCardsAsync(
        CardRepository repository,
        string? type,
        string? mediaType,
        string? media_type,
        string? types,
        string? search,
        string? exclude_contained_media,
        string? sort,
        string? order)
    {
        if (!TryReadTypes(type, types, out var requestedTypes, out var typeError))
        {
            return Results.BadRequest(new { error = typeError });
        }

        var cards = await repository.ListAsync(
            requestedTypes,
            mediaType ?? media_type,
            search,
            IsTruthy(exclude_contained_media),
            sort ?? "created_at",
            order ?? "desc");

        return Results.Ok(cards);
    }

    private static async Task<IResult> GetCardAsync(CardRepository repository, long id)
    {
        var card = await repository.GetByIdAsync(id);

        return card is null ? Results.NotFound(new { error = "Card not found." }) : Results.Ok(card);
    }

    private static async Task<IResult> UpdateCardAsync(
        HttpRequest request,
        CardRepository repository,
        PreviewService previewService,
        long id)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Card update requires multipart form data." });
        }

        var currentCard = await repository.GetByIdAsync(id);

        if (currentCard is null)
        {
            return Results.NotFound(new { error = "Card not found." });
        }

        var form = await request.ReadFormAsync();
        var requestedType = NormalizeOptionalString(form["type"].ToString());

        if (requestedType is not null && requestedType != currentCard.Type)
        {
            return Results.BadRequest(new { error = "Card type cannot be changed." });
        }

        var image = form.Files.GetFile("image") ?? form.Files.GetFile("file");

        if (image is not null && currentCard.Type != "media")
        {
            return Results.BadRequest(new { error = "Image uploads are only supported for media cards." });
        }

        var title = NormalizeOptionalString(form["title"].ToString());

        if (!TryReadOptionalJsonObject(form["properties"].ToString(), out var propertiesJson, out var propertiesError))
        {
            return Results.BadRequest(new { error = propertiesError });
        }

        if (!TryReadRelations(form["relations"].ToString(), out var relations, out var relationsError))
        {
            return Results.BadRequest(new { error = relationsError });
        }

        try
        {
            var assets = image is null
                ? null
                : await previewService.SaveImageAndCreatePreviewAsync(id, image);
            var updated = await repository.UpdateAsync(
                id,
                new UpdateCardData(title, propertiesJson, relations, assets));

            return updated is null ? Results.NotFound(new { error = "Card not found." }) : Results.Ok(updated);
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> DeleteCardAsync(
        CardRepository repository,
        PreviewService previewService,
        long id)
    {
        var deleted = await repository.DeleteAsync(id);

        if (!deleted)
        {
            return Results.NotFound(new { error = "Card not found." });
        }

        previewService.DeleteCardFiles(id);

        return Results.NoContent();
    }

    private static async Task<IResult> GetCardContentAsync(
        CardRepository repository,
        PreviewService previewService,
        long id)
    {
        var card = await repository.GetByIdAsync(id);
        var contentPath = card?.Metadata["content_path"]?.GetValue<string>();

        if (contentPath is null)
        {
            return Results.NotFound(new { error = "Card content not found." });
        }

        var stream = previewService.OpenRead(contentPath);

        return stream is null
            ? Results.NotFound(new { error = "Card content file not found." })
            : Results.File(stream, previewService.GetContentType(contentPath), enableRangeProcessing: true);
    }

    private static async Task<IResult> GetCardPreviewAsync(
        CardRepository repository,
        PreviewService previewService,
        long id)
    {
        var card = await repository.GetByIdAsync(id);

        if (card?.PreviewUrl is null)
        {
            return Results.NotFound(new { error = "Card preview not found." });
        }

        var previewPath = card.Metadata["preview_path"]?.GetValue<string>();

        if (previewPath is null)
        {
            return Results.NotFound(new { error = "Card preview path not found." });
        }

        var stream = previewService.OpenRead(previewPath);

        return stream is null
            ? Results.NotFound(new { error = "Card preview file not found." })
            : Results.File(stream, previewService.GetContentType(previewPath), enableRangeProcessing: true);
    }

    private static bool TryReadOptionalJsonObject(
        string? rawJson,
        out string? normalizedJson,
        out string? error)
    {
        normalizedJson = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return true;
        }

        try
        {
            var node = JsonNode.Parse(rawJson);

            if (node is not JsonObject)
            {
                error = "JSON value must be an object.";
                return false;
            }

            normalizedJson = node.ToJsonString();

            return true;
        }
        catch (JsonException)
        {
            error = "JSON value is invalid.";
            return false;
        }
    }

    private static bool TryReadRelations(
        string? rawJson,
        out IReadOnlyList<CreateRelationData> relations,
        out string? error)
    {
        relations = [];
        error = null;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return true;
        }

        try
        {
            var node = JsonNode.Parse(rawJson);

            if (node is not JsonArray array)
            {
                error = "Relations JSON must be an array.";
                return false;
            }

            var parsedRelations = new List<CreateRelationData>();

            foreach (var item in array)
            {
                if (item is not JsonObject relation)
                {
                    error = "Each relation must be an object.";
                    return false;
                }

                var toCardId = relation["toCardId"]?.GetValue<long>()
                    ?? relation["to_card_id"]?.GetValue<long>()
                    ?? 0;
                var relationType = relation["relationType"]?.GetValue<string>()
                    ?? relation["relation_type"]?.GetValue<string>()
                    ?? "";

                if (toCardId <= 0)
                {
                    error = "Relation target card ID is required.";
                    return false;
                }

                if (!CardTypes.IsSupportedRelationType(relationType))
                {
                    error = $"Unsupported relation type '{relationType}'.";
                    return false;
                }

                JsonNode? propertiesNode = relation["properties"];

                if (propertiesNode is not null && propertiesNode is not JsonObject)
                {
                    error = "Relation properties must be an object.";
                    return false;
                }

                parsedRelations.Add(new CreateRelationData(
                    toCardId,
                    relationType,
                    propertiesNode?.ToJsonString(),
                    new JsonObject
                    {
                        ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    }));
            }

            relations = parsedRelations;

            return true;
        }
        catch (JsonException)
        {
            error = "Relations JSON is invalid.";
            return false;
        }
    }

    private static string? NormalizeOptionalString(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<IFormFile> ReadImageFiles(IFormCollection form)
    {
        var images = form.Files.GetFiles("images");

        if (images.Count > 0)
        {
            return images;
        }

        return form.Files.ToList();
    }

    private static bool IsSupportedStaticImage(IFormFile image)
    {
        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var contentType = image.ContentType.ToLowerInvariant();

        return extension is ".jpg" or ".jpeg" or ".png" or ".webp"
            || contentType is "image/jpeg" or "image/png" or "image/webp";
    }

    private static bool TryReadTypes(
        string? type,
        string? types,
        out IReadOnlyList<string> requestedTypes,
        out string? error)
    {
        requestedTypes = [];
        error = null;

        var rawTypes = new List<string>();

        if (!string.IsNullOrWhiteSpace(type))
        {
            rawTypes.Add(type);
        }

        if (!string.IsNullOrWhiteSpace(types))
        {
            rawTypes.AddRange(types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var requestedType in rawTypes)
        {
            if (!CardTypes.IsSupportedCardType(requestedType))
            {
                error = $"Unsupported card type '{requestedType}'.";
                return false;
            }
        }

        requestedTypes = rawTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return true;
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null && (
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }
}
