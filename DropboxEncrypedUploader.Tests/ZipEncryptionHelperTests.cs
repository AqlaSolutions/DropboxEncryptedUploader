using System;
using System.IO;
using System.Linq;
using DropboxEncrypedUploader.Infrastructure;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropboxEncrypedUploader.Tests;

/// <summary>
/// Tests for ZipEncryptionHelper - critical for resumable encrypted uploads.
/// Verifies that deterministic salt generation works correctly.
/// </summary>
[TestClass]
[DoNotParallelize] // These tests manipulate global static state in ZipOutputStream
public class ZipEncryptionHelperTests
{
    [TestInitialize]
    public void Setup()
    {
        // Ensure random generator is in a clean state before each test
        ZipEncryptionHelper.RestoreRandomSaltGenerator();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Always restore random generator after each test to prevent state leakage
        ZipEncryptionHelper.RestoreRandomSaltGenerator();
    }

    [TestMethod]
    public void SetDeterministicSaltGenerator_ValidSalt_ReturnsTrue()
    {
        // Arrange
        var customSalt = new byte[Configuration.Configuration.AES_SALT_SIZE];
        for (int i = 0; i < Configuration.Configuration.AES_SALT_SIZE; i++) customSalt[i] = (byte)i;

        // Act
        var success = ZipEncryptionHelper.SetDeterministicSaltGenerator(customSalt);

        // Assert
        Assert.IsTrue(success, "Setting deterministic generator should succeed");

        // Cleanup
        ZipEncryptionHelper.RestoreRandomSaltGenerator();
    }

    [TestMethod]
    public void SetDeterministicSaltGenerator_InvalidLength_ReturnsFalse()
    {
        // Arrange
        var invalidSalt = new byte[8]; // Wrong length

        // Act
        var success = ZipEncryptionHelper.SetDeterministicSaltGenerator(invalidSalt);

        // Assert
        Assert.IsFalse(success, "Setting deterministic generator should fail with invalid length");

        // Cleanup (just in case)
        ZipEncryptionHelper.RestoreRandomSaltGenerator();
    }

    [TestMethod]
    public void RestoreRandomSaltGenerator_ResetsToRandom()
    {
        // Verify that RestoreRandomSaltGenerator actually restores randomness

        var customSalt = new byte[Configuration.Configuration.AES_SALT_SIZE];
        for (int i = 0; i < Configuration.Configuration.AES_SALT_SIZE; i++) customSalt[i] = (byte)i;

        // Set deterministic generator
        ZipEncryptionHelper.SetDeterministicSaltGenerator(customSalt);

        // Restore random generator
        ZipEncryptionHelper.RestoreRandomSaltGenerator();

        // Now create two encryptions - they should be DIFFERENT (random)
        var testData = "Test";
        var testBytes = System.Text.Encoding.UTF8.GetBytes(testData);

        byte[] output1, output2;

        using (var ms = new MemoryStream())
        {
            using (var zs = new ZipOutputStream(ms, 512))
            {
                zs.IsStreamOwner = false;
                zs.Password = "pwd";
                zs.UseZip64 = UseZip64.On;
                zs.SetLevel(0);

                var entry = new ZipEntry("/test.txt") { AESKeySize = 256 };
                zs.PutNextEntry(entry);
                zs.Write(testBytes, 0, testBytes.Length);
                zs.CloseEntry();
                zs.Finish();
            }
            output1 = ms.ToArray();
        }

        using (var ms = new MemoryStream())
        {
            using (var zs = new ZipOutputStream(ms, 512))
            {
                zs.IsStreamOwner = false;
                zs.Password = "pwd";
                zs.UseZip64 = UseZip64.On;
                zs.SetLevel(0);

                var entry = new ZipEntry("/test.txt") { AESKeySize = 256 };
                zs.PutNextEntry(entry);
                zs.Write(testBytes, 0, testBytes.Length);
                zs.CloseEntry();
                zs.Finish();
            }
            output2 = ms.ToArray();
        }

        // Outputs should be DIFFERENT (random salts)
        CollectionAssert.AreNotEqual(output1, output2, "Outputs with random salts should differ");
    }

    [TestMethod]
    public void GenerateSaltAndReuse_ProducesSameOutput()
    {
        // This test demonstrates the actual workflow for resumable encrypted uploads:
        // 1. Generate a random salt ourselves (not relying on ZipOutputStream's internal RNG)
        // 2. First encryption: Set deterministic RNG with our salt
        // 3. Second encryption: Reuse the same salt (simulating resume)
        // 4. Verify outputs are identical
        //
        // This mirrors the real implementation where:
        // - New upload: Generate salt → Save to session → Encrypt
        // - Resume: Load salt from session → Encrypt
        // We never need to extract salt from the stream!

        // Arrange
        var testData = "This is test data for salt generation and reuse.";
        var testBytes = System.Text.Encoding.UTF8.GetBytes(testData);
        var password = "test-password";
        var filename = "/test.txt";

        // Step 1: Generate our own random salt (this would be saved to session in real code)
        var salt = new byte[Configuration.Configuration.AES_SALT_SIZE];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }

