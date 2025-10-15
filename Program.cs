using System;
using System.Threading.Tasks;
using DropboxEncrypedUploader.Configuration;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Services;
using DropboxEncrypedUploader.Upload;
using ICSharpCode.SharpZipLib.Zip;

namespace DropboxEncrypedUploader
{
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
                var sessionManager = new UploadSessionManager(dropbox.Client, config.MaxRetries);
                var encryptedUploadStrategy = new EncryptedUploadStrategy(sessionManager, progress, config);
                var directUploadStrategy = new DirectUploadStrategy(sessionManager, progress);
                var recyclingService = new StorageRecyclingService(dropbox, progress, config);

                using (dropbox)
                {
                    // 3. Create Dropbox folder
                    await dropbox.CreateFolderAsync(config.DropboxDirectory);

                    // 4. Analyze sync
                    var syncResult = await syncService.AnalyzeSyncAsync();

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

                        byte[] msBuffer = new byte[config.MaxBufferAllocation];
                        using var reader = new AsyncMultiFileReader(
                            config.ReadBufferSize,
                            (f, t) => new System.IO.FileStream(
                                f,
                                System.IO.FileMode.Open,
                                System.IO.FileAccess.Read,
                                System.IO.FileShare.Read,
                                config.ReadBufferSize,
                                useAsync: true));

                        // Select strategy based on encryption setting
                        IUploadStrategy strategy = config.UseEncryption
                            ? encryptedUploadStrategy
                            : directUploadStrategy;

                        for (int i = 0; i < syncResult.FilesToUpload.Count; i++)
                        {
                            var fileToUpload = syncResult.FilesToUpload[i];

                            // Set NextFile for current upload
                            reader.NextFile = (fileToUpload.FullPath, null);

                            // Determine next file path for pre-opening optimization
                            string nextFilePath = null;
                            if (i < syncResult.FilesToUpload.Count - 1)
                            {
                                nextFilePath = syncResult.FilesToUpload[i + 1].FullPath;
                            }

                            // Upload file (no per-file try-catch - preserve fail-fast behavior)
                            await strategy.UploadFileAsync(fileToUpload, reader, msBuffer, nextFilePath);
                        }
                    }

                    // 7. Recycle deleted files
                    await recyclingService.RecycleDeletedFilesAsync(syncResult);
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
}
