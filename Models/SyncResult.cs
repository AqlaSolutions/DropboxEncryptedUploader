using System;
using System.Collections.Generic;

namespace DropboxEncrypedUploader.Models
{
    /// <summary>
    /// Contains the results of synchronization analysis between local and remote directories.
    /// </summary>
    public class SyncResult
    {
        /// <summary>
        /// Files that need to be uploaded (new or modified).
        /// </summary>
        public List<FileToUpload> FilesToUpload { get; }

        /// <summary>
        /// Remote file paths that need to be deleted (no longer exist locally).
        /// </summary>
        public HashSet<string> FilesToDelete { get; }

        /// <summary>
        /// All existing files in the remote directory (case-insensitive, lowercase paths).
        /// </summary>
        public HashSet<string> ExistingFiles { get; }

        /// <summary>
        /// All existing folders in the remote directory (case-insensitive, lowercase paths).
        /// Includes empty string "" to represent the root folder.
        /// </summary>
        public HashSet<string> ExistingFolders { get; }

        public SyncResult(
            List<FileToUpload> filesToUpload,
            HashSet<string> filesToDelete,
            HashSet<string> existingFiles,
            HashSet<string> existingFolders)
        {
            FilesToUpload = filesToUpload ?? throw new ArgumentNullException(nameof(filesToUpload));
            FilesToDelete = filesToDelete ?? throw new ArgumentNullException(nameof(filesToDelete));
            ExistingFiles = existingFiles ?? throw new ArgumentNullException(nameof(existingFiles));
            ExistingFolders = existingFolders ?? throw new ArgumentNullException(nameof(existingFolders));
        }
    }
}
