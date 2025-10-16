using System;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;
using DropboxEncrypedUploader.Services;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Base class for upload strategies containing common functionality.
/// Manages upload session state and lifecycle internally.
///
/// STATE MANAGEMENT:
/// - Strategy instances maintain mutable state during individual file uploads
/// - PrepareUpload() clears all state at the start of each file upload
/// - Safe to reuse for sequential uploads of multiple files
/// - NOT thread-safe - do not use concurrently
///
/// ERROR HANDLING:
/// - State is cleared when PrepareUpload() is called (at start of each upload)
/// - Exceptions during upload may leave internal state inconsistent
/// - Application retry logic reopens files and calls upload methods again
/// - PrepareUpload() at start of retry ensures clean state
/// </summary>
public abstract class BaseUploadStrategy
{
    protected readonly IUploadSessionManager SessionManager;
    protected readonly ISessionPersistenceService SessionPersistence;
    protected readonly IProgressReporter Progress;

    private FileToUpload _currentFile;
    private UploadSessionMetadata _resumeSession;
    private UploadSessionStartResult _activeSession;
    private long _uploadOffset;         // Bytes uploaded to Dropbox (for AppendSessionAsync parameter)
    private long _localOffset;          // Bytes processed locally from start of file (always starts at 0)
    private long _resumeOffset;         // Resume point from saved session (skip upload until this offset)
    private byte[] _cumulativeHashBytes;
    private bool _hashVerified;         // Track if we verified hash at resume point

    protected BaseUploadStrategy(
        IUploadSessionManager sessionManager,
        ISessionPersistenceService sessionPersistence,
        IProgressReporter progress)
    {
        SessionManager = sessionManager;
        SessionPersistence = sessionPersistence;
        Progress = progress;
    }

    /// <summary>
    /// Prepares for file upload by clearing previous state and loading saved session if available.
    /// Safe to call multiple times (e.g., when retrying after resume failure).
    /// </summary>
    /// <param name="file">File to upload</param>
    protected void PrepareUpload(FileToUpload file)
    {
        // Clear all state from previous upload
        _currentFile = null;
        _resumeSession = null;
        _activeSession = null;
        _uploadOffset = 0;
        _localOffset = 0;
        _resumeOffset = 0;
        _cumulativeHashBytes = null;
        _hashVerified = false;

        // Initialize for this file
        _currentFile = file;
        _resumeSession = LoadAndValidateSession(file);
        _activeSession = _resumeSession != null ? new UploadSessionStartResult(_resumeSession.SessionId) : null;
        _uploadOffset = _resumeSession?.CurrentOffset ?? 0;
        _localOffset = 0;  // Always start from 0 to recompute hash for verification
        _resumeOffset = _resumeSession?.CurrentOffset ?? 0;

        // Always initialize hash to zeros - we recompute from scratch to verify determinism
        _cumulativeHashBytes = new byte[32]; // SHA256 produces 32 bytes

        if (_resumeSession != null)
        {
            Progress.ReportMessage($"Resuming upload from offset {_resumeOffset} for {file.RelativePath}");
        }
    }

    /// <summary>
    /// Gets the encryption salt from a resumed session (for encrypted uploads only).
    /// </summary>
    /// <returns>16-byte salt array, or null if not resuming or no salt saved</returns>
    protected byte[] GetResumedEncryptionSalt()
    {
        return _resumeSession?.EncryptionSalt;
    }

    /// <summary>
    /// Uploads a chunk of data. Automatically manages session state and saves progress.
    /// Handles resume by skipping chunks before resumeOffset and verifying hash.
    /// Computes cumulative hash of all processed data for verification.
    /// </summary>
    /// <param name="buffer">Buffer containing chunk data</param>
    /// <param name="length">Length of data in buffer</param>
    /// <param name="encryptionSalt">Encryption salt to save (only for first chunk of encrypted uploads)</param>
    protected async Task UploadChunkAsync(byte[] buffer, long length, byte[] encryptionSalt = null)
    {
        // 1. Always update cumulative hash (for verification)
        UpdateCumulativeHash(buffer, length);
        _localOffset += length;

        // 2. Verify hash when we reach/pass resume point (once only)
        if (!_hashVerified && _localOffset >= _resumeOffset && _resumeSession != null && _resumeOffset > 0)
        {
            var computedHash = BytesToHexString(_cumulativeHashBytes);
            if (computedHash != _resumeSession.ContentHash)
            {
                // Hash mismatch - re-encryption produced different result
                SessionPersistence.DeleteSession();
                _resumeSession = null;
                _activeSession = null;
                throw new ResumeFailedException("Hash verification failed - file may have changed or encryption is non-deterministic");
            }
            _hashVerified = true; // Only verify once
            Progress.ReportMessage($"Verification successful, continuing upload from offset {_resumeOffset}");
        }

        // 3. Skip upload if we're still before resume point
        if (_localOffset <= _resumeOffset)
        {
            return;
        }

        try
        {
            // 4. Upload this chunk
            if (_activeSession == null)
            {
                _activeSession = await SessionManager.StartSessionAsync(buffer, length);
            }
            else
            {
                await SessionManager.AppendSessionAsync(_activeSession.SessionId, _uploadOffset, buffer, length);
            }

            _uploadOffset += length;

            // 5. Save progress using _localOffset (total processed)
            var cumulativeHashHex = BytesToHexString(_cumulativeHashBytes);
            SaveSessionProgress(_currentFile, _activeSession.SessionId, _localOffset,
                encryptionSalt ?? _resumeSession?.EncryptionSalt, cumulativeHashHex);
        }
        catch (ApiException<UploadSessionLookupError> ex) when (_resumeSession != null && ex.ErrorResponse.IsNotFound)
        {
            // Session doesn't exist on Dropbox (expired or not found)
            // Clear local session metadata and let retry start fresh
            SessionPersistence.DeleteSession();
            _resumeSession = null;
            _activeSession = null;
            throw new ResumeFailedException("Upload session not found on Dropbox - may have expired", ex);
        }
    }