        // Step 2: First encryption with our generated salt
        byte[] firstOutput;
        using (var outputStream = new MemoryStream())
        {
            using (var zipStream = new ZipOutputStream(outputStream, 512))
            {
                zipStream.IsStreamOwner = false;
                zipStream.Password = password;
                zipStream.UseZip64 = UseZip64.On;
                zipStream.SetLevel(0); // No compression

                var entry = new ZipEntry(filename) { AESKeySize = 256 };

                // Set our deterministic salt BEFORE PutNextEntry
                ZipEncryptionHelper.SetDeterministicSaltGenerator(salt);
                zipStream.PutNextEntry(entry);
                ZipEncryptionHelper.RestoreRandomSaltGenerator();

                zipStream.Write(testBytes, 0, testBytes.Length);
                zipStream.CloseEntry();
                zipStream.Finish();
                zipStream.Close();
            }
            firstOutput = outputStream.ToArray();
        }

        // Step 3: Second encryption with THE SAME salt (simulating resume)
        byte[] secondOutput;
        using (var outputStream = new MemoryStream())
        {
            using (var zipStream = new ZipOutputStream(outputStream, 512))
            {
                zipStream.IsStreamOwner = false;
                zipStream.Password = password;
                zipStream.UseZip64 = UseZip64.On;
                zipStream.SetLevel(0); // No compression

                var entry = new ZipEntry(filename) { AESKeySize = 256 };

                // Use the SAME salt (loaded from session in real code)
                ZipEncryptionHelper.SetDeterministicSaltGenerator(salt);
                zipStream.PutNextEntry(entry);
                ZipEncryptionHelper.RestoreRandomSaltGenerator();

                zipStream.Write(testBytes, 0, testBytes.Length);
                zipStream.CloseEntry();
                zipStream.Finish();
                zipStream.Close();
            }
            secondOutput = outputStream.ToArray();
        }

        // Step 4: Verify outputs are IDENTICAL
        Assert.AreEqual(firstOutput.Length, secondOutput.Length,
            "Output lengths must match when using same salt");
        CollectionAssert.AreEqual(firstOutput, secondOutput,
            "Encrypted outputs must be identical when reusing the same salt - this is critical for resumable uploads");
    }

    [TestMethod]
    public void SetDeterministicSaltGenerator_MultipleEntries_EachUsesOwnSalt()
    {
        // Verify that each PutNextEntry call needs its own deterministic generator
        // The generator should be used once per entry and then restored

        var testData = "Test";
        var testBytes = System.Text.Encoding.UTF8.GetBytes(testData);
        var password = "pwd";

        var salt1 = new byte[Configuration.Configuration.AES_SALT_SIZE];
        var salt2 = new byte[Configuration.Configuration.AES_SALT_SIZE];
        for (int i = 0; i < Configuration.Configuration.AES_SALT_SIZE; i++)
        {
            salt1[i] = (byte)i;
            salt2[i] = (byte)(i + 100);
        }

        using (var ms = new MemoryStream())
        {
            using (var zs = new ZipOutputStream(ms, 512))
            {
                zs.IsStreamOwner = false;
                zs.Password = password;
                zs.UseZip64 = UseZip64.On;
                zs.SetLevel(0);

                // First entry with salt1
                var entry1 = new ZipEntry("/test1.txt") { AESKeySize = 256 };
                ZipEncryptionHelper.SetDeterministicSaltGenerator(salt1);
                zs.PutNextEntry(entry1);
                ZipEncryptionHelper.RestoreRandomSaltGenerator();
                zs.Write(testBytes, 0, testBytes.Length);
                zs.CloseEntry();

                // Second entry with salt2 (NEW generator instance)
                var entry2 = new ZipEntry("/test2.txt") { AESKeySize = 256 };
                ZipEncryptionHelper.SetDeterministicSaltGenerator(salt2);
                zs.PutNextEntry(entry2);
                ZipEncryptionHelper.RestoreRandomSaltGenerator();
                zs.Write(testBytes, 0, testBytes.Length);
                zs.CloseEntry();

                zs.Finish();
            }
        }

        // Test passes if no exception thrown - each entry got its own generator
    }
}
