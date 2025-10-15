using System;
using System.IO;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;
using DropboxEncrypedUploader.Upload;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace DropboxEncrypedUploader.Tests;

/// <summary>
/// Tests for EncryptedUploadStrategy.
/// CRITICAL: Verifies behavior matches Program.old.cs lines 137-214 (encrypted upload logic).
/// </summary>
[TestClass]
public class EncryptedUploadStrategyTests
{
    private Mock<IUploadSessionManager> _mockSessionManager;
    private Mock<IProgressReporter> _mockProgress;
    private Configuration.Configuration _config;
    private Mock<AsyncMultiFileReader> _mockReader;

    [TestInitialize]
    public void Setup()
    {
        _mockSessionManager = new Mock<IUploadSessionManager>();
        _mockProgress = new Mock<IProgressReporter>();
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", "password"]);
        _mockReader = new Mock<AsyncMultiFileReader>(_config.ReadBufferSize, null);
    }

    // NOTE: Reader state management tests removed - now handled by Program.cs
    // OpenNextFile() and NextFile assignment are orchestrated by the caller, not the strategy

    [TestMethod]
    public async Task UploadFileAsync_ReportsProgress()
    {
        // Verifies Program.old.cs line 147, 177
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt.zip",
            100,
            DateTime.UtcNow);

        _mockReader.Setup(r => r.ReadNextBlock()).Returns(0);

        var strategy = new EncryptedUploadStrategy(_mockSessionManager.Object, _mockProgress.Object, _config);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        _mockProgress.Verify(p => p.ReportProgress("test.txt", 0, 100), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_ReportsComplete()
    {
        // Verifies Program.old.cs line 191
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt.zip",
            0,
            DateTime.UtcNow);

        _mockReader.Setup(r => r.ReadNextBlock()).Returns(0);

        var strategy = new EncryptedUploadStrategy(_mockSessionManager.Object, _mockProgress.Object, _config);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        _mockProgress.Verify(p => p.ReportComplete("test.txt", ""), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_EmptyFile_UsesSimpleUpload()
    {
        // Verifies Program.old.cs behavior for empty files (session is null, goes to line 450-460)
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt.zip",
            0,
            DateTime.UtcNow);

        _mockReader.Setup(r => r.ReadNextBlock()).Returns(0); // No data

        var strategy = new EncryptedUploadStrategy(_mockSessionManager.Object, _mockProgress.Object, _config);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Should call SimpleUploadAsync for final chunk (even if empty)
        _mockSessionManager.Verify(s => s.SimpleUploadAsync(
            It.IsAny<CommitInfo>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_SingleChunk_UsesSessionUpload()
    {
        // CRITICAL: For encrypted uploads, even a single chunk of source data creates multiple upload chunks:
        // 1. Encrypted data chunk (uploaded during while loop via StartSessionAsync)
        // 2. ZIP footer/metadata (uploaded after ZIP close via FinishSessionAsync)
        // This differs from DirectUploadStrategy which detects isLastChunk and uses SimpleUploadAsync.
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt.zip",
            100,
            DateTime.UtcNow);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() => callCount++ == 0 ? 100 : 0);
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        _mockSessionManager.Setup(s => s.StartSessionAsync(
                It.IsAny<byte[]>(),
                It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        var strategy = new EncryptedUploadStrategy(_mockSessionManager.Object, _mockProgress.Object, _config);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Should use session upload (Start + Finish) because ZIP footer creates second chunk
        _mockSessionManager.Verify(s => s.StartSessionAsync(
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
        _mockSessionManager.Verify(s => s.FinishSessionAsync(
            "session-123",
            It.IsAny<long>(),
            It.IsAny<CommitInfo>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
        _mockSessionManager.Verify(s => s.SimpleUploadAsync(
            It.IsAny<CommitInfo>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadFileAsync_MultipleChunks_UsesSessionUpload()
    {
        // Verifies Program.old.cs lines 184, 187, 212 - session flow
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt.zip",
            200,
            DateTime.UtcNow);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() =>
        {
            callCount++;
            return callCount <= 2 ? 100 : 0;
        });
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        _mockSessionManager.Setup(s => s.StartSessionAsync(
                It.IsAny<byte[]>(),
                It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        var strategy = new EncryptedUploadStrategy(_mockSessionManager.Object, _mockProgress.Object, _config);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Should start session, append, then finish
        _mockSessionManager.Verify(s => s.StartSessionAsync(
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
        _mockSessionManager.Verify(s => s.AppendSessionAsync(
            "session-123",
            It.IsAny<long>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.AtLeastOnce);
        _mockSessionManager.Verify(s => s.FinishSessionAsync(
            "session-123",
            It.IsAny<long>(),
            It.IsAny<CommitInfo>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_CreatesCommitInfoWithAllProperties()
    {
        // Verifies Program.old.cs lines 210, 494-497 - CommitInfo construction
        // Combines verification of: path, overwrite mode, autorename false, and client modified timestamp
        var now = DateTime.UtcNow;
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt.zip",
            0,
            now);

        _mockReader.Setup(r => r.ReadNextBlock()).Returns(0);

        var strategy = new EncryptedUploadStrategy(_mockSessionManager.Object, _mockProgress.Object, _config);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        _mockSessionManager.Verify(s => s.SimpleUploadAsync(
            It.Is<CommitInfo>(c =>
                c.Path == "/dropbox/test.txt.zip" &&
                c.Mode.IsOverwrite &&
                c.Autorename == false &&
                c.ClientModified == now),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_ExceptionDuringZip_StillReported()
    {
        // Verifies Program.old.cs lines 197-204 - exception handling
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt.zip",
            100,
            DateTime.UtcNow);

        _mockReader.Setup(r => r.ReadNextBlock()).Throws(new IOException("Read error"));

        var strategy = new EncryptedUploadStrategy(_mockSessionManager.Object, _mockProgress.Object, _config);

        await Assert.ThrowsExceptionAsync<IOException>(() =>
            strategy.UploadFileAsync(fileToUpload, _mockReader.Object));

        _mockProgress.Verify(p => p.ReportMessage(It.Is<string>(s => s.Contains("Error during encrypted upload"))), Times.Once);
    }
}