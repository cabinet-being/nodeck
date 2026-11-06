using System.Diagnostics;
using MyApp.Api.Analysis;

namespace MyApp.Api.Tests;

public sealed class VideoFrameSamplerTests
{
    [Fact]
    public void LongClipSamplesAllTenSegmentsWithNoMoreThanThirtyDistinctTimes()
    {
        var times = FfmpegVideoFrameSampler.BuildSampleTimes(120);

        Assert.Equal(30, times.Count);
        Assert.Equal(10, times.Select(item => item.SegmentIndex).Distinct().Count());
        Assert.All(Enumerable.Range(0, 10), segment =>
            Assert.Equal(3, times.Count(item => item.SegmentIndex == segment)));
        Assert.Equal(times.Count, times.Select(item => item.TimestampSeconds).Distinct().Count());
        Assert.All(times, item => Assert.InRange(item.TimestampSeconds, 0, 120));
        Assert.True(times.SequenceEqual(times.OrderBy(item => item.TimestampSeconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.4)]
    [InlineData(1)]
    [InlineData(double.NaN)]
    public void ZeroOrShortGifHasOneStartSample(double duration)
    {
        var sample = Assert.Single(FfmpegVideoFrameSampler.BuildSampleTimes(duration));
        Assert.Equal(0, sample.TimestampSeconds);
        Assert.Equal(0, sample.SegmentIndex);
    }

    [Fact]
    public async Task MissingOriginalFailsBeforeStartingFfmpeg()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-video-{Guid.NewGuid():N}.gif");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new FfmpegVideoFrameSampler().SampleAsync(path, 1, default));
    }

    [Fact]
    public async Task StaticVideoRetainsSegmentOccurrencesButReusesFrameFileAndCleansUp()
    {
        if (!ToolAvailable("ffmpeg")) return;
        var root = Path.Combine(Path.GetTempPath(), $"nodeck-sampler-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var video = Path.Combine(root, "static.mp4");
        try
        {
            await RunFfmpegAsync("-f", "lavfi", "-i", "color=c=red:s=16x16:r=1",
                "-t", "12", "-c:v", "mpeg4", "-y", video);
            var sampled = await new FfmpegVideoFrameSampler().SampleAsync(video, 12, default);
            var paths = sampled.Frames.Select(frame => frame.Path).Distinct().ToArray();
            try
            {
                Assert.Equal(10, sampled.Frames.Select(frame => frame.SegmentIndex).Distinct().Count());
                Assert.InRange(sampled.Frames.Count, 1, 40);
                Assert.DoesNotContain(sampled.Frames, frame => frame.IsSceneCandidate);
                Assert.Single(paths);
                Assert.True(File.Exists(paths[0]));
            }
            finally { await sampled.DisposeAsync(); }
            Assert.False(File.Exists(paths[0]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DistinctScenesAddBoundedMidpointCandidatesAfterBaseline()
    {
        if (!ToolAvailable("ffmpeg")) return;
        var root = Path.Combine(Path.GetTempPath(), $"nodeck-scenes-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var video = Path.Combine(root, "scenes.mp4");
        try
        {
            await RunFfmpegAsync("-f", "lavfi", "-i", "color=c=red:s=64x64:r=10:d=5.7",
                "-f", "lavfi", "-i", "color=c=green:s=64x64:r=10:d=0.6",
                "-f", "lavfi", "-i", "color=c=blue:s=64x64:r=10:d=5.7",
                "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]",
                "-map", "[v]", "-c:v", "mpeg4", "-y", video);
            await using var sampled = await new FfmpegVideoFrameSampler().SampleAsync(video, 12, default);
            var baseline = sampled.Frames.Where(frame => !frame.IsSceneCandidate).ToArray();
            var candidates = sampled.Frames.Where(frame => frame.IsSceneCandidate).ToArray();

            Assert.Equal(30, baseline.Length);
            Assert.InRange(candidates.Length, 1, 10);
            Assert.InRange(sampled.Frames.Count, 31, 40);
            Assert.Equal(10, baseline.Select(frame => frame.SegmentIndex).Distinct().Count());
            Assert.All(candidates, frame => Assert.InRange(frame.TimestampSeconds, 0, 12));
            Assert.Contains(candidates, frame => frame.TimestampSeconds is > 5.7 and < 6.3);
            Assert.Equal(sampled.Frames.Count, sampled.Frames
                .Select(frame => frame.TimestampSeconds).Distinct().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool ToolAvailable(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "-version")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task RunFfmpegAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }
}
