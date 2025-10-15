using System;
using System.Collections.Generic;
using System.IO;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// Implementation of filesystem operations.
/// </summary>
public class FileSystemService : IFileSystemService
{
    public bool DirectoryExists(string path)
    {
        return Directory.Exists(path);
    }

    public IEnumerable<string> GetAllFiles(string path)
    {
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories);
    }

    public (long fileSize, DateTime lastWriteTimeUtc) GetFileInfo(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    public string GetRelativePath(string fullPath, string basePath)
    {
        if (!fullPath.StartsWith(basePath))
            throw new ArgumentException($"Full path '{fullPath}' does not start with base path '{basePath}'");

        return fullPath.Substring(basePath.Length);
    }
}