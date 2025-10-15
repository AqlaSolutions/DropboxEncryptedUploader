using System.Collections.Generic;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Analyzes local and remote directories to determine sync operations needed.
/// </summary>
public interface ISyncService
{
    /// <summary>
    /// Analyzes local and remote directories and returns what needs to be synced.
    /// This is a convenience method that orchestrates the sync analysis.
    /// </summary>
    Task<SyncResult> AnalyzeSyncAsync();

    /// <summary>
    /// Gets all local files as a set of relative paths.
    /// </summary>
    HashSet<string> GetLocalFiles();

    /// <summary>
    /// Gets all remote files and folders from Dropbox.
    /// Returns existing files set, existing folders set, and a mapping of original filenames to remote metadata.
    /// </summary>
    Task<(HashSet<string> ExistingFiles, HashSet<string> ExistingFolders, Dictionary<string, FileMetadata> RemoteFilesByOriginalName)> GetRemoteFilesAsync();

    /// <summary>
    /// Determines which files need to be uploaded and which need to be deleted.
    /// </summary>
    (List<FileToUpload> FilesToUpload, HashSet<string> FilesToDelete) DetermineSync(
        HashSet<string> localFiles,
        Dictionary<string, FileMetadata> remoteFilesByOriginalName);
}
