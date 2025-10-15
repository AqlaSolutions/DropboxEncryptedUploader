using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;
using DropboxEncrypedUploader.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace DropboxEncrypedUploader.Tests;

/// <summary>
/// Integration tests that verify end-to-end behavior.
/// These tests ensure the refactored code behaves identically to Program.old.cs.
/// </summary>
[TestClass]
public class IntegrationTests
{
    private Mock<IFileSystemService> _mockFileSystem;
    private Mock<IDropboxService> _mockDropbox;
    private Mock<IProgressReporter> _mockProgress;
    private Configuration.Configuration _config;

    [TestInitialize]
    public void Setup()
    {
        _mockFileSystem = new Mock<IFileSystemService>();
        _mockDropbox = new Mock<IDropboxService>();
        _mockProgress = new Mock<IProgressReporter>();
    }

    private Metadata CreateFileMetadata(string path, DateTime? clientModified = null)
    {
        return new FileMetadata(
            name: Path.GetFileName(path),
            id: "id:" + path,
            clientModified: clientModified ?? DateTime.UtcNow,
            serverModified: clientModified ?? DateTime.UtcNow,
            rev: "abc123def456789", // Must be at least 9 characters
            size: 1024,
            pathLower: path.ToLowerInvariant(),
            pathDisplay: path,
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    private Metadata CreateDeletedMetadata(string path)
    {
        return new DeletedMetadata(
            name: Path.GetFileName(path),
            pathLower: path.ToLowerInvariant(),
            pathDisplay: path);
    }

    [TestMethod]
    public async Task FullSync_WithEncryption_NewFile_UploadsWithZipExtension()
    {
        // Simulates Program.old.cs main flow with encryption enabled
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);

        // Setup: Local has one file, remote is empty
        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.txt", _config.LocalDirectory))
            .Returns("test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\test.txt"))
            .Returns((1024L, DateTime.UtcNow));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);
        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([]);

        // Execute sync
        var syncService = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var result = await syncService.AnalyzeSyncAsync();

