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

    public async Task UploadFileAsync(FileToUpload fileToUpload, AsyncMultiFileReader reader, byte[] buffer, string nextFilePathForPreOpen)
    {
        _progress.ReportProgress(fileToUpload.RelativePath, 0, fileToUpload.FileSize);

        var commitInfo = CreateCommitInfo(fileToUpload);

        // Handle empty files explicitly
        if (fileToUpload.FileSize == 0)
        {
            await SessionManager.SimpleUploadAsync(commitInfo, [], 0);
            _progress.ReportComplete(fileToUpload.RelativePath, "(empty file)");
            return;
        }

        reader.OpenNextFile();

        // Set next file for pre-opening optimization (after OpenNextFile clears it)
        if (nextFilePathForPreOpen != null)
        {
            reader.NextFile = (nextFilePathForPreOpen, null);
        }

        UploadSessionStartResult session = null;
        long offset = 0;
        long totalBytesRead = 0;

        int read;
        while ((read = reader.ReadNextBlock()) > 0)
        {
            totalBytesRead += read;
            _progress.ReportProgress(fileToUpload.RelativePath, totalBytesRead, fileToUpload.FileSize);

            // Check if this is the last chunk
            bool isLastChunk = totalBytesRead >= fileToUpload.FileSize;

            if (isLastChunk)
            {
                // Last chunk: use Finish or Upload
                await FinishUploadAsync(session, offset, commitInfo, buffer, read);
            }
            else
            {
                // Not last chunk: use Start or Append
                session = await UploadChunkAsync(session, offset, buffer, read);
            }

            offset += read;
        }

        _progress.ReportComplete(fileToUpload.RelativePath);
    }
}
