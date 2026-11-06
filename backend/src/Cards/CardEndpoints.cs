using System.Text.Json;
using System.Text.Json.Nodes;
using MyApp.Api.Analysis;
using MyApp.Api.Storage;

namespace MyApp.Api.Cards;

public static class CardEndpoints
{
    public static IEndpointRouteBuilder MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cards");

        group.MapPost("", CreateCardAsync);
        group.MapPost("/collections", CreateExistingCollectionAsync);
        group.MapGet("", ListCardsAsync);
        group.MapGet("/{id:long}", GetCardAsync);
        group.MapPut("/{id:long}", UpdateCardAsync);
        group.MapPut("/{id:long}/preview", UpdatePreviewStartAsync);
        group.MapPut("/{id:long}/children", UpdateCollectionChildrenAsync);
        group.MapGet("/{id:long}/deletion-impact", GetDeletionImpactAsync);
        group.MapDelete("/{id:long}", DeleteCardAsync);
        group.MapGet("/{id:long}/content", GetCardContentAsync);
        group.MapGet("/{id:long}/playback", GetCardPlaybackAsync);
        group.MapGet("/{id:long}/preview", GetCardPreviewAsync);
        group.MapGet("/{id:long}/poster", GetCardPosterAsync);
        group.MapGet("/{id:long}/analysis", GetAnalysisStatusAsync);
        group.MapGet("/{id:long}/speech-analysis", GetSpeechAnalysisStatusAsync);

        return app;
    }

    private static async Task<IResult> GetAnalysisStatusAsync(
        long id, AnalysisQueueRepository queue, CancellationToken cancellationToken)
    {
        var status = await queue.GetStatusAsync(id, cancellationToken);
        return status is null ? Results.NotFound(new { error = "Image analysis job not found." }) :
            Results.Ok(status);
    }

    private static async Task<IResult> GetSpeechAnalysisStatusAsync(
        long id, SpeechAnalysisQueueRepository queue, CancellationToken cancellationToken)
    {
        var status = await queue.GetStatusAsync(id, cancellationToken);
        return status is null ? Results.NotFound(new { error = "Speech analysis job not found." }) :
            Results.Ok(status);
    }

    private static async Task<IResult> CreateExistingCollectionAsync(
        CreateExistingCollectionRequest request, CardRepository repository)
    {
        try
        {
            var card = await repository.CreateCollectionFromExistingAsync(request);
            return Results.Created($"/api/cards/{card.Id}", card);
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> UpdateCollectionChildrenAsync(
        long id, UpdateCollectionChildrenRequest request, CardRepository repository)
    {
        try
        {
            var card = await repository.UpdateCollectionChildrenAsync(id, request.ChildCardIds);
            return card is null ? Results.NotFound(new { error = "Card not found." }) : Results.Ok(card);
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> GetDeletionImpactAsync(long id, CardRepository repository)
    {
        var impact = await repository.GetDeletionImpactAsync(id);
        return impact is null ? Results.NotFound(new { error = "Card not found." }) : Results.Ok(impact);
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

        if (type is "tag" or "source")
        {
            return Results.BadRequest(new { error = "Create tags and sources through their catalog endpoints." });
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

                var createdCollection = await repository.CreateCollectionAsync(
                    new CreateCardCollectionData(type, title, propertiesJson, metadata, images, relations),
                    previewService.SaveImageAndCreatePreviewAsync,
                    previewService.DeleteCardFiles);

                return Results.Created($"/api/cards/{createdCollection.Id}", createdCollection);
            }

            var image = form.Files.GetFile("image") ?? form.Files.GetFile("file");

            if (image is not null && type != "media")
            {
                return Results.BadRequest(new { error = "Media uploads are only supported for media cards." });
            }

            var created = await repository.CreateAsync(
                new CreateCardData(type, title, propertiesJson, metadata, relations),
                async cardId => image is null
                    ? null
                    : await previewService.SaveMediaAndCreatePreviewAsync(cardId, image),
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
        string? tags,
        string? source,
        string? catalog_tags,
        string? catalog_source,
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
            ParseLongList(tags),
            ParseLongOrNull(source),
            ParseLongList(catalog_tags),
            ParseLongOrNull(catalog_source),
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
            return Results.BadRequest(new { error = "Media uploads are only supported for media cards." });
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
            PendingCardFileReplacement? replacement = null;

            if (image is not null)
            {
                replacement = await previewService.PrepareMediaReplacementAsync(id, image);
            }

            Func<Task>? promoteAssets = replacement is null ? null : replacement.PromoteAsync;
            Action? rollbackAssets = replacement is null ? null : replacement.Rollback;
            Action? cleanupPromotedAssets = replacement is null ? null : replacement.CleanupAfterCommit;

            var updated = await repository.UpdateAsync(
                id,
                new UpdateCardData(title, propertiesJson, relations, replacement?.Assets),
                promoteAssets,
                rollbackAssets,
                cleanupPromotedAssets);

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

    private static async Task<IResult> UpdatePreviewStartAsync(
        long id,
        JsonElement request,
        CardRepository repository,
        PreviewService previewService)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("startSeconds", out var startElement) ||
            startElement.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null) ||
            (startElement.ValueKind == JsonValueKind.Number && !startElement.TryGetDouble(out _)))
        {
            return Results.BadRequest(new { error = "startSeconds must be a number or null." });
        }

        var startSeconds = startElement.ValueKind == JsonValueKind.Null ? (double?)null : startElement.GetDouble();
        try
        {
            var card = await repository.UpdatePreviewStartAsync(id, startSeconds, previewService.PreparePreviewOverrideAsync);
            return card is null ? Results.NotFound(new { error = "Card not found." }) : Results.Ok(card);
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> DeleteCardAsync(
        CardRepository repository,
        PreviewService previewService,
        long id,
        bool delete_contents = false)
    {
        var deletedCardIds = await repository.DeleteAsync(id, delete_contents);

        if (deletedCardIds.Count == 0)
        {
            return Results.NotFound(new { error = "Card not found." });
        }

        foreach (var deletedCardId in deletedCardIds)
        {
            previewService.DeleteCardFiles(deletedCardId);
        }

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

    private static async Task<IResult> GetCardPlaybackAsync(
        CardRepository repository,
        PreviewService previewService,
        long id)
    {
        var card = await repository.GetByIdAsync(id);
        var playbackPath = card?.Metadata["playback_path"]?.GetValue<string>();
        if (playbackPath is null)
            return Results.NotFound(new { error = "Card playback file not found." });

        var stream = previewService.OpenRead(playbackPath);
        return stream is null
            ? Results.NotFound(new { error = "Card playback file not found." })
            : Results.File(stream, previewService.GetContentType(playbackPath), enableRangeProcessing: true);
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

    private static async Task<IResult> GetCardPosterAsync(
        CardRepository repository,
        PreviewService previewService,
        long id)
    {
        var card = await repository.GetByIdAsync(id);
        var posterPath = card?.Metadata["poster_path"]?.GetValue<string>();
        if (posterPath is null)
            return Results.NotFound(new { error = "Card poster not found." });

        var stream = previewService.OpenRead(posterPath);
        return stream is null
            ? Results.NotFound(new { error = "Card poster file not found." })
            : Results.File(stream, previewService.GetContentType(posterPath), enableRangeProcessing: true);
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

    private static IReadOnlyList<long> ParseLongList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => long.TryParse(item, out var parsed) ? parsed : 0)
            .Where(item => item > 0)
            .Distinct()
            .ToList();
    }

    private static long? ParseLongOrNull(string? value) =>
        long.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
}
