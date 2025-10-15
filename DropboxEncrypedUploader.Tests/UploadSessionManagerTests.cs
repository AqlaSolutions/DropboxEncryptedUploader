using DropboxEncrypedUploader.Upload;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropboxEncrypedUploader.Tests
{
    [TestClass]
    public class UploadSessionManagerTests
    {
        [TestMethod]
        public void ComputeContentHash_ValidBuffer_ReturnsHash()
        {
            // Verifies Program.old.cs lines 366-372
            var buffer = new byte[] { 1, 2, 3, 4, 5 };

            var hash = UploadSessionManager.ComputeContentHash(buffer, 5);

            Assert.IsNotNull(hash);
            Assert.IsTrue(hash.Length > 0);
        }

        [TestMethod]
        public void ComputeContentHash_SameData_ReturnsSameHash()
        {
            // Verifies deterministic hash computation
            var buffer1 = new byte[] { 1, 2, 3, 4, 5 };
            var buffer2 = new byte[] { 1, 2, 3, 4, 5 };

            var hash1 = UploadSessionManager.ComputeContentHash(buffer1, 5);
            var hash2 = UploadSessionManager.ComputeContentHash(buffer2, 5);

            Assert.AreEqual(hash1, hash2);
        }

        [TestMethod]
        public void ComputeContentHash_DifferentData_ReturnsDifferentHash()
        {
            // Verifies hash changes with data
            var buffer1 = new byte[] { 1, 2, 3, 4, 5 };
            var buffer2 = new byte[] { 1, 2, 3, 4, 6 };

            var hash1 = UploadSessionManager.ComputeContentHash(buffer1, 5);
            var hash2 = UploadSessionManager.ComputeContentHash(buffer2, 5);

            Assert.AreNotEqual(hash1, hash2);
        }

        [TestMethod]
        public void ComputeContentHash_EmptyBuffer_ReturnsHash()
        {
            // Verifies Program.old.cs line 371 - Array.Empty<byte>() in TransformFinalBlock
            var buffer = new byte[0];

            var hash = UploadSessionManager.ComputeContentHash(buffer, 0);

            Assert.IsNotNull(hash);
        }
    }
}
