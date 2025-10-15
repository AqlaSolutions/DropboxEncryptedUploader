using System.Collections.Generic;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Provides storage recycling operations for restoring and re-deleting old files.
/// This exploits Dropbox retention policies to extend storage.
/// </summary>
public interface IStorageRecyclingService
{
    /// <summary>
    /// Lists all recyclable deleted files from Dropbox that meet the criteria.
    /// </summary>
    Task<List<(string PathDisplay, string PathLower, FileMetadata Metadata)>> ListRecyclableDeletedFilesAsync(
        HashSet<string> existingFiles,
        HashSet<string> existingFolders);

    /// <summary>
    /// Restores and immediately deletes files in batches.
    /// </summary>
    Task RestoreAndDeleteFilesAsync(List<(string PathDisplay, string PathLower, FileMetadata Metadata)> filesToRecycle);

    /// <summary>
    /// Deletes a batch of files.
    /// </summary>
    Task DeleteFilesInBatchAsync(HashSet<string> filesToDelete);
}
