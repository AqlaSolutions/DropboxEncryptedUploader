using System;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Exception thrown when resuming an upload session fails.
/// Indicates that the session should be cleared and the upload restarted from the beginning.
/// </summary>
public class ResumeFailedException : Exception
{
    public ResumeFailedException(string message) : base(message) { }

    public ResumeFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}
