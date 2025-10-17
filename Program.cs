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

                // 4.5. Prioritize resuming active upload session
                var savedSession = sessionPersistence.LoadSession();
                if (savedSession != null && syncResult.FilesToUpload.Count > 0)
                {
                    // Find the file with active session in the upload queue
                    int sessionFileIndex = syncResult.FilesToUpload.FindIndex(f =>
                        string.Equals(f.FullPath, savedSession.FilePath, StringComparison.OrdinalIgnoreCase));

                    if (sessionFileIndex > 0)
                    {
                        // Move the file to the front of the queue
                        var fileToResume = syncResult.FilesToUpload[sessionFileIndex];
                        syncResult.FilesToUpload.RemoveAt(sessionFileIndex);
                        syncResult.FilesToUpload.Insert(0, fileToResume);
                        progress.ReportMessage($"Prioritizing resume of {fileToResume.RelativePath} (session found)");
                    }
                    else if (sessionFileIndex == -1)
                    {
                        // Session file is no longer in the upload queue (might have been deleted or modified)
                        progress.ReportMessage($"Session file no longer needs upload, clearing session");
                        sessionPersistence.DeleteSession();
                    }
                    // If sessionFileIndex == 0, file is already at the front, no action needed
                }

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

                        for (int attempt = 0;; attempt++)
                        {
                            try
                            {
                                reader.NextFile = (fileToUpload.FullPath, null);
                                reader.OpenNextFile();

                                if (i < syncResult.FilesToUpload.Count - 1)
                                    reader.NextFile = (syncResult.FilesToUpload[i + 1].FullPath, null);

                                await strategy.UploadFileAsync(fileToUpload, reader);
                                break;
                            }
                            catch (ResumeFailedException ex) 
                            {
                                progress.ReportMessage($"Resume upload failed, retrying from scratch: {ex.Message}");
                                sessionPersistence.DeleteSession();
                            }
                            catch (Exception ex) when (attempt < 2)
                            {
                                progress.ReportMessage($"Upload failed, retrying: {ex.Message}");
                                await Task.Delay(5000);
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