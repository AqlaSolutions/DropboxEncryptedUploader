using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Services;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Implementation of upload session manager with retry logic.
/// </summary>
public class UploadSessionManager(IDropboxService dropboxService, int maxRetries) : IUploadSessionManager
{
    public async Task<UploadSessionStartResult> StartSessionAsync(byte[] buffer, long length)
    {
        string contentHash = DropboxContentHasher.ComputeHash(buffer, (int)length);
        return await RetryUploadAsync(stream =>
                dropboxService.UploadSessionStartAsync(body: stream, contentHash: contentHash),
            buffer, length);
    }

    public async Task AppendSessionAsync(string sessionId, long offset, byte[] buffer, long length)
    {
        string contentHash = DropboxContentHasher.ComputeHash(buffer, (int)length);
        await RetryUploadAsync(stream =>
                dropboxService.UploadSessionAppendV2Async(
                    new UploadSessionCursor(sessionId, (ulong)offset),
                    body: stream,
                    contentHash: contentHash),
            buffer, length);
    }

    public async Task<FileMetadata> FinishSessionAsync(string sessionId, long offset, CommitInfo commitInfo, byte[] buffer, long length)
    {
        string contentHash = DropboxContentHasher.ComputeHash(buffer, (int)length);
        return await RetryUploadAsync(stream =>
                dropboxService.UploadSessionFinishAsync(
                    new UploadSessionCursor(sessionId, (ulong)offset),
                    commitInfo,
                    body: stream,
                    contentHash: contentHash),
            buffer, length);
    }

    public async Task<FileMetadata> SimpleUploadAsync(CommitInfo commitInfo, byte[] buffer, long length)
    {
        string contentHash = length > 0 ? DropboxContentHasher.ComputeHash(buffer, (int)length) : null;
        return await RetryUploadAsync(stream =>
                dropboxService.UploadAsync(
                    commitInfo.Path,
                    commitInfo.Mode,
                    commitInfo.Autorename,
                    commitInfo.ClientModified,
                    body: stream,
                    contentHash: contentHash),
            buffer, length);
    }

    /// <summary>
    /// Executes an upload operation with retry logic for timeout exceptions.
    /// Recreates the MemoryStream on each retry to ensure a fresh stream state.
    /// </summary>
    private async Task<T> RetryUploadAsync<T>(Func<MemoryStream, Task<T>> uploadOperation, byte[] buffer, long length)
    {
        for (int retry = 0; ; retry++)
        {
            var bufferStream = new MemoryStream(buffer);
            bufferStream.Position = 0;
            bufferStream.SetLength(length);

            try
            {
                return await uploadOperation(bufferStream);
            }
            catch (TaskCanceledException) when (retry < maxRetries)
            {
                // Timeout occurred
            }
            catch (HttpRequestException) when (retry < maxRetries)
            {
                // Probably The remote name could not be resolved
                await Task.Delay(retry * 1000);
            }
        }
    }

    /// <summary>
    /// Executes an upload operation with retry logic for timeout exceptions (non-generic version).
    /// </summary>
    private async Task RetryUploadAsync(Func<MemoryStream, Task> uploadOperation, byte[] buffer, long length)
    {
        await RetryUploadAsync(async stream =>
        {
            await uploadOperation(stream);
            return 0; // Dummy return value
        }, buffer, length);
    }
}
