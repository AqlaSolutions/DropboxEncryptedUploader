using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dropbox.Api.Files;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Provides Dropbox operations for file management.
/// </summary>
public interface IDropboxService
{
    /// <summary>
    /// Creates a folder in Dropbox, ignoring errors if it already exists.
    /// </summary>
    Task CreateFolderAsync(string path);

    /// <summary>
    /// Lists all files and folders recursively in a Dropbox directory.
    /// Handles pagination internally.
    /// </summary>
    /// <param name="path">Dropbox directory path</param>
    /// <param name="includeDeleted">Whether to include deleted files</param>
    /// <returns>List of metadata entries</returns>
    Task<List<Metadata>> ListAllFilesAsync(string path, bool includeDeleted = false);

    /// <summary>
    /// Deletes a batch of files and polls until completion.
    /// </summary>
    /// <param name="paths">Collection of file paths to delete</param>
    Task DeleteBatchAsync(IEnumerable<string> paths);

    /// <summary>
    /// Lists revisions of a file.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="mode">Revision mode</param>
    /// <param name="limit">Maximum number of revisions to return</param>
    /// <returns>Revision list result</returns>
    Task<ListRevisionsResult> ListRevisionsAsync(string path, ListRevisionsMode mode, int limit);

    /// <summary>
    /// Restores a deleted file to a specific revision.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="rev">Revision ID to restore</param>
    /// <returns>Metadata of the restored file</returns>
    Task<FileMetadata> RestoreAsync(string path, string rev);

    /// <summary>
    /// Deletes a specific file revision.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="rev">Revision ID to delete</param>
    /// <returns>Delete result</returns>
    Task<DeleteResult> DeleteFileAsync(string path, string rev);

    // Upload session methods

    /// <summary>
    /// Starts a new upload session.
    /// </summary>
    /// <param name="body">Stream containing data to upload</param>
    /// <param name="contentHash">Optional content hash for verification</param>
    Task<UploadSessionStartResult> UploadSessionStartAsync(Stream body, string contentHash = null);

    /// <summary>
    /// Appends data to an existing upload session.
    /// </summary>
    /// <param name="cursor">Session cursor with session ID and offset</param>
    /// <param name="body">Stream containing data to append</param>
    /// <param name="contentHash">Optional content hash for verification</param>
    Task UploadSessionAppendV2Async(UploadSessionCursor cursor, Stream body, string contentHash = null);

    /// <summary>
    /// Finishes an upload session and commits the file.
    /// </summary>
    /// <param name="cursor">Session cursor with session ID and offset</param>
    /// <param name="commit">Commit info with path and metadata</param>
    /// <param name="body">Stream containing final data to upload</param>
    /// <param name="contentHash">Optional content hash for verification</param>
    Task<FileMetadata> UploadSessionFinishAsync(UploadSessionCursor cursor, CommitInfo commit, Stream body, string contentHash = null);

    /// <summary>
    /// Uploads a file in a single request.
    /// </summary>
    /// <param name="path">Destination path</param>
    /// <param name="mode">Write mode</param>
    /// <param name="autorename">Whether to autorename on conflict</param>
    /// <param name="clientModified">Client modification timestamp</param>
    /// <param name="body">Stream containing file data</param>
    /// <param name="contentHash">Optional content hash for verification</param>
    Task<FileMetadata> UploadAsync(string path, WriteMode mode, bool autorename, System.DateTime? clientModified, Stream body, string contentHash = null);
}