    /// <summary>
    /// Updates cumulative hash for resume validation.
    /// Uses chain hashing (Hash(prevHash || chunk)) for internal validation only.
    /// Different from Dropbox's content hash format.
    /// </summary>
    private void UpdateCumulativeHash(byte[] buffer, long length)
    {
        using (var hasher = System.Security.Cryptography.SHA256.Create())
        {
            hasher.TransformBlock(_cumulativeHashBytes, 0, _cumulativeHashBytes.Length, null, 0);
            hasher.TransformFinalBlock(buffer, 0, (int)length);
            _cumulativeHashBytes = hasher.Hash;
        }
    }

    /// <summary>
    /// Finishes the upload and cleans up session state.
    /// </summary>
    protected async Task FinishUploadAsync(CommitInfo commitInfo, byte[] buffer, long length)
    {
        if (_activeSession == null)
        {
            await SessionManager.SimpleUploadAsync(commitInfo, buffer, length);
        }
        else
        {
            await SessionManager.FinishSessionAsync(_activeSession.SessionId, _uploadOffset, commitInfo, buffer, length);
        }

        SessionPersistence.DeleteSession();

        // Clear all state
        _currentFile = null;
        _resumeSession = null;
        _activeSession = null;
        _uploadOffset = 0;
        _localOffset = 0;
        _resumeOffset = 0;
        _cumulativeHashBytes = null;
        _hashVerified = false;
    }

    /// <summary>
    /// Creates commit info for a file upload.
    /// </summary>
    protected CommitInfo CreateCommitInfo(FileToUpload fileToUpload)
    {
        return new CommitInfo(
            fileToUpload.RemotePath,
            WriteMode.Overwrite.Instance,
            autorename: false,
            clientModified: fileToUpload.ClientModified);
    }

    /// <summary>
    /// Loads and validates an existing session for a file.
    /// Returns null if no valid session exists (wrong size/date/file or missing).
    /// Deletes stale sessions that don't match current file metadata.
    /// </summary>
    private UploadSessionMetadata LoadAndValidateSession(FileToUpload fileToUpload)
    {
        var existingSession = SessionPersistence.LoadSession();

        if (existingSession == null)
        {
            return null;
        }

        // Validate that session matches the current file
        bool isValid = existingSession.FilePath == fileToUpload.FullPath &&
                       existingSession.TotalSize == fileToUpload.FileSize &&
                       existingSession.ClientModified == fileToUpload.ClientModified &&
                       existingSession.CurrentOffset >= 0 &&
                       !string.IsNullOrEmpty(existingSession.ContentHash);

        if (!isValid)
        {
            SessionPersistence.DeleteSession();
            return null;
        }

        return existingSession;
    }

    /// <summary>
    /// Saves session progress after uploading a chunk.
    /// </summary>
    private void SaveSessionProgress(FileToUpload fileToUpload, string sessionId, long currentOffset, byte[] encryptionSalt, string contentHash)
    {
        var metadata = new UploadSessionMetadata(
            sessionId,
            fileToUpload.FullPath,
            fileToUpload.ClientModified,
            fileToUpload.FileSize,
            currentOffset,
            encryptionSalt,
            contentHash);

        SessionPersistence.SaveSession(metadata);
    }

    /// <summary>
    /// Converts byte array to hexadecimal string (for .NET Framework compatibility).
    /// </summary>
    private static string BytesToHexString(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", "");
    }
}
