using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;
using DropboxEncrypedUploader.Services;
using DropboxEncrypedUploader.Upload;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace DropboxEncrypedUploader.Tests;

/// <summary>
/// Tests for BaseUploadStrategy - hash chain, validation, and session management.
/// </summary>
[TestClass]
public class BaseUploadStrategyTests
{
    // Test implementation of abstract BaseUploadStrategy
    private class TestUploadStrategy : BaseUploadStrategy
    {
        public TestUploadStrategy(IUploadSessionManager sessionManager, ISessionPersistenceService sessionPersistence, IProgressReporter progress)
            : base(sessionManager, sessionPersistence, progress)
        {
        }

        // Expose protected methods for testing
        public new void PrepareUpload(FileToUpload file)
            => base.PrepareUpload(file);

        public new byte[] GetResumedEncryptionSalt()
            => base.GetResumedEncryptionSalt();

        public new Task UploadChunkAsync(byte[] buffer, long length, byte[] encryptionSalt = null)
            => base.UploadChunkAsync(buffer, length, encryptionSalt);

        public new Task FinishUploadAsync(CommitInfo commitInfo, byte[] buffer, long length)
            => base.FinishUploadAsync(commitInfo, buffer, length);

        public new CommitInfo CreateCommitInfo(FileToUpload fileToUpload)
            => base.CreateCommitInfo(fileToUpload);
    }

    private Mock<IUploadSessionManager> _mockSessionManager;
    private Mock<ISessionPersistenceService> _mockSessionPersistence;
    private Mock<IProgressReporter> _mockProgress;
    private TestUploadStrategy _strategy;

    [TestInitialize]
    public void Setup()
    {
        _mockSessionManager = new Mock<IUploadSessionManager>();
        _mockSessionPersistence = new Mock<ISessionPersistenceService>();
        _mockProgress = new Mock<IProgressReporter>();
        _strategy = new TestUploadStrategy(_mockSessionManager.Object, _mockSessionPersistence.Object, _mockProgress.Object);
    }

    [TestMethod]
    public void PrepareUpload_NoExistingSession_InitializesFresh()
    {
        // Arrange
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, DateTime.UtcNow);
        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns((UploadSessionMetadata)null);

        // Act
        _strategy.PrepareUpload(file);

        // Assert - No report message for fresh start
        _mockProgress.Verify(p => p.ReportMessage(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public void PrepareUpload_ValidSession_Reports()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);

        var session = new UploadSessionMetadata("session-123", @"C:\test.txt", now, 1000, 500, null, "A1B2C3D4E5F6789012345678901234567890123456789012345678901234ABCD");
        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);

        // Act
        _strategy.PrepareUpload(file);

