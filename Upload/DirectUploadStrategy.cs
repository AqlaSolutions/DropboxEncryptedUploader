using System;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;
using DropboxEncrypedUploader.Services;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Upload strategy that uploads files directly without encryption.
/// </summary>
public class DirectUploadStrategy : BaseUploadStrategy, IUploadStrategy
{
    private readonly IProgressReporter _progress;

    public DirectUploadStrategy(
        IUploadSessionManager sessionManager,
        IProgressReporter progress,
        ISessionPersistenceService sessionPersistence)
        : base(sessionManager, sessionPersistence, progress)
    {
        _progress = progress;
    }

    public async Task UploadFileAsync(FileToUpload fileToUpload, AsyncMultiFileReader reader)
    {
        _progress.ReportProgress(fileToUpload.RelativePath, 0, fileToUpload.FileSize);

        var commitInfo = CreateCommitInfo(fileToUpload);

        // Handle empty files explicitly (no need to read from reader)
        if (fileToUpload.FileSize == 0)
        {
            await SessionManager.SimpleUploadAsync(commitInfo, [], 0);
            _progress.ReportComplete(fileToUpload.RelativePath, "(empty file)");
            return;
        }

        PrepareUpload(fileToUpload);
        long totalBytesRead = 0;

        int read;
        while ((read = reader.ReadNextBlock()) > 0)
        {
            totalBytesRead += read;
            _progress.ReportProgress(fileToUpload.RelativePath, totalBytesRead, fileToUpload.FileSize);

            bool isLastChunk = totalBytesRead >= fileToUpload.FileSize;

            if (isLastChunk)
            {
                await FinishUploadAsync(commitInfo, reader.CurrentBuffer, read);
            }
            else
            {
                await UploadChunkAsync(reader.CurrentBuffer, read);
            }
        }

        _progress.ReportComplete(fileToUpload.RelativePath);
    }
}
