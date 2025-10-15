using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Upload strategy that uploads files directly without encryption.
/// </summary>
public class DirectUploadStrategy : BaseUploadStrategy, IUploadStrategy
{
    private readonly IProgressReporter _progress;

    public DirectUploadStrategy(
        IUploadSessionManager sessionManager,
        IProgressReporter progress)
        : base(sessionManager)
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

        UploadSessionStartResult session = null;
        long offset = 0;
        long totalBytesRead = 0;

        int read;
        while ((read = reader.ReadNextBlock()) > 0)
        {
            totalBytesRead += read;
            _progress.ReportProgress(fileToUpload.RelativePath, totalBytesRead, fileToUpload.FileSize);

            // No data copy needed - use reader.CurrentBuffer directly
            // This is safe because the buffer is stable until the next ReadNextBlock() call,
            // and upload completes (with retries) before we call ReadNextBlock() again

            // Check if this is the last chunk
            bool isLastChunk = totalBytesRead >= fileToUpload.FileSize;

            if (isLastChunk)
            {
                // Last chunk: use Finish or Upload
                await FinishUploadAsync(session, offset, commitInfo, reader.CurrentBuffer, read);
            }
            else
            {
                // Not last chunk: use Start or Append
                session = await UploadChunkAsync(session, offset, reader.CurrentBuffer, read);
            }

            offset += read;
        }

        _progress.ReportComplete(fileToUpload.RelativePath);
    }
}
