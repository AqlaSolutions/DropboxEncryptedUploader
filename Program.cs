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
            // 1. Clean up old session files
            Configuration.Configuration.CleanupOldSessions();

            // 2. Parse and validate configuration
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
            var sessionPersistence = new SessionPersistenceService(config.SessionFilePath);
            var sessionManager = new UploadSessionManager(dropbox, config.MaxRetries);
            var encryptedUploadStrategy = new EncryptedUploadStrategy(sessionManager, progress, config, sessionPersistence);
            var directUploadStrategy = new DirectUploadStrategy(sessionManager, progress, sessionPersistence);
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

                        // Upload file with retry on any exception (up to 3 attempts)
                        for (int attempt = 0;; attempt++)
                        {
                            try
                            {
                                if (attempt > 0)
                                {
                                    // Retry: reopen file from start
                                    progress.ReportMessage($"Retry attempt {attempt + 1}/3 for {fileToUpload.RelativePath}");
                                    reader.NextFile = (fileToUpload.FullPath, null);
                                    reader.OpenNextFile();

                                    // Reset next-file optimization
                                    if (i < syncResult.FilesToUpload.Count - 1)
                                    {
                                        reader.NextFile = (syncResult.FilesToUpload[i + 1].FullPath, null);
                                    }
                                }

                                await strategy.UploadFileAsync(fileToUpload, reader);
                                break;
                            }
                            catch (ResumeFailedException ex) when (attempt < 3)
                            {
                                // Session is invalid - ensure it's deleted and retry from scratch
                                sessionPersistence.DeleteSession();
                                progress.ReportMessage($"Resume failed, starting fresh: {ex.Message}");
                            }
                            catch (ResumeFailedException)
                            {
                                // Final attempt failed - ensure session is deleted before propagating
                                sessionPersistence.DeleteSession();
                                throw;
                            }
                            catch (Exception ex) when (attempt < 3)
                            {
                                progress.ReportMessage($"Upload failed, retrying: {ex.Message}");
                            }
                        }
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