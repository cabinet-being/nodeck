namespace MyApp.Api.Storage;

public sealed class PendingPreviewReplacement
{
    private readonly string _stagingRoot;
    private readonly string _stagingPreviewDirectory;
    private readonly string _finalPreviewDirectory;
    private readonly string _backupPreviewDirectory;
    private bool _oldPreviewBackedUp;
    private bool _newPreviewPromoted;

    public PendingPreviewReplacement(string stagingRoot, string stagingPreviewDirectory,
        string finalPreviewDirectory, string backupPreviewDirectory, double previewStartSeconds,
        string previewSelection = "auto", string previewFileName = "preview.gif", double? previewScore = null)
    {
        _stagingRoot = stagingRoot;
        _stagingPreviewDirectory = stagingPreviewDirectory;
        _finalPreviewDirectory = finalPreviewDirectory;
        _backupPreviewDirectory = backupPreviewDirectory;
        PreviewStartSeconds = previewStartSeconds;
        PreviewSelection = previewSelection;
        PreviewFileName = previewFileName;
        PreviewScore = previewScore;
    }

    public double PreviewStartSeconds { get; }
    public string PreviewSelection { get; }
    public string PreviewFileName { get; }
    public double? PreviewScore { get; }

    public Task PromoteAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPreviewDirectory)!);
        if (Directory.Exists(_finalPreviewDirectory))
        {
            Directory.Move(_finalPreviewDirectory, _backupPreviewDirectory);
            _oldPreviewBackedUp = true;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_finalPreviewDirectory)!);
        Directory.Move(_stagingPreviewDirectory, _finalPreviewDirectory);
        _newPreviewPromoted = true;
        return Task.CompletedTask;
    }

    public void Rollback()
    {
        if (_newPreviewPromoted && Directory.Exists(_finalPreviewDirectory))
            Directory.Delete(_finalPreviewDirectory, recursive: true);
        if (_oldPreviewBackedUp && Directory.Exists(_backupPreviewDirectory))
            Directory.Move(_backupPreviewDirectory, _finalPreviewDirectory);
        if (Directory.Exists(_stagingRoot))
            Directory.Delete(_stagingRoot, recursive: true);
    }

    public void CleanupAfterCommit()
    {
        if (Directory.Exists(_backupPreviewDirectory))
            Directory.Delete(_backupPreviewDirectory, recursive: true);
        if (Directory.Exists(_stagingRoot))
            Directory.Delete(_stagingRoot, recursive: true);
    }
}
