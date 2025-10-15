using System.Threading.Tasks;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Facade that orchestrates the synchronization analysis workflow.
/// Coordinates between sync service and filesystem to determine what needs to be synced.
/// </summary>
public class SyncFacade(
    ISyncService syncService,
    IFileSystemService fileSystem,
    Configuration.Configuration config)
{
    /// <summary>
    /// Analyzes local and remote directories and returns what needs to be synced.
    /// This facade method orchestrates the sync analysis workflow.
    /// </summary>
    public async Task<SyncResult> AnalyzeSyncAsync()
    {
        var localFiles = syncService.GetLocalFiles();
        bool localDirectoryExists = fileSystem.DirectoryExists(config.LocalDirectory);
        var (existingFiles, existingFolders, remoteFilesByOriginalName) = await syncService.GetRemoteFilesAsync();
        var (filesToUpload, filesToDelete) = syncService.DetermineSync(localFiles, remoteFilesByOriginalName, localDirectoryExists);

        return new SyncResult(filesToUpload, filesToDelete, existingFiles, existingFolders);
    }
}
