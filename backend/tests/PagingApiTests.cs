using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyApp.Api.Tests;

/// <summary>limit/offset paging and the optional X-Total-Count header of list endpoints.</summary>
[Collection("Collection database")]
public sealed class PagingApiTests
{
    private static async Task<(JsonElement[] Items, long? Total)> GetPageAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        long? total = response.Headers.TryGetValues("X-Total-Count", out var values)
            ? long.Parse(values.Single()) : null;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray(), total);
    }

    private static long Id(JsonElement item) => item.GetProperty("id").GetInt64();

    [DatabaseFact]
    public async Task CardListPagesDoNotOverlapAndReportTotalOnlyWhenAsked()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var created = new List<long>();
        for (var seed = 0; seed < 5; seed++) created.Add(await host.CreateImageCardAsync(100 + seed));

        var seen = new List<long>();
        for (var offset = 0; offset < 6; offset += 2)
        {
            var (items, total) = await GetPageAsync(host.Client,
                $"/api/cards?types=media&limit=2&offset={offset}&include_total=true");
            Assert.Equal(5, total);
            Assert.Equal(offset < 4 ? 2 : 1, items.Length);
            seen.AddRange(items.Select(Id));
        }
        Assert.Equal(created.OrderBy(id => id), seen.OrderBy(id => id));

        var (_, withoutTotal) = await GetPageAsync(host.Client, "/api/cards?types=media&limit=2");
        Assert.Null(withoutTotal);
    }

    [DatabaseFact]
    public async Task TagSourceAndDeckListsPageWithTotals()
    {
        await using var host = await ApiTestHost.CreateAsync(withLibraryApi: true);
        for (var index = 0; index < 5; index++)
        {
            await (host.Client.PostAsJsonAsync("/api/tags", new { name = $"pagetag {index}", aliases = Array.Empty<string>() }))
                .EnsureOk();
            await (host.Client.PostAsJsonAsync("/api/sources", new { name = $"pagesource {index}" })).EnsureOk();
            await (host.Client.PostAsJsonAsync("/api/decks", new { title = $"pagedeck {index}" })).EnsureOk();
        }

        var (tags, tagTotal) = await GetPageAsync(host.Client, "/api/tags?search=pagetag&limit=2&offset=2&include_total=true");
        Assert.Equal(5, tagTotal);
        Assert.Equal(["pagetag 2", "pagetag 3"], tags.Select(tag => tag.GetProperty("name").GetString()));

        var (sources, sourceTotal) = await GetPageAsync(host.Client, "/api/sources?search=pagesource&limit=3&offset=3&include_total=true");
        Assert.Equal(5, sourceTotal);
        Assert.Equal(["pagesource 3", "pagesource 4"], sources.Select(source => source.GetProperty("name").GetString()));

        var (decks, deckTotal) = await GetPageAsync(host.Client, "/api/decks?search=pagedeck&order=asc&limit=4&offset=4&include_total=true");
        Assert.Equal(5, deckTotal);
        Assert.Equal(["pagedeck 4"], decks.Select(deck => deck.GetProperty("title").GetString()));

        // The whole catalog by name pages inside the candidate query; pages must still line up.
        var (allByName, allTotal) = await GetPageAsync(host.Client, "/api/tags?sort=name&limit=200&include_total=true");
        var (firstTwo, _) = await GetPageAsync(host.Client, "/api/tags?sort=name&limit=2");
        var (nextTwo, _) = await GetPageAsync(host.Client, "/api/tags?sort=name&limit=2&offset=2");
        Assert.Equal(allByName.Length, allTotal);
        Assert.Equal(allByName.Take(4).Select(Id), firstTwo.Concat(nextTwo).Select(Id));

        using var invalid = await host.Client.GetAsync("/api/tags?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [DatabaseFact]
    public async Task FavoritesCanBeReadWithoutCardsAndPagedThroughTheCardList()
    {
        await using var host = await ApiTestHost.CreateAsync(withLibraryApi: true);
        for (var seed = 0; seed < 3; seed++)
        {
            var id = await host.CreateImageCardAsync(200 + seed);
            await (host.Client.PutAsync($"/api/favorites/{id}", null)).EnsureOk();
        }

        using var response = await host.Client.GetAsync("/api/favorites?include_cards=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var deckId = body.RootElement.GetProperty("id").GetInt64();
        Assert.Equal(3, body.RootElement.GetProperty("cardCount").GetInt32());
        Assert.True(!body.RootElement.TryGetProperty("cards", out var cards) || cards.GetArrayLength() == 0);

        var (page, total) = await GetPageAsync(host.Client, $"/api/cards?deck_id={deckId}&limit=2&offset=2&include_total=true");
        Assert.Equal(3, total);
        Assert.Single(page);
    }
}

internal static class PagingResponseExtensions
{
    public static async Task EnsureOk(this Task<HttpResponseMessage> request)
    {
        using var response = await request;
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
