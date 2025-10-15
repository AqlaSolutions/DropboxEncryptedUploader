using System.Threading.Tasks;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services
{
    /// <summary>
    /// Implements storage recycling by restoring and re-deleting old files.
    /// This exploits Dropbox retention policies to extend storage.
    /// </summary>
    public interface IStorageRecyclingService
    {
        /// <summary>
        /// Recycles deleted files within the configured age range.
        /// </summary>
        /// <param name="syncResult">Sync result containing existing files and folders</param>
        Task RecycleDeletedFilesAsync(SyncResult syncResult);
    }
}
