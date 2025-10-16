using System;
using System.IO;
using DropboxEncrypedUploader.Models;
using DropboxEncrypedUploader.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropboxEncrypedUploader.Tests;

/// <summary>
/// Tests for SessionPersistenceService with single-session design.
/// </summary>
[TestClass]
public class SessionPersistenceServiceTests
{
    private string _testSessionFile;
    private SessionPersistenceService _service;

    [TestInitialize]
    public void Setup()
    {
        _testSessionFile = Path.Combine(Path.GetTempPath(), $"test-sessions-{Guid.NewGuid()}.json");
        _service = new SessionPersistenceService(_testSessionFile);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_testSessionFile))
            File.Delete(_testSessionFile);
    }

    [TestMethod]
    public void SaveAndLoad_WithEncryptionSaltAndHash_PreservesAllFields()
    {
        // Arrange
        var salt = new byte[Configuration.Configuration.AES_SALT_SIZE];
        for (int i = 0; i < Configuration.Configuration.AES_SALT_SIZE; i++) salt[i] = (byte)i;

        var metadata = new UploadSessionMetadata(
            SessionId: "session-123",
            FilePath: @"C:\test\file.txt",
            ClientModified: new DateTime(2025, 1, 15, 10, 30, 45, DateTimeKind.Utc),
            TotalSize: 1024000,
            CurrentOffset: 512000,
            EncryptionSalt: salt,
            ContentHash: "ABCD1234EF567890"
        );

        // Act
        _service.SaveSession(metadata);
        var loaded = _service.LoadSession();

        // Assert
        Assert.IsNotNull(loaded);
        Assert.AreEqual("session-123", loaded.SessionId);
        Assert.AreEqual(@"C:\test\file.txt", loaded.FilePath);
        Assert.AreEqual(new DateTime(2025, 1, 15, 10, 30, 45, DateTimeKind.Utc), loaded.ClientModified);
        Assert.AreEqual(1024000, loaded.TotalSize);
        Assert.AreEqual(512000, loaded.CurrentOffset);
        Assert.IsNotNull(loaded.EncryptionSalt);
        CollectionAssert.AreEqual(salt, loaded.EncryptionSalt);
        Assert.AreEqual("ABCD1234EF567890", loaded.ContentHash);
    }

    [TestMethod]
    public void SaveAndLoad_WithNullSalt_PreservesNullSalt()
    {
        // For direct uploads (no encryption), salt should be null

        // Arrange
        var metadata = new UploadSessionMetadata(
            SessionId: "session-456",
            FilePath: @"C:\test\file.txt",
            ClientModified: DateTime.UtcNow,
            TotalSize: 1000,
            CurrentOffset: 500,
            EncryptionSalt: null,
            ContentHash: "HASH123"
        );

        // Act
        _service.SaveSession(metadata);
        var loaded = _service.LoadSession();

        // Assert
        Assert.IsNotNull(loaded);
        Assert.IsNull(loaded.EncryptionSalt, "Null salt should be preserved for direct uploads");
        Assert.AreEqual("HASH123", loaded.ContentHash);
    }

    [TestMethod]
    public void LoadSession_FileDoesNotExist_ReturnsNull()
    {
        // Act
        var loaded = _service.LoadSession();

        // Assert
        Assert.IsNull(loaded);
    }

    [TestMethod]
    public void DeleteSession_RemovesSessionFromFile()
    {
        // Arrange
        var metadata = new UploadSessionMetadata(
            "session-789",
            @"C:\test.txt",
            DateTime.UtcNow,
            1000,
            500,
            null,
            "A1B2C3D4E5F6789012345678901234567890123456789012345678901234ABCD"
        );
        _service.SaveSession(metadata);

        // Act
        _service.DeleteSession();
        var loaded = _service.LoadSession();

        // Assert
        Assert.IsNull(loaded, "Deleted session should not be loadable");
    }

    [TestMethod]
    public void SaveSession_UpdateExisting_ReplacesOldData()
    {
        // Single-session design: saving a new session overwrites the previous one

        // Arrange
        var metadata1 = new UploadSessionMetadata("s1", "file1", DateTime.UtcNow, 100, 50, null, "H1");
        var metadata2 = new UploadSessionMetadata("s2", "file2", DateTime.UtcNow, 200, 100, new byte[Configuration.Configuration.AES_SALT_SIZE], "H2");

        // Act
        _service.SaveSession(metadata1);
        _service.SaveSession(metadata2); // Overwrites metadata1

        var loaded = _service.LoadSession();

        // Assert
        Assert.IsNotNull(loaded);
        Assert.AreEqual("s2", loaded.SessionId, "Should load the last saved session");
        Assert.AreEqual(100, loaded.CurrentOffset);
        Assert.AreEqual("H2", loaded.ContentHash);
        Assert.IsNotNull(loaded.EncryptionSalt);
    }

    [TestMethod]
    public void LoadSession_CorruptJSON_LogsWarningAndReturnsNull()
    {
        // Arrange
        File.WriteAllText(_testSessionFile, "{ this is not valid JSON }");

        // Redirect Console.Error to capture warning
        var originalError = Console.Error;
        using (var errorWriter = new StringWriter())
        {
            Console.SetError(errorWriter);

            // Act
            var loaded = _service.LoadSession();

            // Restore Console.Error
            Console.SetError(originalError);

            // Assert
            Assert.IsNull(loaded, "Corrupt JSON should return null");

            var errorOutput = errorWriter.ToString();
            Assert.IsTrue(errorOutput.Contains("WARNING"), "Should log warning");
            Assert.IsTrue(errorOutput.Contains("corrupted"), "Should mention corruption");
            Assert.IsTrue(errorOutput.Contains(_testSessionFile), "Should mention file path");
        }
    }

    [TestMethod]
    public void EncryptionSalt_LargeValue_PreservedCorrectly()
    {
        // Test with maximum valid salt size

        // Arrange
        var largeSalt = new byte[Configuration.Configuration.AES_SALT_SIZE];
        for (int i = 0; i < Configuration.Configuration.AES_SALT_SIZE; i++)
            largeSalt[i] = 0xFF; // All bits set

        var metadata = new UploadSessionMetadata(
            "session", "file", DateTime.UtcNow, 100, 50, largeSalt, "A1B2C3D4E5F6789012345678901234567890123456789012345678901234ABCD");

        // Act
        _service.SaveSession(metadata);
        var loaded = _service.LoadSession();

        // Assert
        Assert.IsNotNull(loaded);
        Assert.IsNotNull(loaded.EncryptionSalt);
        CollectionAssert.AreEqual(largeSalt, loaded.EncryptionSalt);
    }

    [TestMethod]
    public void ContentHash_LongValue_PreservedCorrectly()
    {
        // Test with SHA256 hash (64 hex characters)

        // Arrange
        var longHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var metadata = new UploadSessionMetadata(
            "session", "file", DateTime.UtcNow, 100, 50, null, longHash);

        // Act
        _service.SaveSession(metadata);
        var loaded = _service.LoadSession();

        // Assert
        Assert.IsNotNull(loaded);
        Assert.AreEqual(longHash, loaded.ContentHash);
    }
}
