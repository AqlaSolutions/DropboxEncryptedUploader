using System.Threading.Tasks;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Strategy for uploading files to Dropbox.
/// </summary>
public interface IUploadStrategy
{
    /// <summary>
    /// Uploads a file to Dropbox.
    /// </summary>
    /// <param name="fileToUpload">File information</param>
    /// <param name="reader">Multi-file reader (already opened and positioned at the file to upload)</param>
    Task UploadFileAsync(FileToUpload fileToUpload, AsyncMultiFileReader reader);
}