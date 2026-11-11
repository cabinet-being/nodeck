using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Analysis;

namespace MyApp.Api.Tests;

public sealed class ModelDownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nodeck-model-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeHandler(Dictionary<string, byte[]> bodies) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private ModelDownloadService Service(FakeHandler handler, params ModelDownloadService.ModelFile[] files) =>
        new(new HttpClient(handler), files, NullLogger<ModelDownloadService>.Instance);

    [Fact]
    public async Task DownloadsVerifiedFilesAndSkipsExistingOnes()
    {
        var a = "first model"u8.ToArray();
        var b = new byte[300_000];
        new Random(3).NextBytes(b);
        var handler = new FakeHandler(new() { ["https://models.test/a"] = a, ["https://models.test/b"] = b });
        var pathA = Path.Combine(_root, "wd", "a.bin");
        var pathB = Path.Combine(_root, "whisper", "b.bin");
        var service = Service(handler,
            new ModelDownloadService.ModelFile("https://models.test/a", pathA, Sha(a)), new ModelDownloadService.ModelFile("https://models.test/b", pathB, Sha(b)));

        Assert.True(await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(a, await File.ReadAllBytesAsync(pathA));
        Assert.Equal(b, await File.ReadAllBytesAsync(pathB));
        Assert.False(File.Exists(pathA + ".download"));

        Assert.True(await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChecksumMismatchLeavesNoFileBehind()
    {
        var handler = new FakeHandler(new() { ["https://models.test/a"] = "tampered"u8.ToArray() });
        var path = Path.Combine(_root, "a.bin");
        var service = Service(handler, new ModelDownloadService.ModelFile("https://models.test/a", path, Sha("expected"u8.ToArray())));

        Assert.False(await service.RunOnceAsync(CancellationToken.None));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".download"));
    }

    [Fact]
    public async Task FailedDownloadIsReportedAndStaleTemporaryFileRemoved()
    {
        var path = Path.Combine(_root, "a.bin");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path + ".download", "partial");
        var service = Service(new FakeHandler([]), new ModelDownloadService.ModelFile("https://models.test/missing", path, Sha([1])));

        Assert.False(await service.RunOnceAsync(CancellationToken.None));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".download"));
    }
}
