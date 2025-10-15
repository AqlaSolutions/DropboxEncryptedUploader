using System.Threading.Tasks;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Services
{
    /// <summary>
    /// Analyzes local and remote directories to determine sync operations needed.
    /// </summary>
    public interface ISyncService
    {
        /// <summary>
        /// Analyzes local and remote directories and returns what needs to be synced.
        /// </summary>
        Task<SyncResult> AnalyzeSyncAsync();
    }
}
