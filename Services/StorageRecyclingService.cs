using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services
{
    /// <summary>
    /// Implementation of storage recycling service.
    /// </summary>
    public class StorageRecyclingService : IStorageRecyclingService
    {
        private readonly IDropboxService _dropbox;
        private readonly IProgressReporter _progress;
        private readonly Configuration.Configuration _config;

        public StorageRecyclingService(
            IDropboxService dropbox,
            IProgressReporter progress,
            Configuration.Configuration config)
        {
            _dropbox = dropbox;
            _progress = progress;
            _config = config;
        }

        public async Task RecycleDeletedFilesAsync(SyncResult syncResult)
        {
            _progress.ReportMessage("Recycling deleted files for endless storage");

            var filesToDelete = new HashSet<string>();
            ulong deletingAccumulatedSize = 0;

            foreach (var entry in await _dropbox.ListAllFilesAsync(_config.DropboxDirectory, includeDeleted: true))
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
                    rev = await _dropbox.ListRevisionsAsync(
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
                if (!(age >= TimeSpan.FromDays(_config.MinRecycleAgeDays)) ||
                    (age > TimeSpan.FromDays(_config.MaxRecycleAgeDays)))
                {
                    // Don't need to restore too young, can't restore too old
                    continue;
                }

                _progress.ReportMessage("Restoring " + entry.PathDisplay);
                try
                {
                    // Restore with newest revision by ClientModified
                    var restored = await _dropbox.RestoreAsync(
                        entry.PathDisplay,
                        rev.Entries.OrderByDescending(x => x.ClientModified).First().Rev);

                    // Delete immediately if file >= 32GB and queue is empty
                    if (restored.AsFile.Size >= _config.DeletingBatchSize && filesToDelete.Count == 0)
                    {
                        _progress.ReportMessage("Deleting " + entry.PathDisplay);
                        await _dropbox.DeleteFileAsync(restored.PathLower, restored.Rev);
                    }
                    else
                    {
                        // Add to batch deletion queue
                        filesToDelete.Add(restored.PathLower);
                        deletingAccumulatedSize += restored.Size;

                        // Delete batch when accumulated size reaches threshold
                        if (deletingAccumulatedSize >= _config.DeletingBatchSize)
                        {
                            await DeleteBatchAsync(filesToDelete);
                            filesToDelete.Clear();
                            deletingAccumulatedSize = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue processing other files
                    _progress.ReportMessage("Error recycling file: " + ex.ToString());
                }
            }

            // Delete remaining files in batch
            await DeleteBatchAsync(filesToDelete);
        }

        private async Task DeleteBatchAsync(HashSet<string> filesToDelete)
        {
            if (filesToDelete.Count > 0)
            {
                _progress.ReportMessage($"Deleting files: \n{string.Join("\n", filesToDelete)}");
                await _dropbox.DeleteBatchAsync(filesToDelete);
            }
        }
    }
}
