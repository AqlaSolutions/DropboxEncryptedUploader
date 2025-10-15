using System.Threading.Tasks;
using Dropbox.Api.Files;

namespace DropboxEncrypedUploader.Upload
{
    /// <summary>
    /// Manages Dropbox upload sessions with retry logic.
    /// </summary>
    public interface IUploadSessionManager
    {
        /// <summary>
        /// Starts a new upload session.
        /// </summary>
        Task<UploadSessionStartResult> StartSessionAsync(byte[] buffer, long length, string contentHash);

        /// <summary>
        /// Appends data to an existing upload session.
        /// </summary>
        Task AppendSessionAsync(string sessionId, long offset, byte[] buffer, long length, string contentHash);

        /// <summary>
        /// Finishes an upload session.
        /// </summary>
        Task FinishSessionAsync(string sessionId, long offset, CommitInfo commitInfo, byte[] buffer, long length, string contentHash);

        /// <summary>
        /// Uploads a file in a single request (for small files or single chunks).
        /// </summary>
        Task SimpleUploadAsync(CommitInfo commitInfo, byte[] buffer, long length, string contentHash);
    }
}
