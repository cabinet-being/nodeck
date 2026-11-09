using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace MyApp.Api.Storage;

internal static class MediaProcess
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(60);

    internal static string FormatSeconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    internal static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;

        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"{fileName} is not available in the backend runtime.", exception);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => process.WaitForExit((int)(timeout ?? ProcessingTimeout).TotalMilliseconds));

            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"{fileName} timed out while processing the uploaded video.");
            }

            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
    }

    internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
