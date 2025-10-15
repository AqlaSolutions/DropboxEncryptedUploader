using System.Collections.Generic;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Implements storage recycling by restoring and re-deleting old files.
/// This exploits Dropbox retention policies to extend storage.
/// </summary>
public interface IStorageRecyclingService
{
    /// <summary>
    /// Recycles deleted files within the configured age range.
    /// This is a convenience method that orchestrates the entire recycling process.
    /// </summary>
    /// <param name="syncResult">Sync result containing existing files and folders</param>
    Task RecycleDeletedFilesAsync(SyncResult syncResult);

    /// <summary>
    /// Lists all recyclable deleted files from Dropbox that meet the criteria.
    /// </summary>
    Task<List<(string PathDisplay, string PathLower, FileMetadata Metadata)>> ListRecyclableDeletedFilesAsync(SyncResult syncResult);

    /// <summary>
    /// Restores and immediately deletes files in batches.
    /// </summary>
    Task RestoreAndDeleteFilesAsync(List<(string PathDisplay, string PathLower, FileMetadata Metadata)> filesToRecycle);

    /// <summary>
    /// Deletes a batch of files.
    /// </summary>
    Task DeleteFilesInBatchAsync(HashSet<string> filesToDelete);
}
