using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropboxEncrypedUploader.Tests;

/// <summary>
/// Tests for DropboxContentHasher hash computation.
/// Verifies that the Dropbox content hash helper works correctly.
/// </summary>
[TestClass]
public class DropboxContentHasherTests
{
    [TestMethod]
    public void ComputeContentHash_ValidBuffer_ReturnsHash()
    {
        // Verifies Program.old.cs lines 366-372
        var buffer = new byte[] { 1, 2, 3, 4, 5 };

        var hash = DropboxContentHasher.ComputeHash(buffer, 5);

        Assert.IsNotNull(hash);
        Assert.IsTrue(hash.Length > 0);
    }

    [TestMethod]
    public void ComputeContentHash_SameData_ReturnsSameHash()
    {
        // Verifies deterministic hash computation
        var buffer1 = new byte[] { 1, 2, 3, 4, 5 };
        var buffer2 = new byte[] { 1, 2, 3, 4, 5 };

        var hash1 = DropboxContentHasher.ComputeHash(buffer1, 5);
        var hash2 = DropboxContentHasher.ComputeHash(buffer2, 5);

        Assert.AreEqual(hash1, hash2);
    }

    [TestMethod]
    public void ComputeContentHash_DifferentData_ReturnsDifferentHash()
    {
        // Verifies hash changes with data
        var buffer1 = new byte[] { 1, 2, 3, 4, 5 };
        var buffer2 = new byte[] { 1, 2, 3, 4, 6 };

        var hash1 = DropboxContentHasher.ComputeHash(buffer1, 5);
        var hash2 = DropboxContentHasher.ComputeHash(buffer2, 5);

        Assert.AreNotEqual(hash1, hash2);
    }

    [TestMethod]
    public void ComputeContentHash_EmptyBuffer_ReturnsHash()
    {
        // Verifies Program.old.cs line 371 - Array.Empty<byte>() in TransformFinalBlock
        var buffer = Array.Empty<byte>();

        var hash = DropboxContentHasher.ComputeHash(buffer, 0);

        Assert.IsNotNull(hash);
    }
}
