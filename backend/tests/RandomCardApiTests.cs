using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MyApp.Api.Catalog;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class RandomCardApiTests
{
    private static async Task<(HttpStatusCode Status, long? Id)> RandomAsync(DeckApiHost host, string query = "")
    {
        var response = await host.Client.GetAsync($"/api/cards/random{(query.Length > 0 ? "?" + query : "")}");
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.GetProperty("id").GetInt64());
    }

    private static async Task<HashSet<long>> DrawAsync(DeckApiHost host, string query, int times = 60)
    {
        var seen = new HashSet<long>();
        for (var i = 0; i < times; i++)
        {
            var (status, id) = await RandomAsync(host, query);
            Assert.Equal(HttpStatusCode.OK, status);
            seen.Add(id!.Value);
        }
        return seen;
    }

    [DatabaseFact]
    public async Task DefaultPicksEveryTopLevelContentCardButNoPagesOrLegacyCards()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var first = await host.InsertCardAsync("media", "first");
        var second = await host.InsertCardAsync("media", "second");
        var comic = await host.InsertCardAsync("comic", "comic");
        var page = await host.InsertCardAsync("media", "page");
        await host.AddChildAsync(comic, page, 0);
        await host.InsertCardAsync("tag", "legacy tag card");

        // Three choices, 60 draws: missing one by chance has a probability of about 1e-10.
        Assert.Equal(new HashSet<long> { first, second, comic }, await DrawAsync(host, ""));
        // The pages view picks media pages too.
        Assert.Equal(new HashSet<long> { first, second, page },
            await DrawAsync(host, "types=media&exclude_contained_media=false"));
    }

    [DatabaseFact]
    public async Task GalleryFiltersAndExcludeAreRespected()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var tag = (await new CatalogRepository(host.Configuration).CreateTagAsync(new TagRequest($"random-{token}", null))).Id;
        var assignments = new CardCatalogRepository(host.Configuration);
        var taggedA = await host.InsertCardAsync("media", "tagged a");
        var taggedB = await host.InsertCardAsync("media", "tagged b");
        await host.InsertCardAsync("media", "plain");
        await assignments.SetTagAsync(taggedA, tag);
        await assignments.SetTagAsync(taggedB, tag);

        Assert.Equal(new HashSet<long> { taggedA, taggedB }, await DrawAsync(host, $"catalog_tags={tag}", 40));
        Assert.Equal(new HashSet<long> { taggedB }, await DrawAsync(host, $"catalog_tags={tag}&exclude={taggedA}", 10));
        Assert.Equal(new HashSet<long> { taggedA }, await DrawAsync(host, $"search=tagged%20a", 5));
    }

    [DatabaseFact]
    public async Task NoMatchIsNotFoundAndInvalidParametersAreRejected()
    {
        await using var host = await DeckApiHost.CreateAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await RandomAsync(host)).Status);

        var only = await host.InsertCardAsync("media", "only");
        Assert.Equal(HttpStatusCode.NotFound, (await RandomAsync(host, $"exclude={only}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await RandomAsync(host, "exclude=abc")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await RandomAsync(host, "types=unknown")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await RandomAsync(host, "deck_id=999999")).Status);
        Assert.Equal((HttpStatusCode.OK, only), await RandomAsync(host));
    }
}
