using System.Threading.Tasks;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Upload
{
    /// <summary>
    /// Strategy for uploading files to Dropbox.
    /// </summary>
    public interface IUploadStrategy
    {
        /// <summary>
        /// Uploads a file to Dropbox.
        /// </summary>
        /// <param name="fileToUpload">File information</param>
        /// <param name="reader">Multi-file reader (already configured with NextFile)</param>
        /// <param name="buffer">Reusable buffer for uploads</param>
        /// <param name="nextFilePathForPreOpen">Optional path to next file for pre-opening optimization</param>
        Task UploadFileAsync(FileToUpload fileToUpload, AsyncMultiFileReader reader, byte[] buffer, string nextFilePathForPreOpen);
    }
}
