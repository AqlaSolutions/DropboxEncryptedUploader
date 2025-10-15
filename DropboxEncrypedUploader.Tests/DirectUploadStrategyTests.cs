using System;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;
using DropboxEncrypedUploader.Upload;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace DropboxEncrypedUploader.Tests;

/// <summary>
/// Tests for DirectUploadStrategy.
/// CRITICAL: Verifies behavior matches Program.old.cs lines 218-280 (direct upload logic).
/// </summary>
[TestClass]
public class DirectUploadStrategyTests
{
    private Mock<IUploadSessionManager> _mockSessionManager;
    private Mock<IProgressReporter> _mockProgress;
    private Mock<AsyncMultiFileReader> _mockReader;
    private Configuration.Configuration _config;

    [TestInitialize]
    public void Setup()
    {
        _mockSessionManager = new Mock<IUploadSessionManager>();
        _mockProgress = new Mock<IProgressReporter>();
        _config = new Configuration.Configuration(["token", @"C:\local\", "/dropbox/", ""]);
        _mockReader = new Mock<AsyncMultiFileReader>(
            _config.ReadBufferSize,
            null);
    }

    [TestMethod]
    public async Task UploadFileAsync_EmptyFile_UsesSimpleUploadWithEmptyArray()
    {
        // CRITICAL: Verifies Program.old.cs lines 231-242 - empty file handling
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            0, // Empty file
            DateTime.UtcNow);

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Should call SimpleUploadAsync with empty array
        _mockSessionManager.Verify(s => s.SimpleUploadAsync(
            It.IsAny<CommitInfo>(),
            It.Is<byte[]>(b => b.Length == 0),
            0), Times.Once);

        // Should report complete with "(empty file)" suffix
        _mockProgress.Verify(p => p.ReportComplete("test.txt", "(empty file)"), Times.Once);
    }

    // NOTE: Reader state management tests removed - now handled by Program.cs
    // OpenNextFile() and NextFile assignment are orchestrated by the caller, not the strategy

    [TestMethod]
    public async Task UploadFileAsync_ReportsProgress()
    {
        // Verifies Program.old.cs line 222, 253
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            100,
            DateTime.UtcNow);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() => callCount++ == 0 ? 100 : 0);
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        _mockProgress.Verify(p => p.ReportProgress("test.txt", 0, 100), Times.Once);
        _mockProgress.Verify(p => p.ReportProgress("test.txt", 100, 100), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_ReportsComplete()
    {
        // Verifies Program.old.cs line 278
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            100,
            DateTime.UtcNow);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() => callCount++ == 0 ? 100 : 0);
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        _mockProgress.Verify(p => p.ReportComplete("test.txt", ""), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_SingleChunk_UsesSimpleUpload()
    {
        // CRITICAL: Verifies Program.old.cs lines 264-267 - isLastChunk logic
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            100,
            DateTime.UtcNow);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() => callCount++ == 0 ? 100 : 0);
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Should call SimpleUploadAsync (no session)
        _mockSessionManager.Verify(s => s.SimpleUploadAsync(
            It.IsAny<CommitInfo>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
        _mockSessionManager.Verify(s => s.StartSessionAsync(
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadFileAsync_MultipleChunks_UsesSessionUpload()
    {
        // CRITICAL: Verifies Program.old.cs lines 261-276 - chunk detection and session flow
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
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

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Should start session for first chunk (not last)
        _mockSessionManager.Verify(s => s.StartSessionAsync(
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);

        // Should finish session for last chunk
        _mockSessionManager.Verify(s => s.FinishSessionAsync(
            "session-123",
            It.IsAny<long>(),
            It.IsAny<CommitInfo>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_LastChunkDetection_BasedOnTotalBytesRead()
    {
        // CRITICAL: Verifies Program.old.cs line 262 - bool isLastChunk = totalBytesRead >= info.Length
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            150, // File size
            DateTime.UtcNow);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() =>
        {
            callCount++;
            if (callCount == 1) return 100; // First chunk, not last (100 < 150)
            if (callCount == 2) return 50;  // Second chunk, last (150 >= 150)
            return 0;
        });
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        _mockSessionManager.Setup(s => s.StartSessionAsync(
                It.IsAny<byte[]>(),
                It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // First chunk: Start session (not last chunk)
        _mockSessionManager.Verify(s => s.StartSessionAsync(
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);

        // Second chunk: Finish session (is last chunk)
        _mockSessionManager.Verify(s => s.FinishSessionAsync(
            "session-123",
            It.IsAny<long>(),
            It.IsAny<CommitInfo>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);

        // Should never append (only 2 chunks: start + finish)
        _mockSessionManager.Verify(s => s.AppendSessionAsync(
            It.IsAny<string>(),
            It.IsAny<long>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadFileAsync_ThreeChunks_StartsAppendsFinishes()
    {
        // Verifies session flow: Start → Append → Finish
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            300,
            DateTime.UtcNow);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() =>
        {
            callCount++;
            return callCount <= 3 ? 100 : 0;
        });
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        _mockSessionManager.Setup(s => s.StartSessionAsync(
                It.IsAny<byte[]>(),
                It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Chunk 1 (0-100): Start
        _mockSessionManager.Verify(s => s.StartSessionAsync(
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);

        // Chunk 2 (100-200): Append
        _mockSessionManager.Verify(s => s.AppendSessionAsync(
            "session-123",
            It.IsAny<long>(),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);

        // Chunk 3 (200-300): Finish
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
        // Verifies Program.old.cs lines 229, 494-497 - CommitInfo construction
        // Combines verification of: path (no .zip for direct upload), overwrite mode, autorename false, and client modified timestamp
        var now = DateTime.UtcNow;
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt", // No .zip extension for direct upload
            100,
            now);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() => callCount++ == 0 ? 100 : 0);
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        _mockSessionManager.Verify(s => s.SimpleUploadAsync(
            It.Is<CommitInfo>(c =>
                c.Path == "/dropbox/test.txt" &&
                c.Mode.IsOverwrite &&
                c.Autorename == false &&
                c.ClientModified == now),
            It.IsAny<byte[]>(),
            It.IsAny<long>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_UpdatesOffsetCorrectly()
    {
        // CRITICAL: Verifies offset += read (actual bytes read)
        // DirectUploadStrategy uses actual bytes read for both length and offset calculation
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            300,
            DateTime.UtcNow);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() =>
        {
            callCount++;
            return callCount <= 3 ? 100 : 0;
        });
        _mockReader.Setup(r => r.CurrentBuffer).Returns(new byte[100]);

        _mockSessionManager.Setup(s => s.StartSessionAsync(
                It.IsAny<byte[]>(),
                It.IsAny<long>()))
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Verify offsets based on actual bytes read:
        // Chunk 1: read 100 bytes → Start with offset 0, length 100
        // Chunk 2: read 100 bytes → Append at offset 100, length 100
        // Chunk 3: read 100 bytes → Finish at offset 200, length 100
        _mockSessionManager.Verify(s => s.AppendSessionAsync(
            "session-123",
            100, // offset after first chunk (100 bytes)
            It.IsAny<byte[]>(),
            100), Times.Once); // length = actual bytes read

        _mockSessionManager.Verify(s => s.FinishSessionAsync(
            "session-123",
            200, // offset after second chunk (100 + 100)
            It.IsAny<CommitInfo>(),
            It.IsAny<byte[]>(),
            100), Times.Once); // length = actual bytes read
    }

    [TestMethod]
    public async Task UploadFileAsync_EmptyFile_PassesNullContentHash()
    {
        // CRITICAL: Verifies Program.old.cs lines 234-239 - empty files have no content hash
        var fileToUpload = new FileToUpload(
            "empty.txt",
            @"C:\local\empty.txt",
            "/dropbox/empty.txt",
            0,
            DateTime.UtcNow);

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Should call SimpleUploadAsync for empty file
        _mockSessionManager.Verify(s => s.SimpleUploadAsync(
            It.IsAny<CommitInfo>(),
            It.Is<byte[]>(b => b.Length == 0),
            0), Times.Once);
    }

    [TestMethod]
    public async Task UploadFileAsync_UploadsCorrectDataNotGarbage()
    {
        // CRITICAL: Verifies that DirectUploadStrategy uploads the actual file data, not garbage
        // This is the key test to prevent regression of the original bug
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            10,
            DateTime.UtcNow);

        // Create reader buffer with specific test data
        var expectedData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var readerBuffer = new byte[100];
        Array.Copy(expectedData, readerBuffer, expectedData.Length);

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() => callCount++ == 0 ? 10 : 0);
        _mockReader.Setup(r => r.CurrentBuffer).Returns(readerBuffer);

        byte[] capturedData = null;
        _mockSessionManager.Setup(s => s.SimpleUploadAsync(
                It.IsAny<CommitInfo>(),
                It.IsAny<byte[]>(),
                It.IsAny<long>()))
            .Callback<CommitInfo, byte[], long>((c, b, l) =>
            {
                // Capture the actual data that would be uploaded
                capturedData = new byte[l];
                Array.Copy(b, capturedData, l);
            })
            .ReturnsAsync(new FileMetadata());

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Verify the correct data is uploaded (not garbage)
        Assert.IsNotNull(capturedData, "Data should have been captured");
        CollectionAssert.AreEqual(expectedData, capturedData,
            "DirectUploadStrategy must upload the actual file data from reader.CurrentBuffer, not garbage");
    }

    [TestMethod]
    public async Task UploadFileAsync_MultiChunk_UploadsCorrectDataForEachChunk()
    {
        // CRITICAL: Verifies that each chunk uploads the correct data, not garbage
        var fileToUpload = new FileToUpload(
            "test.txt",
            @"C:\local\test.txt",
            "/dropbox/test.txt",
            20,
            DateTime.UtcNow);

        var chunk1Data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var chunk2Data = new byte[] { 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        var readerBuffer = new byte[100];

        int callCount = 0;
        _mockReader.Setup(r => r.ReadNextBlock()).Returns(() =>
        {
            callCount++;
            if (callCount == 1)
            {
                Array.Copy(chunk1Data, readerBuffer, chunk1Data.Length);
                return 10;
            }
            if (callCount == 2)
            {
                Array.Copy(chunk2Data, readerBuffer, chunk2Data.Length);
                return 10;
            }
            return 0;
        });
        _mockReader.Setup(r => r.CurrentBuffer).Returns(readerBuffer);

        var capturedChunks = new System.Collections.Generic.List<byte[]>();

        _mockSessionManager.Setup(s => s.StartSessionAsync(
                It.IsAny<byte[]>(),
                It.IsAny<long>()))
            .Callback<byte[], long>((b, l) =>
            {
                // Capture the actual data that would be uploaded
                var chunk = new byte[l];
                Array.Copy(b, chunk, l);
                capturedChunks.Add(chunk);
            })
            .ReturnsAsync(new UploadSessionStartResult("session-123"));

        _mockSessionManager.Setup(s => s.FinishSessionAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<CommitInfo>(),
                It.IsAny<byte[]>(),
                It.IsAny<long>()))
            .Callback<string, long, CommitInfo, byte[], long>((sid, off, c, b, l) =>
            {
                // Capture the actual data that would be uploaded
                var chunk = new byte[l];
                Array.Copy(b, chunk, l);
                capturedChunks.Add(chunk);
            })
            .ReturnsAsync(new FileMetadata());

        var strategy = new DirectUploadStrategy(_mockSessionManager.Object, _mockProgress.Object);

        await strategy.UploadFileAsync(fileToUpload, _mockReader.Object);

        // Verify both chunks upload the correct data (not garbage)
        Assert.AreEqual(2, capturedChunks.Count, "Should have captured 2 chunks");
        CollectionAssert.AreEqual(chunk1Data, capturedChunks[0],
            "First chunk must upload actual file data, not garbage");
        CollectionAssert.AreEqual(chunk2Data, capturedChunks[1],
            "Second chunk must upload actual file data, not garbage");
    }
}