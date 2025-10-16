using System;
using System.Collections.Generic;
using System.IO;
using DropboxEncrypedUploader.Models;
using Newtonsoft.Json;

namespace DropboxEncrypedUploader.Services;

/// <summary>
/// JSON file-based implementation of session persistence.
/// Stores a single upload session metadata in a JSON file for resumption after interruptions.
/// Uses synchronous I/O since the application is single-threaded.
/// File is stored in AppData with directory-specific path to allow concurrent runs
/// for different directories.
/// </summary>
public class SessionPersistenceService(string sessionFilePath) : ISessionPersistenceService
{
    private readonly string _sessionFilePath = sessionFilePath ?? throw new ArgumentNullException(nameof(sessionFilePath));

    public UploadSessionMetadata LoadSession()
    {
        if (!File.Exists(_sessionFilePath))
        {
            return null;
        }

        try
        {
            using (var fileStream = new FileStream(_sessionFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(fileStream))
            {
                var json = reader.ReadToEnd();
                return JsonConvert.DeserializeObject<UploadSessionMetadata>(json);
            }
        }
        catch (Exception ex)
        {
            // If file is corrupt or can't be read, start fresh
            Console.Error.WriteLine($"WARNING: Session file {_sessionFilePath} is corrupted or unreadable: {ex.Message}");
            Console.Error.WriteLine("Upload will restart from the beginning.");
            return null;
        }
    }

    public void SaveSession(UploadSessionMetadata metadata)
    {
        if (metadata == null)
            throw new ArgumentNullException(nameof(metadata));

        if (string.IsNullOrEmpty(metadata.SessionId))
            throw new ArgumentException("SessionId cannot be null or empty", nameof(metadata));

        if (string.IsNullOrEmpty(metadata.FilePath))
            throw new ArgumentException("FilePath cannot be null or empty", nameof(metadata));

        try
        {
            var json = JsonConvert.SerializeObject(metadata, Formatting.Indented);

            // Write with exclusive lock
            using (var fileStream = new FileStream(_sessionFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fileStream))
            {
                writer.Write(json);
            }
        }
        catch (Exception ex)
        {
            // Don't crash the app if we can't save session metadata, but warn the user
            Console.Error.WriteLine($"WARNING: Failed to save upload session metadata to {_sessionFilePath}: {ex.Message}");
            Console.Error.WriteLine("Upload will continue, but resume may not work if interrupted.");
        }
    }

    public void DeleteSession()
    {
        if (File.Exists(_sessionFilePath))
        {
            try
            {
                File.Delete(_sessionFilePath);
            }
            catch
            {
                // Ignore errors during deletion
            }
        }
    }
}
