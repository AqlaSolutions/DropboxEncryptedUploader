using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Implementation of storage recycling service.
/// </summary>
public class StorageRecyclingService(
    IDropboxService dropbox,
    IProgressReporter progress,
    Configuration.Configuration config)
    : IStorageRecyclingService
{
    /// <summary>
    /// Recycles deleted files within the configured age range.
    /// This is a convenience method that orchestrates the entire recycling process.
    /// </summary>
    public async Task RecycleDeletedFilesAsync(SyncResult syncResult)
    {
        progress.ReportMessage("Recycling deleted files for endless storage");

        var deletedFiles = await ListRecyclableDeletedFilesAsync(syncResult);
        await RestoreAndDeleteFilesAsync(deletedFiles);
    }

    /// <summary>
    /// Lists all recyclable deleted files from Dropbox that meet the criteria.
    /// </summary>
    public async Task<List<(string PathDisplay, string PathLower, FileMetadata Metadata)>> ListRecyclableDeletedFilesAsync(SyncResult syncResult)
    {
        var recyclableFiles = new List<(string PathDisplay, string PathLower, FileMetadata Metadata)>();

        foreach (var entry in await dropbox.ListAllFilesAsync(config.DropboxDirectory, includeDeleted: true))
        {
            // Skip if not deleted or if file still exists
            if (!entry.IsDeleted || syncResult.ExistingFiles.Contains(entry.PathLower))
                continue;

            // Check if parent folder exists
            var parentFolder = entry.PathLower;
            int lastSlash = parentFolder.LastIndexOf('/');
            if (lastSlash == -1)
                continue;
            parentFolder = parentFolder.Substring(0, lastSlash);
            if (!syncResult.ExistingFolders.Contains(parentFolder))
                continue;

            // Get revisions
            ListRevisionsResult rev;
            try
            {
                rev = await dropbox.ListRevisionsAsync(
                    entry.AsDeleted.PathLower,
                    ListRevisionsMode.Path.Instance,
                    1);
            }
            catch
            {
                // ListRevisions doesn't work for folders, no way to check beforehand
                continue;
            }

            // Check age: must be between 15 and 29 days (inclusive)
            var age = DateTime.UtcNow - rev.ServerDeleted;
            if (!(age >= TimeSpan.FromDays(config.MinRecycleAgeDays)) ||
                age > TimeSpan.FromDays(config.MaxRecycleAgeDays))
            {
                // Don't need to restore too young, can't restore too old
                continue;
            }

            // Get newest revision by ClientModified
            var newestRevision = rev.Entries.OrderByDescending(x => x.ClientModified).First();

            recyclableFiles.Add((entry.PathDisplay, entry.AsDeleted.PathLower, newestRevision));
        }

        return recyclableFiles;
    }

    /// <summary>
    /// Restores and immediately deletes files in batches.
    /// </summary>
    public async Task RestoreAndDeleteFilesAsync(List<(string PathDisplay, string PathLower, FileMetadata Metadata)> filesToRecycle)
    {
        var filesToDelete = new HashSet<string>();
        ulong deletingAccumulatedSize = 0;

        foreach (var (pathDisplay, pathLower, metadata) in filesToRecycle)
        {
            progress.ReportMessage("Restoring " + pathDisplay);
            try
            {
                // Restore with specified revision
                var restored = await dropbox.RestoreAsync(pathDisplay, metadata.Rev);

                // Delete immediately if file >= 32GB and queue is empty
                if (restored.AsFile.Size >= config.DeletingBatchSize && filesToDelete.Count == 0)
                {
                    progress.ReportMessage("Deleting " + pathDisplay);
                    await dropbox.DeleteFileAsync(restored.PathLower, restored.Rev);
                }
                else
                {
                    // Add to batch deletion queue
                    filesToDelete.Add(restored.PathLower);
                    deletingAccumulatedSize += restored.Size;

                    // Delete batch when accumulated size reaches threshold
                    if (deletingAccumulatedSize >= config.DeletingBatchSize)
                    {
                        await DeleteFilesInBatchAsync(filesToDelete);
                        filesToDelete.Clear();
                        deletingAccumulatedSize = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but continue processing other files
                progress.ReportMessage("Error recycling file: " + ex);
            }
        }

        // Delete remaining files in batch
        await DeleteFilesInBatchAsync(filesToDelete);
    }

    /// <summary>
    /// Deletes a batch of files.
    /// </summary>
    public async Task DeleteFilesInBatchAsync(HashSet<string> filesToDelete)
    {
        if (filesToDelete.Count > 0)
        {
            progress.ReportMessage($"Deleting files: \n{string.Join("\n", filesToDelete)}");
            await dropbox.DeleteBatchAsync(filesToDelete);
        }
    }
}
