using System;
using System.Reflection;
using ICSharpCode.SharpZipLib.Zip;

namespace DropboxEncrypedUploader.Infrastructure;

/// <summary>
/// Helper class to manage deterministic ZIP encryption salts for resumable uploads.
/// </summary>
public static class ZipEncryptionHelper
{
    /// <summary>
    /// Replaces SharpZipLib's static random number generator with a deterministic one
    /// seeded from the provided salt. This ensures that PutNextEntry will generate
    /// the same salt when called.
    ///
    /// IMPORTANT: Must be called BEFORE PutNextEntry. The replacement is global and
    /// affects all ZipOutputStream instances until restored.
    /// </summary>
    /// <param name="salt">16-byte salt to use for deterministic generation</param>
    /// <returns>True if successfully replaced, false otherwise</returns>
    public static bool SetDeterministicSaltGenerator(byte[] salt)
    {
        try
        {
            if (salt == null || salt.Length != Configuration.Configuration.AES_SALT_SIZE)
                return false;

            // Find the static _aesRnd field in ZipOutputStream
            var aesRndField = typeof(ZipOutputStream)
                .GetField("_aesRnd", BindingFlags.NonPublic | BindingFlags.Static);

            if (aesRndField == null)
                return false;

            // Create a deterministic RNG that always returns our salt
            var deterministicRng = new DeterministicRandomNumberGenerator(salt);
            aesRndField.SetValue(null, deterministicRng);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restores SharpZipLib's random number generator to use true randomness.
    /// Call this after creating the ZIP entry to allow other operations to use secure randomness.
    /// </summary>
    public static void RestoreRandomSaltGenerator()
    {
        try
        {
            var aesRndField = typeof(ZipOutputStream)
                .GetField("_aesRnd", BindingFlags.NonPublic | BindingFlags.Static);

            if (aesRndField != null)
            {
                aesRndField.SetValue(null, System.Security.Cryptography.RandomNumberGenerator.Create());
            }
        }
        catch
        {
            // Ignore errors
        }
    }

    /// <summary>
    /// Deterministic RandomNumberGenerator that always returns a specific byte sequence.
    /// Used temporarily to make SharpZipLib generate a specific salt for resumable uploads.
    /// Includes safety checks to ensure it's only used once and for the expected size.
    /// </summary>
    private class DeterministicRandomNumberGenerator : System.Security.Cryptography.RandomNumberGenerator
    {
        private readonly byte[] _fixedBytes;
        private bool _hasBeenUsed;

        public DeterministicRandomNumberGenerator(byte[] fixedBytes)
        {
            _fixedBytes = fixedBytes ?? throw new ArgumentNullException(nameof(fixedBytes));
            if (_fixedBytes.Length != Configuration.Configuration.AES_SALT_SIZE)
                throw new ArgumentException($"Salt must be exactly {Configuration.Configuration.AES_SALT_SIZE} bytes", nameof(fixedBytes));

            _hasBeenUsed = false;
        }

        public override void GetBytes(byte[] data)
        {
            ValidateUsage(data?.Length ?? 0);
            Array.Copy(_fixedBytes, 0, data, 0, _fixedBytes.Length);
        }

        public override void GetBytes(byte[] data, int offset, int count)
        {
            ValidateUsage(count);
            Array.Copy(_fixedBytes, 0, data, offset, _fixedBytes.Length);
        }

        private void ValidateUsage(int requestedSize)
        {
            // Check if already used - this generator should only be called once per PutNextEntry
            if (_hasBeenUsed)
            {
                throw new InvalidOperationException(
                    "DeterministicRandomNumberGenerator was called more than once. " +
                    "This should only be used for a single PutNextEntry call.");
            }

            // Verify the requested size matches the expected salt size
            if (requestedSize != Configuration.Configuration.AES_SALT_SIZE)
            {
                throw new InvalidOperationException(
                    $"Unexpected random bytes request: expected {Configuration.Configuration.AES_SALT_SIZE} bytes for AES-256 salt, " +
                    $"but got request for {requestedSize} bytes. " +
                    "This may indicate SharpZipLib behavior has changed.");
            }

            _hasBeenUsed = true;
        }
    }
}
