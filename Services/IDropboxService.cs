using System.Collections.Generic;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;

namespace DropboxEncrypedUploader.Services
{
    /// <summary>
    /// Provides Dropbox operations for file management.
    /// </summary>
    public interface IDropboxService
    {
        /// <summary>
        /// Gets the underlying DropboxClient for upload operations.
        /// </summary>
        DropboxClient Client { get; }

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
    }
}
