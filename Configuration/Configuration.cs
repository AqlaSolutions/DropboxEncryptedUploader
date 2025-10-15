using System;
using System.IO;

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
    }

    private static bool IsEndingWithSeparator(string s)
    {
        return s.Length != 0 &&
               (s[s.Length - 1] == Path.DirectorySeparatorChar ||
                s[s.Length - 1] == Path.AltDirectorySeparatorChar);
    }
}