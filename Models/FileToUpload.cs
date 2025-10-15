using System;

namespace DropboxEncrypedUploader.Models
{
    /// <summary>
    /// Represents a local file that needs to be uploaded to Dropbox.
    /// </summary>
    public class FileToUpload
    {
        /// <summary>
        /// Relative path from the local directory root (using local path separators).
        /// </summary>
        public string RelativePath { get; }

        /// <summary>
        /// Full absolute path to the file on the local filesystem.
        /// </summary>
        public string FullPath { get; }

        /// <summary>
        /// Remote path where the file will be uploaded (pre-calculated, using forward slashes).
        /// </summary>
        public string RemotePath { get; }

        /// <summary>
        /// Size of the file in bytes.
        /// </summary>
        public long FileSize { get; }

        /// <summary>
        /// Client-side modification timestamp (UTC).
        /// </summary>
        public DateTime ClientModified { get; }

        public FileToUpload(string relativePath, string fullPath, string remotePath, long fileSize, DateTime clientModified)
        {
            RelativePath = relativePath;
            FullPath = fullPath;
            RemotePath = remotePath;
            FileSize = fileSize;
            ClientModified = clientModified;
        }
    }
}
