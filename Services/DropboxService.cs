using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Configuration;

namespace DropboxEncrypedUploader.Services
{
    /// <summary>
    /// Implementation of Dropbox operations.
    /// </summary>
    public class DropboxService : IDropboxService, IDisposable
    {
        private readonly DropboxClient _client;
        private readonly int _listFolderLimit;

        public DropboxClient Client => _client;

        public DropboxService(Configuration.Configuration config)
        {
            var clientConfig = new DropboxClientConfig()
            {
                HttpClient = new HttpClient() { Timeout = config.HttpTimeout },
                LongPollHttpClient = new HttpClient() { Timeout = config.LongPollTimeout }
            };
            _client = new DropboxClient(config.Token, clientConfig);
            _listFolderLimit = config.ListFolderLimit;
        }

        public async Task CreateFolderAsync(string path)
        {
            try
            {
                await _client.Files.CreateFolderV2Async(path.TrimEnd('/'));
            }
            catch
            {
                // Ignore errors - folder may already exist
            }
        }

        public async Task<List<Metadata>> ListAllFilesAsync(string path, bool includeDeleted = false)
        {
            var result = new List<Metadata>();

            ListFolderResult list = await _client.Files.ListFolderAsync(
                path.TrimEnd('/'),
                recursive: true,
                limit: (uint)_listFolderLimit,
                includeDeleted: includeDeleted);

            while (list != null)
            {
                result.AddRange(list.Entries);

                list = list.HasMore
                    ? await _client.Files.ListFolderContinueAsync(list.Cursor)
                    : null;
            }

            return result;
        }

        public async Task DeleteBatchAsync(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();
            if (pathList.Count == 0)
                return;

            var deleteJob = await _client.Files.DeleteBatchAsync(
                pathList.Select(x => new DeleteArg(x)));

            if (deleteJob.IsAsyncJobId)
            {
                DeleteBatchJobStatus status;
                do
                {
                    Thread.Sleep(5000);
                    status = await _client.Files.DeleteBatchCheckAsync(deleteJob.AsAsyncJobId.Value);
                }
                while (status.IsInProgress);
            }
        }

        public async Task<ListRevisionsResult> ListRevisionsAsync(string path, ListRevisionsMode mode, int limit)
        {
            return await _client.Files.ListRevisionsAsync(path, mode, (ulong)limit);
        }

        public async Task<FileMetadata> RestoreAsync(string path, string rev)
        {
            return await _client.Files.RestoreAsync(path, rev);
        }

        public async Task<DeleteResult> DeleteFileAsync(string path, string rev)
        {
            return await _client.Files.DeleteV2Async(path, rev);
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
