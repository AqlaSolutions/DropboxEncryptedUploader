using System;
using System.IO;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;

namespace DropboxEncrypedUploader.Upload
{
    /// <summary>
    /// Implementation of upload session manager with retry logic.
    /// </summary>
    public class UploadSessionManager : IUploadSessionManager
    {
        private readonly DropboxClient _client;
        private readonly int _maxRetries;

        public UploadSessionManager(DropboxClient client, int maxRetries)
        {
            _client = client;
            _maxRetries = maxRetries;
        }

        public async Task<UploadSessionStartResult> StartSessionAsync(byte[] buffer, long length, string contentHash)
        {
            return await RetryUploadAsync(stream =>
                _client.Files.UploadSessionStartAsync(contentHash: contentHash, body: stream),
                buffer, length);
        }

        public async Task AppendSessionAsync(string sessionId, long offset, byte[] buffer, long length, string contentHash)
        {
            await RetryUploadAsync(stream =>
                _client.Files.UploadSessionAppendV2Async(
                    new UploadSessionCursor(sessionId, (ulong)offset),
                    contentHash: contentHash,
                    body: stream),
                buffer, length);
        }

        public async Task FinishSessionAsync(string sessionId, long offset, CommitInfo commitInfo, byte[] buffer, long length, string contentHash)
        {
            await RetryUploadAsync(stream =>
                _client.Files.UploadSessionFinishAsync(
                    new UploadSessionCursor(sessionId, (ulong)offset),
                    commitInfo,
                    contentHash: contentHash,
                    body: stream),
                buffer, length);
        }

        public async Task SimpleUploadAsync(CommitInfo commitInfo, byte[] buffer, long length, string contentHash)
        {
            await RetryUploadAsync(stream =>
                _client.Files.UploadAsync(
                    commitInfo.Path,
                    commitInfo.Mode,
                    commitInfo.Autorename,
                    commitInfo.ClientModified,
                    contentHash: contentHash,
                    body: stream),
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
                catch (TaskCanceledException) when (retry < _maxRetries)
                {
                    // Timeout occurred, will retry with fresh stream
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

        /// <summary>
        /// Computes Dropbox content hash for a buffer.
        /// </summary>
        public static string ComputeContentHash(byte[] buffer, int length)
        {
            using var hasher = new DropboxContentHasher();
            hasher.TransformBlock(buffer, 0, length, buffer, 0);
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return DropboxContentHasher.ToHex(hasher.Hash);
        }
    }
}
