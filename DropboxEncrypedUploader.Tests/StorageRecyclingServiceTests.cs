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
/// Tests for StorageRecyclingService.
/// CRITICAL: Verifies behavior matches Program.old.cs lines 283-347 (recycling logic).
/// </summary>
[TestClass]
public class StorageRecyclingServiceTests
{
    private Mock<IDropboxService> _mockDropbox;
    private Mock<IProgressReporter> _mockProgress;
    private Configuration.Configuration _config;

    [TestInitialize]
    public void Setup()
    {
        _mockDropbox = new Mock<IDropboxService>();
        _mockProgress = new Mock<IProgressReporter>();
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);
    }

    private SyncResult CreateSyncResult(
        HashSet<string> existingFiles = null,
        HashSet<string> existingFolders = null)
    {
        var folders = existingFolders ?? new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "" };
        return new SyncResult(
            [],
            [],
            existingFiles ?? new HashSet<string>(StringComparer.InvariantCultureIgnoreCase),
            folders);
    }

    private Metadata CreateFileMetadata(string path, bool isDeleted = false)
    {
        if (isDeleted)
        {
            // For deleted files, return a DeletedMetadata instance
            return new DeletedMetadata(
                name: Path.GetFileName(path),
                pathLower: path.ToLowerInvariant(),
                pathDisplay: path);
        }

        return new FileMetadata(
            name: Path.GetFileName(path),
            id: "id:" + path,
            clientModified: DateTime.UtcNow,
            serverModified: DateTime.UtcNow,
            rev: "abc123def456789", // Must be at least 9 characters
            size: 1024,
            pathLower: path.ToLowerInvariant(),
            pathDisplay: path,
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_SkipsNonDeletedEntries()
    {
        // Verifies Program.old.cs line 293
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: false);

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var syncResult = CreateSyncResult();

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should not call ListRevisionsAsync for non-deleted files
        _mockDropbox.Verify(d => d.ListRevisionsAsync(It.IsAny<string>(), It.IsAny<ListRevisionsMode>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_SkipsIfFileStillExists()
    {
        // CRITICAL: Verifies Program.old.cs line 293 - || existingFiles.Contains(entry.PathLower)
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);

        var existingFiles = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "/dropbox/file.zip" };
        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var syncResult = CreateSyncResult(existingFiles: existingFiles);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should not call ListRevisionsAsync if file still exists
        _mockDropbox.Verify(d => d.ListRevisionsAsync(It.IsAny<string>(), It.IsAny<ListRevisionsMode>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_SkipsIfParentFolderDoesNotExist()
    {
        // CRITICAL: Verifies Program.old.cs lines 295-299
        var metadata = CreateFileMetadata("/dropbox/subfolder/file.zip", isDeleted: true);

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);

        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "" }; // No subfolder
        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should not call ListRevisionsAsync if parent folder doesn't exist
        _mockDropbox.Verify(d => d.ListRevisionsAsync(It.IsAny<string>(), It.IsAny<ListRevisionsMode>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_SkipsIfNoLastSlash()
    {
        // Verifies Program.old.cs lines 296-297
        var metadata = CreateFileMetadata("file.zip", isDeleted: true); // No slash

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var syncResult = CreateSyncResult();

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should not call ListRevisionsAsync
        _mockDropbox.Verify(d => d.ListRevisionsAsync(It.IsAny<string>(), It.IsAny<ListRevisionsMode>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_ContinuesOnListRevisionsException()
    {
        // CRITICAL: Verifies Program.old.cs lines 307-311 - per-file exception handling
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);
        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ThrowsAsync(new Exception("Folder error"));

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        // Include parent folder "/dropbox" so the file passes the parent folder check
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        // Should not throw
        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        _mockDropbox.Verify(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1), Times.Once);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_SkipsIfTooYoung()
    {
        // CRITICAL: Verifies Program.old.cs line 313 - !(DateTime.UtcNow - rev.ServerDeleted >= TimeSpan.FromDays(15))
        var now = DateTime.UtcNow;
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

        var revision = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now.AddDays(-10),
            serverModified: now.AddDays(-10),
            rev: "abc123def456789",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        var revisionsList = new List<FileMetadata> { revision };
        var revisionsResult = new ListRevisionsResult(
            isDeleted: true,
            entries: revisionsList,
            serverDeleted: now.AddDays(-10)); // Only 10 days old

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);
        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(revisionsResult);

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should not call RestoreAsync for too young files
        _mockDropbox.Verify(d => d.RestoreAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_SkipsIfTooOld()
    {
        // CRITICAL: Verifies Program.old.cs line 313 - || (DateTime.UtcNow - rev.ServerDeleted > TimeSpan.FromDays(29))
        var now = DateTime.UtcNow;
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

        var revision = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now.AddDays(-30),
            serverModified: now.AddDays(-30),
            rev: "abc123def456789",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        var revisionsList = new List<FileMetadata> { revision };
        var revisionsResult = new ListRevisionsResult(
            isDeleted: true,
            entries: revisionsList,
            serverDeleted: now.AddDays(-30)); // 30 days old

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);
        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(revisionsResult);

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should not call RestoreAsync for too old files
        _mockDropbox.Verify(d => d.RestoreAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_RestoresIfExactly15DaysOld()
    {
        // CRITICAL: Verifies Program.old.cs line 313 - age >= 15 days (inclusive)
        var now = DateTime.UtcNow;
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

        var revision = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now.AddDays(-15),
            serverModified: now.AddDays(-15),
            rev: "abc123def456789",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        var revisionsList = new List<FileMetadata> { revision };
        var revisionsResult = new ListRevisionsResult(
            isDeleted: true,
            entries: revisionsList,
            serverDeleted: now.AddDays(-15)); // Exactly 15 days

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

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should call RestoreAsync for 15-day-old file
        _mockDropbox.Verify(d => d.RestoreAsync("/dropbox/file.zip", "abc123def456789"), Times.Once);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_RestoresIfExactly29DaysOld()
    {
        // CRITICAL: Verifies Program.old.cs line 313 - age > 29 days (not >=)
        // Using 29 days minus 1 hour to avoid floating point precision issues at the exact boundary
        var now = DateTime.UtcNow;
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

        var revision = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now.AddDays(-29).AddHours(1),
            serverModified: now.AddDays(-29).AddHours(1),
            rev: "abc123def456789",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        var revisionsList = new List<FileMetadata> { revision };
        var revisionsResult = new ListRevisionsResult(
            isDeleted: true,
            entries: revisionsList,
            serverDeleted: now.AddDays(-29).AddHours(1)); // 29 days minus 1 hour (within valid range)

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

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        // Include parent folder "/dropbox" so the file passes the parent folder check
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // CRITICAL: Exactly 29 days should be INCLUDED (age <= 29, not age < 29)
        // Program.old.cs line 60: (DateTime.UtcNow - rev.ServerDeleted > TimeSpan.FromDays(29))
        // This means skip if age > 29, so age == 29 is processed
        _mockDropbox.Verify(d => d.RestoreAsync("/dropbox/file.zip", "abc123def456789"), Times.Once);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_SelectsNewestRevisionByClientModified()
    {
        // CRITICAL: Verifies Program.old.cs line 323 - rev.Entries.OrderByDescending(x => x.ClientModified).First().Rev
        var now = DateTime.UtcNow;
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

        var olderRevision = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now.AddDays(-20),
            serverModified: now.AddDays(-20),
            rev: "0123456789abc",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        var newerRevision = new FileMetadata(
            name: "file.zip",
            id: "id2",
            clientModified: now.AddDays(-18),
            serverModified: now.AddDays(-18),
            rev: "def456789abcd",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210");

        var revisionsList = new List<FileMetadata> { olderRevision, newerRevision };
        var revisionsResult = new ListRevisionsResult(
            isDeleted: true,
            entries: revisionsList,
            serverDeleted: now.AddDays(-18));

        var restoredFile = new FileMetadata(
            name: "file.zip",
            id: "id2",
            clientModified: now,
            serverModified: now,
            rev: "def456789abcd",
            size: 1024,
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210");

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);
        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(revisionsResult);
        _mockDropbox.Setup(d => d.RestoreAsync("/dropbox/file.zip", "def456789abcd"))
            .ReturnsAsync(restoredFile);

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should restore with the newest revision (rev_new)
        _mockDropbox.Verify(d => d.RestoreAsync("/dropbox/file.zip", "def456789abcd"), Times.Once);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_LargeFileWithEmptyQueue_DeletesImmediately()
    {
        // CRITICAL: Verifies Program.old.cs lines 325-329
        var now = DateTime.UtcNow;
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

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
            serverDeleted: now.AddDays(-20));

        var restoredFile = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now,
            serverModified: now,
            rev: "abc123def456789",
            size: 32UL * 1024 * 1024 * 1024, // Exactly 32GB
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);
        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(revisionsResult);
        _mockDropbox.Setup(d => d.RestoreAsync("/dropbox/file.zip", "abc123def456789"))
            .ReturnsAsync(restoredFile);

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should call DeleteFileAsync immediately (not batch)
        _mockDropbox.Verify(d => d.DeleteFileAsync("/dropbox/file.zip", "abc123def456789"), Times.Once);
        _mockDropbox.Verify(d => d.DeleteBatchAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_SmallFile_AddsToBatch()
    {
        // CRITICAL: Verifies Program.old.cs lines 331-338
        var now = DateTime.UtcNow;
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

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
            serverDeleted: now.AddDays(-20));

        var restoredFile = new FileMetadata(
            name: "file.zip",
            id: "id1",
            clientModified: now,
            serverModified: now,
            rev: "abc123def456789",
            size: 1024, // Small file
            pathLower: "/dropbox/file.zip",
            pathDisplay: "/dropbox/file.zip",
            contentHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);
        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(revisionsResult);
        _mockDropbox.Setup(d => d.RestoreAsync("/dropbox/file.zip", "abc123def456789"))
            .ReturnsAsync(restoredFile);

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should call DeleteBatchAsync at the end (line 347)
        _mockDropbox.Verify(d => d.DeleteFileAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockDropbox.Verify(d => d.DeleteBatchAsync(It.Is<IEnumerable<string>>(paths => paths.Contains("/dropbox/file.zip"))), Times.Once);
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_32GBFileWithNonEmptyQueue_AddsToBatch()
    {
        // CRITICAL: Verifies Program.old.cs lines 325-329, 336-337
        // When file >= 32GB BUT queue is NOT empty (filesToDelete.Count > 0),
        // it should NOT delete immediately, but add to batch instead
        var now = DateTime.UtcNow;

        // First file: small file to populate queue
        var smallMetadata = CreateFileMetadata("/dropbox/small.zip", isDeleted: true);
        var smallRevision = new FileMetadata(
            "small.zip", "id0", now.AddDays(-20), now.AddDays(-20),
            "abc123456789abc", 1024, "/dropbox/small.zip", "/dropbox/small.zip",
            "hash1");
        var smallRevisionResult = new ListRevisionsResult(true, new List<FileMetadata> { smallRevision }, now.AddDays(-20));
        var smallRestoredFile = new FileMetadata(
            "small.zip", "id0", now, now,
            "abc123456789abc", 1024, "/dropbox/small.zip", "/dropbox/small.zip",
            "hash1");

        // Second file: 32GB file
        var largeMetadata = CreateFileMetadata("/dropbox/large.zip", isDeleted: true);
        var largeRevision = new FileMetadata(
            "large.zip", "id1", now.AddDays(-20), now.AddDays(-20),
            "abc123def456789", 1024, "/dropbox/large.zip", "/dropbox/large.zip",
            "hash2");
        var largeRevisionResult = new ListRevisionsResult(true, new List<FileMetadata> { largeRevision }, now.AddDays(-20));
        var largeRestoredFile = new FileMetadata(
            "large.zip", "id1", now, now,
            "abc123def456789", 32UL * 1024 * 1024 * 1024, // Exactly 32GB
            "/dropbox/large.zip", "/dropbox/large.zip",
            "hash2");

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([smallMetadata, largeMetadata]);

        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/small.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(smallRevisionResult);
        _mockDropbox.Setup(d => d.RestoreAsync("/dropbox/small.zip", "abc123456789abc"))
            .ReturnsAsync(smallRestoredFile);

        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/large.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(largeRevisionResult);
        _mockDropbox.Setup(d => d.RestoreAsync("/dropbox/large.zip", "abc123def456789"))
            .ReturnsAsync(largeRestoredFile);

        // Capture the arguments passed to DeleteBatchAsync
        var capturedPaths = new List<string>();
        _mockDropbox.Setup(d => d.DeleteBatchAsync(It.IsAny<IEnumerable<string>>()))
            .Callback<IEnumerable<string>>(paths => capturedPaths.AddRange(paths))
            .Returns(Task.CompletedTask);

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        // Should NOT call DeleteFileAsync for the 32GB file because queue is not empty
        _mockDropbox.Verify(d => d.DeleteFileAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        // Should call DeleteBatchAsync once with both files
        _mockDropbox.Verify(d => d.DeleteBatchAsync(It.IsAny<IEnumerable<string>>()), Times.Once);
        // Verify both files were included in the batch
        Assert.IsTrue(capturedPaths.Contains("/dropbox/small.zip"), "small.zip should be in batch");
        Assert.IsTrue(capturedPaths.Contains("/dropbox/large.zip"), "large.zip should be in batch");
        Assert.AreEqual(2, capturedPaths.Count, "Should have exactly 2 files in batch");
    }

    [TestMethod]
    public async Task RecycleDeletedFilesAsync_ContinuesOnRestoreException()
    {
        // CRITICAL: Verifies Program.old.cs lines 340-343 - per-file exception handling
        var now = DateTime.UtcNow;
        var metadata = CreateFileMetadata("/dropbox/file.zip", isDeleted: true);

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
            serverDeleted: now.AddDays(-20));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, true))
            .ReturnsAsync([metadata]);
        _mockDropbox.Setup(d => d.ListRevisionsAsync("/dropbox/file.zip", It.IsAny<ListRevisionsMode>(), 1))
            .ReturnsAsync(revisionsResult);
        _mockDropbox.Setup(d => d.RestoreAsync("/dropbox/file.zip", "abc123def456789"))
            .ThrowsAsync(new Exception("Restore failed"));

        var service = new StorageRecyclingService(_mockDropbox.Object, _mockProgress.Object, _config);
        var folders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "", "/dropbox" };
        var syncResult = CreateSyncResult(existingFolders: folders);

        // Should not throw
        var deletedFiles = await service.ListRecyclableDeletedFilesAsync(
            syncResult.ExistingFiles,
            syncResult.ExistingFolders);
        await service.RestoreAndDeleteFilesAsync(deletedFiles);

        _mockProgress.Verify(p => p.ReportMessage(It.Is<string>(s => s.Contains("Error recycling file"))), Times.Once);
    }
}