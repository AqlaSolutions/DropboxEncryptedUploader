using System;

namespace DropboxEncrypedUploader.Models;

/// <summary>
/// Represents a local file that needs to be uploaded to Dropbox.
/// </summary>
/// <param name="RelativePath">Relative path from the local directory root (using local path separators).</param>
/// <param name="FullPath">Full absolute path to the file on the local filesystem.</param>
/// <param name="RemotePath">Remote path where the file will be uploaded (pre-calculated, using forward slashes).</param>
/// <param name="FileSize">Size of the file in bytes.</param>
/// <param name="ClientModified">Client-side modification timestamp (UTC).</param>
public record FileToUpload(string RelativePath, string FullPath, string RemotePath, long FileSize, DateTime ClientModified);
