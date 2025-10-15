using System;
using System.IO;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Upload
{
    /// <summary>
    /// Upload strategy that uploads files directly without encryption.
    /// </summary>
    public class DirectUploadStrategy : IUploadStrategy
    {
        private readonly IUploadSessionManager _sessionManager;
        private readonly IProgressReporter _progress;

        public DirectUploadStrategy(
            IUploadSessionManager sessionManager,
            IProgressReporter progress)
        {
            _sessionManager = sessionManager;
            _progress = progress;
        }

        public async Task UploadFileAsync(FileToUpload fileToUpload, AsyncMultiFileReader reader, byte[] buffer, string nextFilePathForPreOpen)
        {
            _progress.ReportProgress(fileToUpload.RelativePath, 0, fileToUpload.FileSize);

            var commitInfo = CreateCommitInfo(fileToUpload);

            // Handle empty files explicitly
            if (fileToUpload.FileSize == 0)
            {
                await _sessionManager.SimpleUploadAsync(commitInfo, [], 0, null);
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

                var bufferStream = new MemoryStream(buffer);
                bufferStream.Write(reader.CurrentBuffer, 0, read);
                bufferStream.Position = 0;
                var length = bufferStream.Length;
                string contentHash = UploadSessionManager.ComputeContentHash(buffer, (int)length);

                // Check if this is the last chunk
                bool isLastChunk = totalBytesRead >= fileToUpload.FileSize;

                if (isLastChunk)
                {
                    // Last chunk: use Finish or Upload
                    await FinishUploadAsync(session, offset, commitInfo, buffer, length, contentHash);
                }
                else
                {
                    // Not last chunk: use Start or Append
                    session = await UploadChunkAsync(session, offset, buffer, length, contentHash);
                }

                offset += length;
            }

            _progress.ReportComplete(fileToUpload.RelativePath);
        }

        private async Task<UploadSessionStartResult> UploadChunkAsync(
            UploadSessionStartResult session,
            long offset,
            byte[] buffer,
            long length,
            string contentHash)
        {
            if (session == null)
            {
                return await _sessionManager.StartSessionAsync(buffer, length, contentHash);
            }
            else
            {
                await _sessionManager.AppendSessionAsync(session.SessionId, offset, buffer, length, contentHash);
                return session;
            }
        }

        private async Task FinishUploadAsync(
            UploadSessionStartResult session,
            long offset,
            CommitInfo commitInfo,
            byte[] buffer,
            long length,
            string contentHash)
        {
            if (session == null)
            {
                await _sessionManager.SimpleUploadAsync(commitInfo, buffer, length, contentHash);
            }
            else
            {
                await _sessionManager.FinishSessionAsync(session.SessionId, offset, commitInfo, buffer, length, contentHash);
            }
        }

        private CommitInfo CreateCommitInfo(FileToUpload fileToUpload)
        {
            return new CommitInfo(
                fileToUpload.RemotePath,
                WriteMode.Overwrite.Instance,
                autorename: false,
                clientModified: fileToUpload.ClientModified);
        }
    }
}
