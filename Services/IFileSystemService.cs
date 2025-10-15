using System;
using System.Collections.Generic;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Provides filesystem operations for reading local files and directories.
/// </summary>
public interface IFileSystemService
{
    /// <summary>
    /// Checks if a directory exists.
    /// </summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// Gets all files in a directory recursively.
    /// </summary>
    /// <param name="path">Directory path</param>
    /// <returns>List of full file paths</returns>
    IEnumerable<string> GetAllFiles(string path);

    /// <summary>
    /// Gets file information (size and modification time).
    /// </summary>
    /// <param name="path">Full file path</param>
    /// <returns>Tuple of (fileSize, lastWriteTimeUtc)</returns>
    (long fileSize, DateTime lastWriteTimeUtc) GetFileInfo(string path);

    /// <summary>
    /// Gets the relative path of a file from a base directory.
    /// </summary>
    /// <param name="fullPath">Full path to the file</param>
    /// <param name="basePath">Base directory path (must end with directory separator)</param>
    /// <returns>Relative path from base directory</returns>
    string GetRelativePath(string fullPath, string basePath);
}