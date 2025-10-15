using System;
using System.Collections.Generic;

namespace DropboxEncrypedUploader.Models;

/// <summary>
/// Contains the results of synchronization analysis between local and remote directories.
/// </summary>
/// <param name="FilesToUpload">Files that need to be uploaded (new or modified).</param>
/// <param name="FilesToDelete">Remote file paths that need to be deleted (no longer exist locally).</param>
/// <param name="ExistingFiles">All existing files in the remote directory (case-insensitive, lowercase paths).</param>
/// <param name="ExistingFolders">All existing folders in the remote directory (case-insensitive, lowercase paths). Includes empty string "" to represent the root folder.</param>
public record SyncResult(
    List<FileToUpload> FilesToUpload,
    HashSet<string> FilesToDelete,
    HashSet<string> ExistingFiles,
    HashSet<string> ExistingFolders);
