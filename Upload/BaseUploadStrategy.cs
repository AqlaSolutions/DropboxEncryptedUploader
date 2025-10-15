using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Base class for upload strategies containing common functionality.
/// </summary>
public abstract class BaseUploadStrategy
{
    protected readonly IUploadSessionManager SessionManager;

    protected BaseUploadStrategy(IUploadSessionManager sessionManager)
    {
        SessionManager = sessionManager;
    }

    /// <summary>
    /// Uploads a chunk of data, either starting a new session or appending to existing one.
    /// </summary>
    protected async Task<UploadSessionStartResult> UploadChunkAsync(
        UploadSessionStartResult session,
        long offset,
        byte[] buffer,
        long length)
    {
        if (session == null)
        {
            return await SessionManager.StartSessionAsync(buffer, length);
        }

        await SessionManager.AppendSessionAsync(session.SessionId, offset, buffer, length);
        return session;
    }

    /// <summary>
    /// Finishes the upload, either with a simple upload or session finish.
    /// </summary>
    protected async Task FinishUploadAsync(
        UploadSessionStartResult session,
        long offset,
        CommitInfo commitInfo,
        byte[] buffer,
        long length)
    {
        if (session == null)
        {
            await SessionManager.SimpleUploadAsync(commitInfo, buffer, length);
        }
        else
        {
            await SessionManager.FinishSessionAsync(session.SessionId, offset, commitInfo, buffer, length);
        }
    }

    /// <summary>
    /// Creates commit info for a file upload.
    /// </summary>
    protected CommitInfo CreateCommitInfo(FileToUpload fileToUpload)
    {
        return new CommitInfo(
            fileToUpload.RemotePath,
            WriteMode.Overwrite.Instance,
            autorename: false,
            clientModified: fileToUpload.ClientModified);
    }
}
