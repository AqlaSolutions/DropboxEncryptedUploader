using System;
using System.IO;
using System.Threading.Tasks;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Services;
using DropboxEncrypedUploader.Upload;
using ICSharpCode.SharpZipLib.Zip;

namespace DropboxEncrypedUploader;

internal class Program
{
    static async Task Main(string[] args)
    {
        try
        {
            // 1. Parse and validate configuration
            var config = new Configuration.Configuration(args);

            // Configure ZIP encoding settings globally (once before any encryption operations)
            if (config.UseEncryption)
            {
                ZipStrings.UseUnicode = true;
                ZipStrings.CodePage = 65001; // UTF-8
            }

            // 2. Create services
            var progress = new ConsoleProgressReporter();
            var fileSystem = new FileSystemService();
            var dropbox = new DropboxService(config);
            var syncService = new SyncService(fileSystem, dropbox, progress, config);
            var syncFacade = new SyncFacade(syncService, fileSystem, config);
            var sessionManager = new UploadSessionManager(dropbox, config.MaxRetries);
            var encryptedUploadStrategy = new EncryptedUploadStrategy(sessionManager, progress, config);
            var directUploadStrategy = new DirectUploadStrategy(sessionManager, progress);
            var recyclingService = new StorageRecyclingService(dropbox, progress, config);

            using (dropbox)
            {
                // 3. Create Dropbox folder
                await dropbox.CreateFolderAsync(config.DropboxDirectory);

                // 4. Analyze sync
                var syncResult = await syncFacade.AnalyzeSyncAsync();

                // 5. Delete files that no longer exist locally
                if (syncResult.FilesToDelete.Count > 0)
                {
                    progress.ReportMessage($"Deleting files: \n{string.Join("\n", syncResult.FilesToDelete)}");
                    await dropbox.DeleteBatchAsync(syncResult.FilesToDelete);
                }

                // 6. Upload new/modified files
                if (syncResult.FilesToUpload.Count > 0)
                {
                    progress.ReportFileCount(syncResult.FilesToUpload.Count);

                    using var reader = new AsyncMultiFileReader(
                        config.ReadBufferSize,
                        (f, t) => new FileStream(
                            f,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            config.ReadBufferSize,
                            useAsync: true));

                    // Select strategy based on encryption setting
                    IUploadStrategy strategy = config.UseEncryption
                        ? encryptedUploadStrategy
                        : directUploadStrategy;

                    for (int i = 0; i < syncResult.FilesToUpload.Count; i++)
                    {
                        var fileToUpload = syncResult.FilesToUpload[i];

                        // Set current file and open it
                        reader.NextFile = (fileToUpload.FullPath, null);
                        reader.OpenNextFile();

                        // Set next file for pre-opening optimization
                        if (i < syncResult.FilesToUpload.Count - 1)
                        {
                            reader.NextFile = (syncResult.FilesToUpload[i + 1].FullPath, null);
                        }

                        // Upload file (no per-file try-catch - preserve fail-fast behavior)
                        await strategy.UploadFileAsync(fileToUpload, reader);
                    }
                }

                // 7. Recycle deleted files
                progress.ReportMessage("Recycling deleted files for endless storage");
                var deletedFiles = await recyclingService.ListRecyclableDeletedFilesAsync(
                    syncResult.ExistingFiles,
                    syncResult.ExistingFolders);
                await recyclingService.RestoreAndDeleteFilesAsync(deletedFiles);
            }

            progress.ReportMessage("All done");
        }
        catch (Exception e)
        {
            // Redirecting error to normal output
            Console.WriteLine(e);
            throw;
        }
    }
}