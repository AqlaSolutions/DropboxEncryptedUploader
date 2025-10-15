using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services
{
    /// <summary>
    /// Implements synchronization analysis between local and remote directories.
    /// </summary>
    public class SyncService : ISyncService
    {
        private readonly IFileSystemService _fileSystem;
        private readonly IDropboxService _dropbox;
        private readonly IProgressReporter _progress;
        private readonly Configuration.Configuration _config;

        public SyncService(
            IFileSystemService fileSystem,
            IDropboxService dropbox,
            IProgressReporter progress,
            Configuration.Configuration config)
        {
            _fileSystem = fileSystem;
            _dropbox = dropbox;
            _progress = progress;
            _config = config;
        }

        public async Task<SyncResult> AnalyzeSyncAsync()
        {
            // Check if local directory exists
            bool localExists = _fileSystem.DirectoryExists(_config.LocalDirectory);
            if (!localExists)
                _progress.ReportMessage("Local directory does not exist: " + _config.LocalDirectory);

            // Get all local files
            var localFiles = new HashSet<string>(
                localExists
                    ? _fileSystem.GetAllFiles(_config.LocalDirectory)
                        .Select(f => _fileSystem.GetRelativePath(f, _config.LocalDirectory))
                    : Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            var existingFiles = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var existingFolders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var filesToDelete = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            // Critical: Initialize with empty string for root folder
            existingFolders.Add("");

            // List all remote files
            foreach (var entry in await _dropbox.ListAllFilesAsync(_config.DropboxDirectory))
            {
                if (entry.IsFolder)
                {
                    existingFolders.Add(entry.AsFolder.PathLower);
                    continue;
                }

                if (!entry.IsFile)
                    continue;

                existingFiles.Add(entry.PathLower);
                var relativePath = entry.PathLower.Substring(_config.DropboxDirectory.Length);

                // Strip .zip extension if using encryption
                string originalFileName;
                if (_config.UseEncryption)
                {
                    if (!relativePath.EndsWith(".zip"))
                        continue;
                    originalFileName = relativePath.Substring(0, relativePath.Length - 4)
                        .Replace("/", Path.DirectorySeparatorChar.ToString());
                }
                else
                {
                    originalFileName = relativePath.Replace("/", Path.DirectorySeparatorChar.ToString());
                }

                if (localFiles.Contains(originalFileName))
                {
                    // File exists both locally and remotely - check if modified
                    var (fileSize, lastWriteTimeUtc) = _fileSystem.GetFileInfo(
                        Path.Combine(_config.LocalDirectory, originalFileName));

                    // Skip upload if timestamps match within tolerance (1 second)
                    if ((lastWriteTimeUtc - entry.AsFile.ClientModified).TotalSeconds < _config.TimestampToleranceSeconds)
                        localFiles.Remove(originalFileName);
                }
                else if (localExists)
                {
                    // File exists remotely but not locally - mark for deletion
                    filesToDelete.Add(entry.PathLower);
                }
            }

            // Build list of files to upload
            var filesToUpload = new List<FileToUpload>();
            foreach (var relativePath in localFiles)
            {
                string fullPath = Path.Combine(_config.LocalDirectory, relativePath);
                var (fileSize, lastWriteTimeUtc) = _fileSystem.GetFileInfo(fullPath);

                string remotePath = Path.Combine(_config.DropboxDirectory, relativePath.Replace("\\", "/"))
                    + _config.RemoteFileExtension;

                filesToUpload.Add(new FileToUpload(
                    relativePath,
                    fullPath,
                    remotePath,
                    fileSize,
                    lastWriteTimeUtc));
            }

            return new SyncResult(filesToUpload, filesToDelete, existingFiles, existingFolders);
        }
    }
}
