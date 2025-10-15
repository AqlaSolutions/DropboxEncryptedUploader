using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Implements synchronization analysis between local and remote directories.
/// </summary>
public class SyncService(
    IFileSystemService fileSystem,
    IDropboxService dropbox,
    IProgressReporter progress,
    Configuration.Configuration config)
    : ISyncService
{
    /// <summary>
    /// Gets all local files as a set of relative paths.
    /// </summary>
    public HashSet<string> GetLocalFiles()
    {
        bool localExists = fileSystem.DirectoryExists(config.LocalDirectory);
        if (!localExists)
            progress.ReportMessage("Local directory does not exist: " + config.LocalDirectory);

        return new HashSet<string>(
            localExists
                ? fileSystem.GetAllFiles(config.LocalDirectory)
                    .Select(f => fileSystem.GetRelativePath(f, config.LocalDirectory))
                : [],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets all remote files and folders from Dropbox.
    /// Returns existing files set, existing folders set, and a mapping of original filenames to remote metadata.
    /// </summary>
    public async Task<(HashSet<string> ExistingFiles, HashSet<string> ExistingFolders, Dictionary<string, FileMetadata> RemoteFilesByOriginalName)> GetRemoteFilesAsync()
    {
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remoteFilesByOriginalName = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);

        // Critical: Initialize with empty string for root folder
        existingFolders.Add("");

        // List all remote files
        foreach (var entry in await dropbox.ListAllFilesAsync(config.DropboxDirectory))
        {
            if (entry.IsFolder)
            {
                existingFolders.Add(entry.AsFolder.PathLower);
                continue;
            }

            if (!entry.IsFile)
                continue;

            existingFiles.Add(entry.PathLower);

            // Transform remote path to local filename
            if (TryTransformRemotePathToLocal(entry.PathLower, out string originalFileName))
            {
                remoteFilesByOriginalName[originalFileName] = entry.AsFile;
            }
        }

        return (existingFiles, existingFolders, remoteFilesByOriginalName);
    }

    /// <summary>
    /// Transforms a remote Dropbox path to a local filename.
    /// Strips the base directory, handles encryption extension (.zip), and converts path separators.
    /// </summary>
    /// <param name="remotePathLower">The remote path (lowercase) from Dropbox</param>
    /// <param name="localFileName">The transformed local filename, or null if transformation fails</param>
    /// <returns>True if transformation succeeded, false if file should be skipped</returns>
    private bool TryTransformRemotePathToLocal(string remotePathLower, out string localFileName)
    {
        var relativePath = remotePathLower.Substring(config.DropboxDirectory.Length);

        // Strip .zip extension if using encryption
        if (config.UseEncryption)
        {
            if (!relativePath.EndsWith(".zip"))
            {
                localFileName = null;
                return false;
            }
            relativePath = relativePath.Substring(0, relativePath.Length - 4);
        }

        // Convert forward slashes to platform-specific directory separator
        localFileName = relativePath.Replace("/", Path.DirectorySeparatorChar.ToString());
        return true;
    }

    /// <summary>
    /// Determines which files need to be uploaded and which need to be deleted.
    /// </summary>
    public (List<FileToUpload> FilesToUpload, HashSet<string> FilesToDelete) DetermineSync(
        HashSet<string> localFiles,
        Dictionary<string, FileMetadata> remoteFilesByOriginalName,
        bool localDirectoryExists)
    {
        var filesToUpload = new List<FileToUpload>();
        var filesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Create a mutable copy to track which files need uploading
        var filesToUploadSet = new HashSet<string>(localFiles, StringComparer.OrdinalIgnoreCase);

        // Check each remote file
        foreach (var kvp in remoteFilesByOriginalName)
        {
            var originalFileName = kvp.Key;
            var remoteFile = kvp.Value;

            if (localFiles.Contains(originalFileName))
            {
                // File exists both locally and remotely - check if modified
                var (fileSize, lastWriteTimeUtc) = fileSystem.GetFileInfo(
                    Path.Combine(config.LocalDirectory, originalFileName));

                // Skip upload if timestamps match within tolerance (1 second)
                if ((lastWriteTimeUtc - remoteFile.ClientModified).TotalSeconds < config.TimestampToleranceSeconds)
                    filesToUploadSet.Remove(originalFileName);
            }
            else if (localDirectoryExists)
            {
                // File exists remotely but not locally - mark for deletion
                filesToDelete.Add(remoteFile.PathLower);
            }
        }

        // Build list of files to upload (remaining local files)
        foreach (var relativePath in filesToUploadSet)
        {
            string fullPath = Path.Combine(config.LocalDirectory, relativePath);
            var (fileSize, lastWriteTimeUtc) = fileSystem.GetFileInfo(fullPath);

            string remotePath = Path.Combine(config.DropboxDirectory, relativePath.Replace("\\", "/"))
                                + config.RemoteFileExtension;

            filesToUpload.Add(new FileToUpload(
                relativePath,
                fullPath,
                remotePath,
                fileSize,
                lastWriteTimeUtc));
        }

        return (filesToUpload, filesToDelete);
    }
}
