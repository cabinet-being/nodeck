using MyApp.Api.Cards;
using static MyApp.Api.Storage.StorageDirectories;

namespace MyApp.Api.Storage;

public sealed class PendingCardFileReplacement
{
    private readonly string _stagingRoot;
    private readonly string _stagingCardDirectory;
    private readonly string _stagingPreviewDirectory;
    private readonly string _finalCardDirectory;
    private readonly string _finalPreviewDirectory;
    private readonly string _backupRoot;
    private readonly string _backupCardDirectory;
    private readonly string _backupPreviewDirectory;

    public PendingCardFileReplacement(
        string appDataRoot,
        string stagingRoot,
        string stagingCardDirectory,
        string stagingPreviewDirectory,
        string finalCardDirectory,
        string finalPreviewDirectory,
        CardFileAssets assets)
    {
        _stagingRoot = stagingRoot;
        _stagingCardDirectory = stagingCardDirectory;
        _stagingPreviewDirectory = stagingPreviewDirectory;
        _finalCardDirectory = finalCardDirectory;
        _finalPreviewDirectory = finalPreviewDirectory;
        _backupRoot = Path.Combine(appDataRoot, "tmp", "replacement-backups", Guid.NewGuid().ToString("N"));
        _backupCardDirectory = Path.Combine(_backupRoot, "cards");
        _backupPreviewDirectory = Path.Combine(_backupRoot, "preview");
        Assets = assets;
    }

    public CardFileAssets Assets { get; }

    public Task PromoteAsync()
    {
        Directory.CreateDirectory(_backupRoot);

        MoveDirectoryIfExists(_finalCardDirectory, _backupCardDirectory);
        MoveDirectoryIfExists(_finalPreviewDirectory, _backupPreviewDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_finalCardDirectory)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_finalPreviewDirectory)!);
        Directory.Move(_stagingCardDirectory, _finalCardDirectory);
        Directory.Move(_stagingPreviewDirectory, _finalPreviewDirectory);

        return Task.CompletedTask;
    }

    public void Rollback()
    {
        DeleteDirectoryIfExists(_finalCardDirectory);
        DeleteDirectoryIfExists(_finalPreviewDirectory);
        MoveDirectoryIfExists(_backupCardDirectory, _finalCardDirectory);
        MoveDirectoryIfExists(_backupPreviewDirectory, _finalPreviewDirectory);
        CleanupStaging();
    }

    public void CleanupAfterCommit()
    {
        DeleteDirectoryIfExists(_backupRoot);
        CleanupStaging();
    }

    public void CleanupStaging()
    {
        DeleteDirectoryIfExists(_stagingRoot);
    }
}
