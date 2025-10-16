using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Dropbox.Api;

namespace DropboxEncrypedUploader.Configuration;

/// <summary>
/// Contains all configuration settings for the Dropbox encrypted uploader.
/// Validates and normalizes input parameters.
/// </summary>
public class Configuration
{
    // Input parameters
    public string Token { get; }
    public string LocalDirectory { get; }
    public string DropboxDirectory { get; }
    public string Password { get; }

    // Derived settings
    public bool UseEncryption => !string.IsNullOrEmpty(Password);
    public string RemoteFileExtension => UseEncryption ? ".zip" : "";

    // Buffer sizes
    public int ReadBufferSize { get; } = 90_000_000; // 90MB
    public int MaxBufferAllocation { get; } = 99_000_000; // 99MB

    // Timeouts
    public TimeSpan HttpTimeout { get; } = TimeSpan.FromMinutes(5);
    public TimeSpan LongPollTimeout { get; } = TimeSpan.FromMinutes(10);

    // API limits
    public int ListFolderLimit { get; } = 2000;

    // Batch settings
    public ulong DeletingBatchSize { get; } = 32UL * 1024 * 1024 * 1024; // 32GB

    // Retry settings
    public int MaxRetries { get; } = 10;

    // Storage recycling settings
    public int MinRecycleAgeDays { get; } = 15;
    public int MaxRecycleAgeDays { get; } = 29;

    // Sync settings
    public double TimestampToleranceSeconds { get; } = 1.0;

    // Session persistence settings
    public string SessionFilePath { get; private set; }
    private const int SessionFileRetentionDays = 5;

    // Encryption settings
    public const int AES_SALT_SIZE = 16; // AES-256 encryption uses 16-byte salt

    /// <summary>
    /// Creates and validates a new Configuration instance from command-line arguments.
    /// </summary>
    /// <param name="args">Command-line arguments: [token, localPath, dropboxFolder, password]</param>
    /// <exception cref="ArgumentException">Thrown when arguments are invalid</exception>
    public Configuration(string[] args)
    {
        if (args == null || args.Length < 4)
            throw new ArgumentException("Usage: DropboxEncrypedUploader <token> <localPath> <dropboxFolder> <password>");

        Token = args[0];
        if (string.IsNullOrWhiteSpace(Token))
            throw new ArgumentException("Token cannot be empty");

        // Normalize local directory path
        LocalDirectory = Path.GetFullPath(args[1]);
        if (!IsEndingWithSeparator(LocalDirectory))
            LocalDirectory += Path.DirectorySeparatorChar;

        // Normalize Dropbox directory path
        DropboxDirectory = args[2];
        if (!IsEndingWithSeparator(DropboxDirectory))
            DropboxDirectory += Path.AltDirectorySeparatorChar;

        Password = args[3];

        // Generate session file path in AppData with directory hash
        SessionFilePath = GenerateSessionFilePath(LocalDirectory);
    }

    private static bool IsEndingWithSeparator(string s)
    {
        return s.Length != 0 &&
               (s[s.Length - 1] == Path.DirectorySeparatorChar ||
                s[s.Length - 1] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Generates session file path in AppData with directory-specific hash.
    /// This allows concurrent runs for different directories while preventing
    /// conflicts for the same directory.
    /// </summary>
    private static string GenerateSessionFilePath(string localDirectory)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "DropboxEncryptedUploader");

        // Create app folder if it doesn't exist
        if (!Directory.Exists(appFolder))
        {
            Directory.CreateDirectory(appFolder);
        }

        // Compute hash of lowercase directory path
        using (var sha256 = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(localDirectory.ToLowerInvariant());
            var hashBytes = sha256.ComputeHash(bytes);
            var hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 32);

            return Path.Combine(appFolder, $"session-{hash}.json");
        }
    }

    /// <summary>
    /// Cleans up session files older than 5 days from AppData.
    /// Called on application startup to prevent accumulation of stale sessions.
    /// </summary>
    public static void CleanupOldSessions()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "DropboxEncryptedUploader");

        if (!Directory.Exists(appFolder))
            return;

        try
        {
            var now = DateTime.UtcNow;
            var sessionFiles = Directory.GetFiles(appFolder, "session-*.json");

            foreach (var file in sessionFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if ((now - fileInfo.LastWriteTimeUtc).TotalDays > SessionFileRetentionDays)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignore errors for individual files (might be locked or deleted)
                }
            }
        }
        catch
        {
            // Ignore errors during cleanup - not critical
        }
    }
}