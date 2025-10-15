using System.Threading.Tasks;
using Dropbox.Api.Files;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Manages Dropbox upload sessions with retry logic.
/// </summary>
public interface IUploadSessionManager
{
    /// <summary>
    /// Starts a new upload session.
    /// </summary>
    Task<UploadSessionStartResult> StartSessionAsync(byte[] buffer, long length);

    /// <summary>
    /// Appends data to an existing upload session.
    /// </summary>
    Task AppendSessionAsync(string sessionId, long offset, byte[] buffer, long length);

    /// <summary>
    /// Finishes an upload session.
    /// </summary>
    Task<FileMetadata> FinishSessionAsync(string sessionId, long offset, CommitInfo commitInfo, byte[] buffer, long length);

    /// <summary>
    /// Uploads a file in a single request (for small files or single chunks).
    /// </summary>
    Task<FileMetadata> SimpleUploadAsync(CommitInfo commitInfo, byte[] buffer, long length);
}