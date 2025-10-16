using System;

namespace DropboxEncrypedUploader.Models;

/// <summary>
/// Represents persistent metadata for an in-progress upload session.
/// Used to resume uploads that were interrupted.
/// </summary>
/// <param name="SessionId">Dropbox upload session ID</param>
/// <param name="FilePath">Full path to the file being uploaded</param>
/// <param name="ClientModified">Client-side modification timestamp (UTC)</param>
/// <param name="TotalSize">Total size of the file in bytes</param>
/// <param name="CurrentOffset">Number of bytes already uploaded to Dropbox</param>
/// <param name="EncryptionSalt">AES encryption salt extracted from ZIP stream (null for direct uploads)</param>
/// <param name="ContentHash">Cumulative SHA256 hash of all uploaded chunks for verification</param>
public record UploadSessionMetadata(
    string SessionId,
    string FilePath,
    DateTime ClientModified,
    long TotalSize,
    long CurrentOffset,
    byte[] EncryptionSalt,
    string ContentHash
);
