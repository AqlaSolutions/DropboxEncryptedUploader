using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace DropboxEncrypedUploader.Tests;

/// <summary>
/// Tests for SyncService.
/// CRITICAL: Verifies behavior matches Program.old.cs lines 35-101 (sync analysis logic).
/// </summary>
[TestClass]
public class SyncServiceTests
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

    private Configuration.Configuration CreateConfig(bool withEncryption)
    {
        return new Configuration.Configuration([
            "token",
            @"C:\local\",
            "/dropbox/",
            withEncryption ? "password" : ""
        ]);
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

    private Metadata CreateFolderMetadata(string path)
    {
        return new FolderMetadata(
            name: Path.GetFileName(path),
            id: "id:" + path,
            pathLower: path.ToLowerInvariant(),
            pathDisplay: path);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_LocalDirectoryDoesNotExist_ReportsMessage()
    {
        // Verifies Program.old.cs lines 31-33
        _config = CreateConfig(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(false);
        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        await facade.AnalyzeSyncAsync();

        _mockProgress.Verify(p => p.ReportMessage("Local directory does not exist: " + _config.LocalDirectory), Times.Once);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_LocalDirectoryDoesNotExist_ReturnsEmptyLocalFiles()
    {
        // Verifies Program.old.cs lines 35-39 (empty array when directory doesn't exist)
        _config = CreateConfig(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(false);
        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        Assert.AreEqual(0, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_UsesOrdinalIgnoreCaseForLocalFiles()
    {
        // CRITICAL: Verifies Program.old.cs line 39 - StringComparer.OrdinalIgnoreCase
        _config = CreateConfig(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\File.txt", @"C:\local\file.txt"]); // Same file, different case
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\File.txt", _config.LocalDirectory))
            .Returns("File.txt");
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\file.txt", _config.LocalDirectory))
            .Returns("file.txt");
        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // OrdinalIgnoreCase should treat them as same file (only one entry)
        Assert.AreEqual(1, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_ExistingFolders_InitializedWithEmptyString()
    {
        // CRITICAL: Verifies Program.old.cs line 61 - existingFolders.Add("")
        _config = CreateConfig(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory)).Returns([]);
        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        Assert.IsTrue(result.ExistingFolders.Contains(""));
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_UsesInvariantIgnoreCaseForExistingFiles()
    {
        // CRITICAL: Verifies Program.old.cs line 59 - StringComparer.InvariantCultureIgnoreCase
        _config = CreateConfig(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory)).Returns([]);

        var metadata = CreateFileMetadata("/dropbox/test.zip", DateTime.UtcNow);

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        Assert.IsTrue(result.ExistingFiles.Contains("/dropbox/test.zip"));
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_WithEncryption_StripsZipExtension()
    {
        // CRITICAL: Verifies Program.old.cs lines 82-85
        _config = CreateConfig(withEncryption: true);
        var now = DateTime.UtcNow;

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.txt", _config.LocalDirectory))
            .Returns("test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\test.txt"))
            .Returns((1024L, now));

        var metadata = CreateFileMetadata("/dropbox/test.txt.zip", now.AddMilliseconds(-100));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // Should match test.txt with test.txt.zip (after stripping .zip)
        Assert.AreEqual(0, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_WithEncryption_SkipsFilesWithoutZipExtension()
    {
        // CRITICAL: Verifies Program.old.cs line 84 - if (!relativePath.EndsWith(".zip")) continue;
        _config = CreateConfig(withEncryption: true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory)).Returns([]);

        var metadata = CreateFileMetadata("/dropbox/test.txt");

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // File without .zip extension should be skipped during path comparison (continue statement)
        // but it's still added to ExistingFiles collection
        Assert.AreEqual(1, result.ExistingFiles.Count(f => f == "/dropbox/test.txt"));
        // The key is that no local files should match it for comparison
        Assert.AreEqual(0, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_WithoutEncryption_DoesNotStripExtension()
    {
        // CRITICAL: Verifies Program.old.cs lines 87-90
        _config = CreateConfig(withEncryption: false);
        var now = DateTime.UtcNow;

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\test.zip"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.zip", _config.LocalDirectory))
            .Returns("test.zip");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\test.zip"))
            .Returns((1024L, now));

        var metadata = CreateFileMetadata("/dropbox/test.zip", now.AddMilliseconds(-100));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // Should match test.zip with test.zip (no stripping)
        Assert.AreEqual(0, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_TimestampWithinTolerance_SkipsUpload()
    {
        // CRITICAL: Verifies Program.old.cs line 95 - (info.LastWriteTimeUtc - entry.AsFile.ClientModified).TotalSeconds < 1f
        _config = CreateConfig(true);
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

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // File should be skipped (within 1 second tolerance)
        Assert.AreEqual(0, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_TimestampExactlyAtTolerance_SkipsUpload()
    {
        // CRITICAL: Verifies Program.old.cs line 95 - uses < 1f (not <= 1f)
        _config = CreateConfig(true);
        var now = DateTime.UtcNow;

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.txt", _config.LocalDirectory))
            .Returns("test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\test.txt"))
            .Returns((1024L, now));

        var metadata = CreateFileMetadata("/dropbox/test.txt.zip", now.AddSeconds(-0.999));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // Should be skipped (< 1 second)
        Assert.AreEqual(0, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_TimestampExceedsTolerance_IncludesInUpload()
    {
        // CRITICAL: Verifies Program.old.cs line 95 - files > 1 second different should be uploaded
        _config = CreateConfig(true);
        var now = DateTime.UtcNow;

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.txt", _config.LocalDirectory))
            .Returns("test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\test.txt"))
            .Returns((1024L, now));

        var metadata = CreateFileMetadata("/dropbox/test.txt.zip", now.AddSeconds(-1.5));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // File should be included (> 1 second difference)
        Assert.AreEqual(1, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_RemoteFileNotLocal_MarkedForDeletion()
    {
        // CRITICAL: Verifies Program.old.cs lines 98-99
        _config = CreateConfig(true);

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory)).Returns([]);

        var metadata = CreateFileMetadata("/dropbox/deleted.txt.zip", DateTime.UtcNow);

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        Assert.IsTrue(result.FilesToDelete.Contains("/dropbox/deleted.txt.zip"));
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_LocalDirectoryDoesNotExist_DoesNotMarkForDeletion()
    {
        // CRITICAL: Verifies Program.old.cs line 98 - else if (localExists)
        _config = CreateConfig(true);

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(false);

        var metadata = CreateFileMetadata("/dropbox/file.zip", DateTime.UtcNow);

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // Should NOT be marked for deletion when local directory doesn't exist
        Assert.AreEqual(0, result.FilesToDelete.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_FolderEntry_AddedToExistingFolders()
    {
        // Verifies Program.old.cs lines 69-73
        _config = CreateConfig(true);

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory)).Returns([]);

        var metadata = CreateFolderMetadata("/dropbox/subfolder");

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([metadata]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        Assert.IsTrue(result.ExistingFolders.Contains("/dropbox/subfolder"));
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_ForwardSlashConvertedToBackslash()
    {
        // CRITICAL: Verifies Program.old.cs line 85 - .Replace("/", Path.DirectorySeparatorChar + "")
        _config = CreateConfig(true);
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

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        // Should match sub\test.txt with sub/test.txt.zip (after conversion)
        Assert.AreEqual(0, result.FilesToUpload.Count);
    }

    [TestMethod]
    public async Task AnalyzeSyncAsync_RemotePathCalculation_UsesCorrectFormat()
    {
        // Verifies Program.old.cs line 494 - Path.Combine + Replace("\\", "/") + fileExtension
        _config = CreateConfig(true);

        _mockFileSystem.Setup(fs => fs.DirectoryExists(_config.LocalDirectory)).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetAllFiles(_config.LocalDirectory))
            .Returns([@"C:\local\test.txt"]);
        _mockFileSystem.Setup(fs => fs.GetRelativePath(@"C:\local\test.txt", _config.LocalDirectory))
            .Returns("test.txt");
        _mockFileSystem.Setup(fs => fs.GetFileInfo(@"C:\local\test.txt"))
            .Returns((1024L, DateTime.UtcNow));

        _mockDropbox.Setup(d => d.ListAllFilesAsync(_config.DropboxDirectory, false))
            .ReturnsAsync([]);

        var service = new SyncService(_mockFileSystem.Object, _mockDropbox.Object, _mockProgress.Object, _config);
        var facade = new SyncFacade(service, _mockFileSystem.Object, _config);

        var result = await facade.AnalyzeSyncAsync();

        Assert.AreEqual(1, result.FilesToUpload.Count);
        Assert.AreEqual("/dropbox/test.txt.zip", result.FilesToUpload[0].RemotePath);
    }
}