        // Verify results
        Assert.AreEqual(1, result.FilesToUpload.Count);
        Assert.AreEqual("test.txt", result.FilesToUpload[0].RelativePath);
        Assert.AreEqual("/dropbox/test.txt.zip", result.FilesToUpload[0].RemotePath); // .zip extension added
        Assert.AreEqual(0, result.FilesToDelete.Count);
        Assert.IsTrue(result.ExistingFolders.Contains("")); // Root folder initialized
    }

    [TestMethod]
    public async Task FullSync_WithoutEncryption_NewFile_UploadsWithoutZipExtension()
    {
        // Simulates Program.old.cs main flow without encryption
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", ""]);

        // Setup: Local has one file, remote is empty
        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.txt", _config.LocalDirectory))
            .Returns("test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\test.txt"))
            .Returns((1024L, DateTime.UtcNow));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);
        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([]);

        // Execute sync
        var syncService = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var result = await syncService.AnalyzeSyncAsync();

        // Verify results
        Assert.AreEqual(1, result.FilesToUpload.Count);
        Assert.AreEqual("test.txt", result.FilesToUpload[0].RelativePath);
        Assert.AreEqual("/dropbox/test.txt", result.FilesToUpload[0].RemotePath); // No .zip extension
    }

    [TestMethod]
    public async Task FullSync_ExistingFileWithinTimestampTolerance_SkipsUpload()
    {
        // CRITICAL: Verifies timestamp tolerance behavior
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);
        var now = DateTime.UtcNow;

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.txt", _config.LocalDirectory))
            .Returns("test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\test.txt"))
            .Returns((1024L, now));

        var metadata = CreateFileMetadata("/dropbox/test.txt.zip", now.AddMilliseconds(-500));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var syncService = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var result = await syncService.AnalyzeSyncAsync();

        // Should skip upload (within 1 second tolerance)
        Assert.AreEqual(0, result.FilesToUpload.Count);
        Assert.AreEqual(0, result.FilesToDelete.Count);
    }

    [TestMethod]
    public async Task FullSync_RemoteFileNotLocal_MarkedForDeletion()
    {
        // Verifies deletion logic
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([]); // No local files

        var metadata = CreateFileMetadata("/dropbox/deleted.txt.zip", DateTime.UtcNow);

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var syncService = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var result = await syncService.AnalyzeSyncAsync();

        Assert.AreEqual(0, result.FilesToUpload.Count);
        Assert.AreEqual(1, result.FilesToDelete.Count);
        Assert.IsTrue(result.FilesToDelete.Contains("/dropbox/deleted.txt.zip"));
    }

    [TestMethod]
    public async Task FullSync_SubdirectoryFiles_PathConversionCorrect()
    {
        // CRITICAL: Verifies path separator conversion (/ ↔ \)
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);
        var now = DateTime.UtcNow;

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\sub\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\sub\test.txt", _config.LocalDirectory))
            .Returns(@"sub\test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\sub\test.txt"))
            .Returns((1024L, now));

        var metadata = CreateFileMetadata("/dropbox/sub/test.txt.zip", now.AddMilliseconds(-100));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var syncService = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var result = await syncService.AnalyzeSyncAsync();

        // Should match sub\test.txt with sub/test.txt.zip
        Assert.AreEqual(0, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task StorageRecycling_FileAge15to29Days_RestoresAndDeletes()
    {
        // CRITICAL: Verifies recycling age logic
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);
        var now = DateTime.UtcNow;

        var metadata = CreateDeletedMetadata("/dropbox/file.zip");

        var revision = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now.AddDays(-20),
            serverModified: now.AddDays(-20),
            rev: "abc123def456789",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        var revisionsList = new List<FileMetadata> { revision };
        var revisionsResult = new ListRevisionsResult(
            isDeleted: true,
            entries: revisionsList,
            serverDeleted: now.AddDays(-20)); // 20 days old (within 15-29 range)

        var restoredFile = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now,
            serverModified: now,
            rev: "abc123def456789",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);
        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(revisionsResult);
        _mockDropbox.Setup(d => d.RestoreAsync("/dropbox/file.zip", "abc123def456789"))
            .ReturnsAsync(restoredFile);

        var syncResult = new SyncResult(
            [],
            [],
            new HashSet<string>(StringComparer.InvariantCultureIgnoreCase),
            new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" });

        var recyclingService = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);

        await recyclingService.RecycleDeletedFilesAsync(syncResult);

        // Should restore and then batch delete
        _mockDropbox.Verify(d => d.RestoreAsync("/dropbox/file.zip", "abc123def456789"), Times.Once);
        _mockDropbox.Verify(d => d.DeleteBatchAsync(It.Is<IEnumerable<string>>(paths => paths.Contains("/dropbox/file.zip"))), Times.Once);
    }

    [TestMethod]
    public async Task CaseInsensitiveComparison_DifferentCaseFiles_TreatedAsIdentical()
    {
        // CRITICAL: Verifies case-insensitive comparison behavior
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);
        var now = DateTime.UtcNow;

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\Test.txt", @"C:\local\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\Test.txt", _config.LocalDirectory))
            .Returns("Test.txt");
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.txt", _config.LocalDirectory))
            .Returns("test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(It.IsAny<string>()))
            .Returns((1024L, now));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);

        var syncService = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var result = await syncService.AnalyzeSyncAsync();

        // Should treat as single file (case-insensitive)
        Assert.AreEqual(1, result.FilesToUpload.Count);
    }

    [TestMethod]
    public void Configuration_BufferSizes_MatchOriginalConstants()
    {
        // Verifies all configuration constants match Program.old.cs
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "pass"]);

        Assert.AreEqual(90_000_000, _config.ReadBufferSize);
        Assert.AreEqual(99_000_000, _config.MaxBufferAllocation);
        Assert.AreEqual(2000, _config.ListFolderLimit);
        Assert.AreEqual(32UL * 1024 * 1024 * 1024, _config.DeletingBatchSize);
        Assert.AreEqual(10, _config.MaxRetries);
        Assert.AreEqual(15, _config.MinRecycleAgeDays);
        Assert.AreEqual(29, _config.MaxRecycleAgeDays);
        Assert.AreEqual(1.0, _config.TimestampToleranceSeconds);
    }

    [TestMethod]
    public async Task MultipleFiles_UploadOrder_Preserved()
    {
        // Verifies files are processed in order
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\a.txt", @"C:\local\b.txt", @"C:\local\c.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\a.txt", _config.LocalDirectory))
            .Returns("a.txt");
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\b.txt", _config.LocalDirectory))
            .Returns("b.txt");
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\c.txt", _config.LocalDirectory))
            .Returns("c.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(It.IsAny<string>()))
            .Returns((1024L, DateTime.UtcNow));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);

        var syncService = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var result = await syncService.AnalyzeSyncAsync();

        Assert.AreEqual(3, result.FilesToUpload.Count);
        // Files should be in the order returned by GetAllFiles
        CollectionAssert.AreEqual(
            new[] { "a.txt", "b.txt", "c.txt" },
            result.FilesToUpload.Select(f => f.RelativePath).ToArray());
    }
}