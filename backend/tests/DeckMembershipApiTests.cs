using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class DeckMembershipApiTests
{
    private static async Task<long[]> CardIdsAsync(HttpResponseMessage response)
    {
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return body!.RootElement.GetProperty("cards").EnumerateArray()
            .Select(c => c.GetProperty("id").GetInt64()).ToArray();
    }

    private static async Task<long[]> DeckIdsOfCardAsync(DeckApiHost host, long cardId)
    {
        using var response = await host.Client.GetAsync($"/api/cards/{cardId}/decks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return body!.RootElement.EnumerateArray().Select(d => d.GetProperty("id").GetInt64()).ToArray();
    }

    [DatabaseFact]
    public async Task PutAddsCardIdempotentlyAndDeleteRemovesIt()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var first = await host.InsertCardAsync("media", "member-1");
        var second = await host.InsertCardAsync("media", "member-2");
        var deckId = await host.CreateDeckAsync("membership deck", first);

        using var added = await host.Client.PutAsync($"/api/decks/{deckId}/cards/{second}", null);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(new[] { first, second }, await CardIdsAsync(added));

        using var again = await host.Client.PutAsync($"/api/decks/{deckId}/cards/{second}", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(new[] { first, second }, await CardIdsAsync(again));

        using var removed = await host.Client.DeleteAsync($"/api/decks/{deckId}/cards/{first}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(new[] { second }, await CardIdsAsync(removed));

        using var removedAgain = await host.Client.DeleteAsync($"/api/decks/{deckId}/cards/{first}");
        Assert.Equal(HttpStatusCode.OK, removedAgain.StatusCode);
        Assert.Equal(new[] { second }, await CardIdsAsync(removedAgain));
    }

    [DatabaseFact]
    public async Task MissingDeckIs404AndMissingCardOnAddIs400()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var card = await host.InsertCardAsync("media", "exists");
        var deckId = await host.CreateDeckAsync("existing deck");

        using var noDeckPut = await host.Client.PutAsync($"/api/decks/{deckId + 9999}/cards/{card}", null);
        using var noDeckDelete = await host.Client.DeleteAsync($"/api/decks/{deckId + 9999}/cards/{card}");
        using var noCardPut = await host.Client.PutAsync($"/api/decks/{deckId}/cards/{card + 9999}", null);

        Assert.Equal(HttpStatusCode.NotFound, noDeckPut.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noDeckDelete.StatusCode);
        // Existing behaviour (DeckEndpoints.AddCardAsync): InvalidOperationException -> 400.
        Assert.Equal(HttpStatusCode.BadRequest, noCardPut.StatusCode);
        Assert.Empty(await CardIdsAsync(await host.Client.GetAsync($"/api/decks/{deckId}")));
    }

    [DatabaseFact]
    public async Task FavoritesSystemDeckRejectsMembershipEdits()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var card = await host.InsertCardAsync("media", "fav-target");
        var otherDeck = await host.CreateDeckAsync("not favorites", card);
        // The favorites deck is created lazily by the repository.
        var favorites = await new MyApp.Api.Decks.DeckRepository(host.Configuration).GetFavoritesAsync();

        using var put = await host.Client.PutAsync($"/api/decks/{favorites.Id}/cards/{card}", null);
        using var delete = await host.Client.DeleteAsync($"/api/decks/{favorites.Id}/cards/{card}");

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Equal(new[] { otherDeck }, await DeckIdsOfCardAsync(host, card));
    }

    [DatabaseFact]
    public async Task CardInTwoDecksListsBothDecks()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var card = await host.InsertCardAsync("media", "shared-card");
        var lonely = await host.InsertCardAsync("media", "lonely-card");
        var deckA = await host.CreateDeckAsync("deck A", card);
        var deckB = await host.CreateDeckAsync("deck B");
        using var added = await host.Client.PutAsync($"/api/decks/{deckB}/cards/{card}", null);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        Assert.Equal(new[] { deckA, deckB }, (await DeckIdsOfCardAsync(host, card)).Order().ToArray());
        Assert.Empty(await DeckIdsOfCardAsync(host, lonely));
    }
}