        // Assert
        _mockProgress.Verify(p => p.ReportMessage(It.Is<string>(m => m.Contains("Resuming upload"))), Times.Once);
    }

    [DataTestMethod]
    [DataRow(2000, 1000, 500, DisplayName = "Wrong file size")]
    [DataRow(1000, 1000, -100, DisplayName = "Negative offset")]
    public void PrepareUpload_InvalidSessionMetadata_InvalidatesSession(long actualSize, long sessionSize, long offset)
    {
        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", actualSize, now);
        var session = new UploadSessionMetadata("session-123", @"C:\test.txt", now, sessionSize, offset, null, "A1B2C3D4E5F6789012345678901234567890123456789012345678901234ABCD");

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);

        // Act
        _strategy.PrepareUpload(file);

        // Assert
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Once, "Should delete invalid session");
        _mockProgress.Verify(p => p.ReportMessage(It.IsAny<string>()), Times.Never, "Should not report resume for invalid session");
    }

    [TestMethod]
    public void PrepareUpload_WrongModificationTime_InvalidatesSession()
    {
        // Arrange
        var time1 = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var time2 = new DateTime(2025, 1, 2, 10, 0, 0, DateTimeKind.Utc);

        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, time2);
        var session = new UploadSessionMetadata("session-123", @"C:\test.txt", time1, 1000, 500, null, "A1B2C3D4E5F6789012345678901234567890123456789012345678901234ABCD");

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);

        // Act
        _strategy.PrepareUpload(file);

        // Assert
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Once);
    }

    [TestMethod]
    public void PrepareUpload_MissingContentHash_InvalidatesSession()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);

        // Session with null ContentHash
        var session = new UploadSessionMetadata("session-123", @"C:\test.txt", now, 1000, 500, null, null);

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);

        // Act
        _strategy.PrepareUpload(file);

        // Assert
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Once, "Should delete session with missing hash");
    }

    [TestMethod]
    public void PrepareUpload_EmptyContentHash_InvalidatesSession()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);

        // Session with empty ContentHash
        var session = new UploadSessionMetadata("session-123", @"C:\test.txt", now, 1000, 500, null, "");

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);

        // Act
        _strategy.PrepareUpload(file);

        // Assert
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Once, "Should delete session with empty hash");
    }

    [TestMethod]
    public void GetResumedEncryptionSalt_HasSalt_ReturnsSalt()
    {
        // Arrange
        var salt = new byte[Configuration.Configuration.AES_SALT_SIZE];
        for (int i = 0; i < Configuration.Configuration.AES_SALT_SIZE; i++) salt[i] = (byte)i;

        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);
        var session = new UploadSessionMetadata("session-123", @"C:\test.txt", now, 1000, 500, salt, "A1B2C3D4E5F6789012345678901234567890123456789012345678901234ABCD");

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);

        // Act
        _strategy.PrepareUpload(file);
        var retrievedSalt = _strategy.GetResumedEncryptionSalt();

        // Assert
        Assert.IsNotNull(retrievedSalt);
        CollectionAssert.AreEqual(salt, retrievedSalt);
    }

    [TestMethod]
    public void GetResumedEncryptionSalt_NoSalt_ReturnsNull()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);
        var session = new UploadSessionMetadata("session-123", @"C:\test.txt", now, 1000, 500, null, "A1B2C3D4E5F6789012345678901234567890123456789012345678901234ABCD");

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);

        // Act
        _strategy.PrepareUpload(file);
        var salt = _strategy.GetResumedEncryptionSalt();

        // Assert
        Assert.IsNull(salt, "Should return null for direct uploads");
    }

    [TestMethod]
    public async Task UploadChunkAsync_FirstChunk_StartsSession()
    {
        // Arrange
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, DateTime.UtcNow);
        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns((UploadSessionMetadata)null);

        _mockSessionManager.Setup(s => s.StartSessionAsync(It.IsAny<byte[]>(), It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        _strategy.PrepareUpload(file);

        var buffer = new byte[100];

        // Act
        await _strategy.UploadChunkAsync(buffer, 100);

        // Assert
        _mockSessionManager.Verify(s => s.StartSessionAsync(buffer, 100), Times.Once);
        _mockSessionPersistence.Verify(s => s.SaveSession(
            It.Is<UploadSessionMetadata>(m =>
                m.SessionId == "session-123" &&
                m.CurrentOffset == 100)), Times.Once);
    }

    [TestMethod]
    public async Task UploadChunkAsync_SecondChunk_AppendsSession()
    {
        // Arrange
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, DateTime.UtcNow);
        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns((UploadSessionMetadata)null);

        _mockSessionManager.Setup(s => s.StartSessionAsync(It.IsAny<byte[]>(), It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        _strategy.PrepareUpload(file);

        var buffer1 = new byte[100];
        var buffer2 = new byte[100];

        // Act
        await _strategy.UploadChunkAsync(buffer1, 100);
        await _strategy.UploadChunkAsync(buffer2, 100);

        // Assert
        _mockSessionManager.Verify(s => s.StartSessionAsync(It.IsAny<byte[]>(), It.IsAny<long>()), Times.Once);
        _mockSessionManager.Verify(s => s.AppendSessionAsync("session-123", 100, buffer2, 100), Times.Once);
    }

    [TestMethod]
    public async Task UploadChunkAsync_ComputesChainHash()
    {
        // Test that cumulative hash is computed correctly using chain hashing

        // Arrange
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, DateTime.UtcNow);
        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns((UploadSessionMetadata)null);

        _mockSessionManager.Setup(s => s.StartSessionAsync(It.IsAny<byte[]>(), It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        _strategy.PrepareUpload(file);

        var savedHashes = new List<string>();
        _mockSessionPersistence.Setup(s => s.SaveSession(It.IsAny<UploadSessionMetadata>()))
            .Callback<UploadSessionMetadata>(metadata => savedHashes.Add(metadata.ContentHash));

        var chunk1 = new byte[] { 1, 2, 3 };
        var chunk2 = new byte[] { 4, 5, 6 };

        // Act
        await _strategy.UploadChunkAsync(chunk1, 3);
        await _strategy.UploadChunkAsync(chunk2, 3);

        // Assert
        Assert.AreEqual(2, savedHashes.Count);

        // Hashes should be different (chain hash)
        Assert.AreNotEqual(savedHashes[0], savedHashes[1]);

        // Verify hash length (SHA256 = 64 hex characters)
        Assert.AreEqual(64, savedHashes[0].Length);
        Assert.AreEqual(64, savedHashes[1].Length);
    }

    [TestMethod]
    public async Task UploadChunkAsync_WithEncryptionSalt_SavesSalt()
    {
        // Arrange
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, DateTime.UtcNow);
        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns((UploadSessionMetadata)null);

        _mockSessionManager.Setup(s => s.StartSessionAsync(It.IsAny<byte[]>(), It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        _strategy.PrepareUpload(file);

        var buffer = new byte[100];
        var salt = new byte[Configuration.Configuration.AES_SALT_SIZE];
        for (int i = 0; i < Configuration.Configuration.AES_SALT_SIZE; i++) salt[i] = (byte)i;

        // Act
        await _strategy.UploadChunkAsync(buffer, 100, salt);

        // Assert
        _mockSessionPersistence.Verify(s => s.SaveSession(
            It.Is<UploadSessionMetadata>(m =>
                m.EncryptionSalt != null &&
                m.EncryptionSalt.Length == Configuration.Configuration.AES_SALT_SIZE)), Times.Once);
    }

    [TestMethod]
    public async Task UploadChunkAsync_HashMismatchDuringResume_ClearsSession()
    {
        // Test that hash verification failure clears the session

        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);

        // Resume at offset 50 with a specific hash that won't match our test data
        // _localOffset starts at 0, so after first chunk (50 bytes), it reaches offset 50 and verifies hash
        var session = new UploadSessionMetadata("session-123", @"C:\test.txt", now, 1000, 50, null, "A1B2C3D4E5F6789012345678901234567890123456789012345678901234ABCD");

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);
        _strategy.PrepareUpload(file);

        // First chunk: reaches resume offset (50), verifies hash, and fails because hash doesn't match
        var buffer1 = new byte[50];

        // Act & Assert
        await Assert.ThrowsExceptionAsync<ResumeFailedException>(() => _strategy.UploadChunkAsync(buffer1, 50));

        // Should clear the session when hash verification fails
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Once);
    }

    [TestMethod]
    public async Task UploadChunkAsync_NetworkErrorDuringUpload_DoesNotClearSession()
    {
        // Test that network errors during uploads don't clear the session
        // (allowing retry to resume from saved progress)
        // Session will naturally expire after 5 days if not resumed

        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);

        // Start fresh (no resume)
        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns((UploadSessionMetadata)null);

        _mockSessionManager.Setup(s => s.StartSessionAsync(It.IsAny<byte[]>(), It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        _mockSessionManager.Setup(s => s.AppendSessionAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<long>()))
            .ThrowsAsync(new Exception("Network error"));

        _strategy.PrepareUpload(file);

        // First chunk succeeds (starts session)
        await _strategy.UploadChunkAsync(new byte[100], 100);

        // Second chunk fails with network error
        // Act & Assert
        await Assert.ThrowsExceptionAsync<Exception>(() => _strategy.UploadChunkAsync(new byte[100], 100));

        // Session should NOT be cleared for network errors - only verification failures clear it
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Never);
    }

    [TestMethod]
    public void CreateCommitInfo_CreatesCorrectStructure()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt.zip", 1000, now);

        // Act
        var commitInfo = _strategy.CreateCommitInfo(file);

        // Assert
        Assert.AreEqual("/dropbox/test.txt.zip", commitInfo.Path);
        Assert.IsTrue(commitInfo.Mode.IsOverwrite);
        Assert.IsFalse(commitInfo.Autorename);
        Assert.AreEqual(now, commitInfo.ClientModified);
    }

    [TestMethod]
    public async Task FinishUploadAsync_ClearsAllState()
    {
        // Arrange
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, DateTime.UtcNow);
        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns((UploadSessionMetadata)null);

        _mockSessionManager.Setup(s => s.StartSessionAsync(It.IsAny<byte[]>(), It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));
        _mockSessionManager.Setup(s => s.FinishSessionAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CommitInfo>(), It.IsAny<byte[]>(), It.IsAny<long>()))
            .ReturnsAsync(new FileMetadata());

        _strategy.PrepareUpload(file);

        await _strategy.UploadChunkAsync(new byte[100], 100);

        var commitInfo = _strategy.CreateCommitInfo(file);

        // Act
        await _strategy.FinishUploadAsync(commitInfo, new byte[50], 50);

        // Assert
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Once, "Should delete session after completion");
    }

    [TestMethod]
    public async Task UploadChunkAsync_SessionNotFoundDuringResume_ClearsSession()
    {
        // Test that when Dropbox reports session not found during resume, we clear local session
        // Uses reflection to create ApiException<UploadSessionLookupError> with sealed internal constructor

        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);

        // Compute expected hash for 100 bytes
        byte[] testData = new byte[100];
        byte[] initialHash = new byte[32];
        string expectedHash;
        using (var hasher = System.Security.Cryptography.SHA256.Create())
        {
            hasher.TransformBlock(initialHash, 0, 32, null, 0);
            hasher.TransformFinalBlock(testData, 0, 100);
            expectedHash = BitConverter.ToString(hasher.Hash).Replace("-", "");
        }

        // Resume from offset 100 with valid session
        var session = new UploadSessionMetadata(
            "session-123",
            @"C:\test.txt",
            now,
            1000,
            100,
            null,
            expectedHash);

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);

        // Create ApiException<UploadSessionLookupError> with IsNotFound = true using reflection
        // Constructor signature: internal ApiException(string requestId)
        var lookupError = UploadSessionLookupError.NotFound.Instance;
        var apiExceptionType = typeof(ApiException<UploadSessionLookupError>);

        // Get the internal constructor that takes only requestId
        var constructor = apiExceptionType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(string)],
            null);

        if (constructor == null)
        {
            Assert.Fail("Could not find ApiException<UploadSessionLookupError> constructor");
        }

        // Create the exception with requestId
        var exception = (ApiException<UploadSessionLookupError>)constructor.Invoke(["test-request-id"]);

        // Set the ErrorResponse property via backing field (property has private setter or is set during deserialization)
        // Auto-implemented properties use backing field named <PropertyName>k__BackingField
        var backingField = apiExceptionType.BaseType.GetField("<ErrorResponse>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (backingField == null)
        {
            // Try without angle brackets (older .NET versions)
            backingField = apiExceptionType.BaseType.GetField("ErrorResponse", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        if (backingField == null)
        {
            Assert.Fail("Could not find ErrorResponse backing field");
        }

        backingField.SetValue(exception, lookupError);

        // Setup mock to throw the exception on second chunk (after resume point)
        _mockSessionManager.Setup(s => s.AppendSessionAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<long>()))
            .ThrowsAsync(exception);

        _strategy.PrepareUpload(file);

        // First chunk: skipped (hash verified, at resume offset)
        await _strategy.UploadChunkAsync(new byte[100], 100);

        // Second chunk: triggers append which throws session not found
        // Act & Assert
        var ex = await Assert.ThrowsExceptionAsync<ResumeFailedException>(() =>
            _strategy.UploadChunkAsync(new byte[100], 100));

        // Verify session was cleared
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Once,
            "Session should be cleared when Dropbox reports session not found");

        // Verify exception message
        Assert.IsTrue(ex.Message.Contains("not found") || ex.Message.Contains("expired"),
            "Exception message should indicate session not found or expired");
    }

    [TestMethod]
    public async Task UploadChunkAsync_MisalignedChunkBoundaries_FailsHashVerification()
    {
        // Test that resume fails when chunk boundaries don't align with saved offset.
        // This is a critical edge case: if the resume offset is 150 bytes but chunks
        // are 100 bytes each, the hash at offset 200 won't match the saved hash at 150.

        // Arrange
        var now = DateTime.UtcNow;
        var file = new FileToUpload("test.txt", @"C:\test.txt", "/dropbox/test.txt", 1000, now);

        // Compute hash at exactly 150 bytes (what was saved during original upload)
        byte[] testData = new byte[150];
        byte[] initialHash = new byte[32];
        string savedHashAt150;
        using (var hasher = System.Security.Cryptography.SHA256.Create())
        {
            hasher.TransformBlock(initialHash, 0, 32, null, 0);
            hasher.TransformFinalBlock(testData, 0, 150);
            savedHashAt150 = BitConverter.ToString(hasher.Hash).Replace("-", "");
        }

        // Create session that says we uploaded 150 bytes with specific hash
        var session = new UploadSessionMetadata(
            "session-123",
            @"C:\test.txt",
            now,
            1000,
            150,  // Resume offset: 150 bytes
            null,
            savedHashAt150);

        _mockSessionPersistence.Setup(s => s.LoadSession()).Returns(session);
        _strategy.PrepareUpload(file);

        // Now upload with 100-byte chunks (misaligned with 150-byte offset)
        // Chunk 1: 100 bytes -> localOffset=100 (< 150, skipped)
        await _strategy.UploadChunkAsync(new byte[100], 100);

        // Chunk 2: 100 bytes -> localOffset=200 (>= 150, hash verified)
        // But hash at 200 bytes != savedHashAt150 bytes
        // Act & Assert
        await Assert.ThrowsExceptionAsync<ResumeFailedException>(() =>
            _strategy.UploadChunkAsync(new byte[100], 100));

        // Session should be cleared due to hash verification failure
        _mockSessionPersistence.Verify(s => s.DeleteSession(), Times.Once,
            "Should clear session when chunk boundaries don't align with saved offset");
    }
}
