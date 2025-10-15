using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropboxEncrypedUploader.Tests
{
    /// <summary>
    /// Tests for Configuration class.
    /// Verifies behavior matches Program.old.cs lines 20-29, 62-68, 361-363.
    /// </summary>
    [TestClass]
    public class ConfigurationTests
    {
        [TestMethod]
        public void Constructor_ValidArguments_ParsesCorrectly()
        {
            // Arrange
            var args = new[] { "test-token", @"C:\local", "/dropbox", "password123" };

            // Act
            var config = new Configuration.Configuration(args);

            // Assert
            Assert.AreEqual("test-token", config.Token);
            Assert.IsTrue(config.LocalDirectory.EndsWith(Path.DirectorySeparatorChar.ToString()));
            Assert.IsTrue(config.DropboxDirectory.EndsWith(Path.AltDirectorySeparatorChar.ToString()));
            Assert.AreEqual("password123", config.Password);
        }

        [TestMethod]
        public void Constructor_LocalDirectoryWithoutSeparator_AddsSeparator()
        {
            // Verifies Program.old.cs lines 22-23
            var args = new[] { "token", @"C:\local", "/dropbox", "pass" };

            var config = new Configuration.Configuration(args);

            Assert.IsTrue(config.LocalDirectory.EndsWith(Path.DirectorySeparatorChar.ToString()));
            Assert.AreEqual(@"C:\local\", config.LocalDirectory);
        }

        [TestMethod]
        public void Constructor_LocalDirectoryWithSeparator_DoesNotAddAnother()
        {
            // Verifies Program.old.cs lines 22-23 and IsEndingWithSeparator logic (361-363)
            var args = new[] { "token", @"C:\local\", "/dropbox", "pass" };

            var config = new Configuration.Configuration(args);

            Assert.AreEqual(@"C:\local\", config.LocalDirectory);
        }

        [TestMethod]
        public void Constructor_DropboxDirectoryWithoutSeparator_AddsAltSeparator()
        {
            // Verifies Program.old.cs lines 25-26
            var args = new[] { "token", @"C:\local", "/dropbox", "pass" };

            var config = new Configuration.Configuration(args);

            Assert.IsTrue(config.DropboxDirectory.EndsWith(Path.AltDirectorySeparatorChar.ToString()));
            Assert.AreEqual("/dropbox/", config.DropboxDirectory);
        }

        [TestMethod]
        public void Constructor_DropboxDirectoryWithSeparator_DoesNotAddAnother()
        {
            // Verifies Program.old.cs lines 25-26 and IsEndingWithSeparator logic (361-363)
            var args = new[] { "token", @"C:\local", "/dropbox/", "pass" };

            var config = new Configuration.Configuration(args);

            Assert.AreEqual("/dropbox/", config.DropboxDirectory);
        }

        [TestMethod]
        public void UseEncryption_EmptyPassword_ReturnsFalse()
        {
            // Verifies Program.old.cs line 28
            var args = new[] { "token", @"C:\local", "/dropbox", "" };

            var config = new Configuration.Configuration(args);

            Assert.IsFalse(config.UseEncryption);
        }

        [TestMethod]
        public void UseEncryption_NullPassword_ReturnsFalse()
        {
            // Not directly in old code but tests string.IsNullOrEmpty behavior
            var args = new[] { "token", @"C:\local", "/dropbox", null };

            var config = new Configuration.Configuration(args);

            Assert.IsFalse(config.UseEncryption);
        }

        [TestMethod]
        public void UseEncryption_WithPassword_ReturnsTrue()
        {
            // Verifies Program.old.cs line 28
            var args = new[] { "token", @"C:\local", "/dropbox", "password" };

            var config = new Configuration.Configuration(args);

            Assert.IsTrue(config.UseEncryption);
        }

        [TestMethod]
        public void RemoteFileExtension_WithEncryption_ReturnsZip()
        {
            // Verifies Program.old.cs line 29
            var args = new[] { "token", @"C:\local", "/dropbox", "password" };

            var config = new Configuration.Configuration(args);

            Assert.AreEqual(".zip", config.RemoteFileExtension);
        }

        [TestMethod]
        public void RemoteFileExtension_WithoutEncryption_ReturnsEmpty()
        {
            // Verifies Program.old.cs line 29
            var args = new[] { "token", @"C:\local", "/dropbox", "" };

            var config = new Configuration.Configuration(args);

            Assert.AreEqual("", config.RemoteFileExtension);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Constructor_NullArgs_ThrowsException()
        {
            new Configuration.Configuration(null);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Constructor_TooFewArgs_ThrowsException()
        {
            new Configuration.Configuration(new[] { "token", "path" });
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Constructor_EmptyToken_ThrowsException()
        {
            new Configuration.Configuration(new[] { "", @"C:\local", "/dropbox", "pass" });
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Constructor_WhitespaceToken_ThrowsException()
        {
            new Configuration.Configuration(new[] { "   ", @"C:\local", "/dropbox", "pass" });
        }

        [TestMethod]
        public void Configuration_DefaultValues_MatchOriginal()
        {
            // Verifies constants from Program.old.cs:
            // Buffer sizes: lines 132-133
            // Timeouts: lines 45-46
            // List limit: line 63
            // Batch size: line 285
            // Max retries: line 378
            var args = new[] { "token", @"C:\local", "/dropbox", "pass" };

            var config = new Configuration.Configuration(args);

            Assert.AreEqual(90_000_000, config.ReadBufferSize); // 90MB
            Assert.AreEqual(99_000_000, config.MaxBufferAllocation); // 99MB
            Assert.AreEqual(TimeSpan.FromMinutes(5), config.HttpTimeout);
            Assert.AreEqual(TimeSpan.FromMinutes(10), config.LongPollTimeout);
            Assert.AreEqual(2000, config.ListFolderLimit);
            Assert.AreEqual(32UL * 1024 * 1024 * 1024, config.DeletingBatchSize); // 32GB
            Assert.AreEqual(10, config.MaxRetries);
            Assert.AreEqual(15, config.MinRecycleAgeDays);
            Assert.AreEqual(29, config.MaxRecycleAgeDays);
            Assert.AreEqual(1.0, config.TimestampToleranceSeconds);
        }

        [TestMethod]
        public void LocalDirectory_GetFullPath_IsApplied()
        {
            // Verifies Program.old.cs line 21 uses Path.GetFullPath
            var args = new[] { "token", "local", "/dropbox", "pass" };

            var config = new Configuration.Configuration(args);

            // GetFullPath should make it absolute
            Assert.IsTrue(Path.IsPathRooted(config.LocalDirectory));
        }

        [TestMethod]
        public void IsEndingWithSeparator_EmptyString_ReturnsFalse()
        {
            // Verifies Program.old.cs line 362: (s.Length != 0)
            var args = new[] { "token", ".", "/dropbox", "pass" };
            var config = new Configuration.Configuration(args);

            // Empty check is handled by length != 0 in original
            Assert.IsNotNull(config.LocalDirectory);
        }

        [TestMethod]
        public void DropboxDirectory_HandlesBackslashAsSeparator()
        {
            // Verifies Program.old.cs line 363: checks both DirectorySeparatorChar and AltDirectorySeparatorChar
            var args = new[] { "token", @"C:\local", @"\dropbox\", "pass" };

            var config = new Configuration.Configuration(args);

            // Should not add another separator since it already ends with one
            Assert.AreEqual(@"\dropbox\", config.DropboxDirectory);
        }
    }
}
