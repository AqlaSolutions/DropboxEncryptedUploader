using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Service for persisting and retrieving upload session metadata.
/// Allows resuming interrupted uploads.
/// Uses synchronous I/O since the application is single-threaded.
///
/// DESIGN: Stores a single active upload session (not multiple concurrent sessions).
/// This matches the application's single-threaded, sequential upload design.
/// </summary>
public interface ISessionPersistenceService
{
    /// <summary>
    /// Loads the current session metadata.
    /// </summary>
    /// <returns>Session metadata if found, null otherwise</returns>
    UploadSessionMetadata LoadSession();

    /// <summary>
    /// Saves or updates the current session metadata.
    /// </summary>
    /// <param name="metadata">Session metadata to save</param>
    void SaveSession(UploadSessionMetadata metadata);

    /// <summary>
    /// Deletes the current session metadata (called when upload completes successfully).
    /// </summary>
    void DeleteSession();
}
