namespace MyApp.Api.Storage;

/// <summary>Limits how many media files are processed (ffmpeg/ImageSharp) at the same time.</summary>
internal sealed class MediaProcessingGate(int limit)
{
    private readonly SemaphoreSlim _semaphore = new(Math.Max(1, limit), Math.Max(1, limit));

    internal async Task<IDisposable> AcquireAsync()
    {
        await _semaphore.WaitAsync();
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
