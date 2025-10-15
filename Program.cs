using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;
using ICSharpCode.SharpZipLib.Zip;

namespace DropboxEncrypedUploader
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var token = args[0];
                var localDirectory = Path.GetFullPath(args[1]);
                if (!IsEndingWithSeparator(localDirectory))
                    localDirectory += Path.DirectorySeparatorChar;
                var dropboxDirectory = args[2];
                if (!IsEndingWithSeparator(dropboxDirectory))
                    dropboxDirectory += Path.AltDirectorySeparatorChar;
                string password = args[3];
                bool useEncryption = !string.IsNullOrEmpty(password);
                string remoteFileExtension = useEncryption ? ".zip" : "";

                bool localExists = Directory.Exists(localDirectory);
                if (!localExists)
                    Console.WriteLine("Local directory does not exist: " + localDirectory);

                var newFiles = new HashSet<string>(
                    localExists
                        ? Directory.GetFiles(localDirectory, "*", SearchOption.AllDirectories)
                            .Select(f => f.Substring(localDirectory.Length))
                        : [], StringComparer.OrdinalIgnoreCase);

                var filesToDelete = new HashSet<string>();

                var config = new DropboxClientConfig()
                {
                    HttpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) },
                    LongPollHttpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) }
                };
                using (var dropbox = new DropboxClient(token, config))
                {
                    try
                    {
                        await dropbox.Files.CreateFolderV2Async(dropboxDirectory.TrimEnd('/'));
                    }
                    catch
                    {
                    }


                    var existingFiles = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                    var existingFolders = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                    existingFolders.Add("");

                    for (var list = await dropbox.Files.ListFolderAsync(dropboxDirectory.TrimEnd('/'), true, limit: 2000);
                        list != null;
                        list = list.HasMore ? await dropbox.Files.ListFolderContinueAsync(list.Cursor) : null)
                    {
                        foreach (var entry in list.Entries)
                        {
                            if (!entry.IsFile)
                            {
                                if (entry.IsFolder)
                                    existingFolders.Add(entry.AsFolder.PathLower);
                                continue;
                            }

                            existingFiles.Add(entry.PathLower);
                            var relativePath = entry.PathLower.Substring(dropboxDirectory.Length);

                            // When using encryption, strip .zip extension to match original files
                            // When not using encryption, use filename as-is
                            string originalFileName;
                            if (useEncryption)
                            {
                                if (!relativePath.EndsWith(".zip")) continue;
                                originalFileName = relativePath.Substring(0, relativePath.Length - 4).Replace("/", Path.DirectorySeparatorChar + "");
                            }
                            else
                            {
                                originalFileName = relativePath.Replace("/", Path.DirectorySeparatorChar + "");
                            }

                            if (newFiles.Contains(originalFileName))
                            {
                                var info = new FileInfo(Path.Combine(localDirectory, originalFileName));
                                if ((info.LastWriteTimeUtc - entry.AsFile.ClientModified).TotalSeconds < 1f)
                                    newFiles.Remove(originalFileName);
                            }
                            else if (localExists)
                                filesToDelete.Add(entry.PathLower);
                        }
                    }

                    await DeleteFilesBatchAsync();

                    ulong deletingAccumulatedSize = 0;

                    async Task DeleteFilesBatchAsync()
                    {
                        if (filesToDelete.Count > 0)
                        {
                            Console.WriteLine($"Deleting files: \n{string.Join("\n", filesToDelete)}");
                            var j = await dropbox.Files.DeleteBatchAsync(filesToDelete.Select(x => new DeleteArg(x)));
                            if (j.IsAsyncJobId)
                            {

                                for (DeleteBatchJobStatus r = await dropbox.Files.DeleteBatchCheckAsync(j.AsAsyncJobId.Value);
                                    r.IsInProgress;
                                    r = await dropbox.Files.DeleteBatchCheckAsync(j.AsAsyncJobId.Value))
                                {
                                    Thread.Sleep(5000);
                                }
                            }

                            filesToDelete.Clear();
                            deletingAccumulatedSize = 0;
                        }
                    }

                    if (newFiles.Count > 0)
                    {
                        Console.WriteLine($"Uploading files: {newFiles.Count}");
                        byte[] msBuffer = new byte[1000 * 1000 * 99];
                        int bufferSize = 1000 * 1000 * 90;
                        using var reader = new AsyncMultiFileReader(bufferSize, (f, t) => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true));
                        var newFilesList = newFiles.ToList();

                        if (useEncryption)
                        {
                            // Upload with ZIP encryption
                            ZipStrings.UseUnicode = true;
                            ZipStrings.CodePage = 65001;
                            var entryFactory = new ZipEntryFactory();

                            for (int i = 0; i < newFilesList.Count; i++)
                            {
                                var relativePath = newFilesList[i];
                                Console.Write($" {relativePath}");

                                PrepareReader(reader, newFilesList, i, localDirectory);

                                string fullPath = Path.Combine(localDirectory, relativePath);
                                var info = new FileInfo(fullPath);
                                var clientModifiedAt = info.LastWriteTimeUtc;
                                using (var zipWriterUnderlyingStream = new CopyStream())
                                {
                                    var bufferStream = new MemoryStream(msBuffer);
                                    bufferStream.SetLength(0);

                                    UploadSessionStartResult session = null;
                                    long offset = 0;
                                    long totalBytesRead = 0;

                                    using (var zipWriter = new ZipOutputStream(zipWriterUnderlyingStream, bufferSize) { IsStreamOwner = false, Password = password, UseZip64 = UseZip64.On })
                                    {
                                        try
                                        {
                                            zipWriterUnderlyingStream.CopyTo = bufferStream;
                                            zipWriter.SetLevel(0);
                                            var entry = entryFactory.MakeFileEntry(fullPath, '/' + Path.GetFileName(relativePath), true);
                                            entry.AESKeySize = 256;
                                            zipWriter.PutNextEntry(entry);

                                            int read;
                                            while ((read = reader.ReadNextBlock()) > 0)
                                            {
                                                totalBytesRead += read;
                                                ReportProgress(relativePath, totalBytesRead, info.Length);
                                                zipWriter.Write(reader.CurrentBuffer, 0, read);
                                                zipWriter.Flush();
                                                bufferStream.Position = 0;
                                                var length = bufferStream.Length;
                                                string contentHash = ComputeContentHash(msBuffer, (int)length);

                                                session = await UploadChunkAsync(dropbox, session, offset, msBuffer, length, contentHash);

                                                offset += length;
                                                zipWriterUnderlyingStream.CopyTo = bufferStream = new MemoryStream(msBuffer);
                                                bufferStream.SetLength(0);
                                            }

                                            ReportComplete(relativePath);

                                            zipWriter.CloseEntry();
                                            zipWriter.Finish();
                                            zipWriter.Close();
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine(ex);
                                            // disposing ZipOutputStream causes writing to bufferStream
                                            if (!bufferStream.CanRead && !bufferStream.CanWrite)
                                                zipWriterUnderlyingStream.CopyTo = bufferStream = new MemoryStream(msBuffer);
                                            throw;
                                        }
                                    }

                                    bufferStream.Position = 0;
                                    var finalLength = bufferStream.Length;
                                    string finalContentHash = ComputeContentHash(msBuffer, (int)finalLength);
                                    var commitInfo = CreateCommitInfo(dropboxDirectory, relativePath, remoteFileExtension, clientModifiedAt);

                                    await FinishUploadAsync(dropbox, session, offset, commitInfo, msBuffer, finalLength, finalContentHash);
                                }
                            }
                        }
                        else
                        {
                            // Upload without encryption (direct upload)
                            for (int i = 0; i < newFilesList.Count; i++)
                            {
                                var relativePath = newFilesList[i];
                                Console.Write($" {relativePath}");

                                PrepareReader(reader, newFilesList, i, localDirectory);

                                string fullPath = Path.Combine(localDirectory, relativePath);
                                var info = new FileInfo(fullPath);
                                var clientModifiedAt = info.LastWriteTimeUtc;
                                var commitInfo = CreateCommitInfo(dropboxDirectory, relativePath, remoteFileExtension, clientModifiedAt);

                                // Handle empty files explicitly
                                if (info.Length == 0)
                                {
                                    var emptyStream = new MemoryStream(Array.Empty<byte>());
                                    await dropbox.Files.UploadAsync(commitInfo.Path,
                                        commitInfo.Mode,
                                        commitInfo.Autorename,
                                        commitInfo.ClientModified,
                                        body: emptyStream);
                                    ReportComplete(relativePath, "(empty file)");
                                    continue;
                                }

                                UploadSessionStartResult session = null;
                                long offset = 0;
                                MemoryStream bufferStream = null;
                                long totalBytesRead = 0;

                                int read;
                                while ((read = reader.ReadNextBlock()) > 0)
                                {
                                    totalBytesRead += read;
                                    ReportProgress(relativePath, totalBytesRead, info.Length);

                                    bufferStream = new MemoryStream(msBuffer);
                                    bufferStream.Write(reader.CurrentBuffer, 0, read);
                                    bufferStream.Position = 0;
                                    var length = bufferStream.Length;
                                    string contentHash = ComputeContentHash(msBuffer, (int)length);

                                    // Check if this is the last chunk
                                    bool isLastChunk = totalBytesRead >= info.Length;

                                    if (isLastChunk)
                                    {
                                        // Last chunk: use Finish or Upload
                                        await FinishUploadAsync(dropbox, session, offset, commitInfo, msBuffer, length, contentHash);
                                    }
                                    else
                                    {
                                        // Not last chunk: use Start or Append
                                        session = await UploadChunkAsync(dropbox, session, offset, msBuffer, length, contentHash);
                                    }

                                    offset += length;
                                }

                                ReportComplete(relativePath);
                            }
                        }
                    }

                    Console.WriteLine("Recycling deleted files for endless storage");

                    const ulong deletingBatchSize = 1024UL * 1024 * 1024 * 32;

                    for (var list = await dropbox.Files.ListFolderAsync(dropboxDirectory.TrimEnd('/'), true, limit: 2000, includeDeleted: true);
                        list != null;
                        list = list.HasMore ? await dropbox.Files.ListFolderContinueAsync(list.Cursor) : null)
                    {
                        foreach (var entry in list.Entries)
                        {
                            if (!entry.IsDeleted || existingFiles.Contains(entry.PathLower)) continue;

                            var parentFolder = entry.PathLower;
                            int lastSlash = parentFolder.LastIndexOf('/');
                            if (lastSlash == -1) continue;
                            parentFolder = parentFolder.Substring(0, lastSlash);
                            if (!existingFolders.Contains(parentFolder)) continue;

                            ListRevisionsResult rev;
                            try
                            {
                                rev = await dropbox.Files.ListRevisionsAsync(entry.AsDeleted.PathLower, ListRevisionsMode.Path.Instance, 1);

                            }
                            catch
                            {
                                // get revisions doesn't work for folders but no way to check if it's a folder beforehand
                                continue;
                            }

                            if (!(DateTime.UtcNow - rev.ServerDeleted >= TimeSpan.FromDays(15)) || (DateTime.UtcNow - rev.ServerDeleted > TimeSpan.FromDays(29)))
                            {
                                // don't need to restore too young
                                // can't restore too old
                                continue;
                            }

                            Console.WriteLine("Restoring " + entry.PathDisplay);
                            try
                            {
                                var restored = await dropbox.Files.RestoreAsync(entry.PathDisplay, rev.Entries.OrderByDescending(x => x.ClientModified).First().Rev);

                                if (restored.AsFile.Size >= deletingBatchSize && filesToDelete.Count == 0)
                                {
                                    Console.WriteLine("Deleting " + entry.PathDisplay);
                                    await dropbox.Files.DeleteV2Async(restored.PathLower, restored.Rev);
                                }
                                else
                                {
                                    // warning: rev not included, concurrent modification changes may be lost
                                    filesToDelete.Add(restored.PathLower);
                                    deletingAccumulatedSize += restored.Size;

                                    if (deletingAccumulatedSize >= deletingBatchSize)
                                        await DeleteFilesBatchAsync();
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex);
                            }

                        }
                    }
                    await DeleteFilesBatchAsync();

                }

                Console.WriteLine("All done");
            }
            catch (Exception e)
            {
                // redirecting error to normal output
                Console.WriteLine(e);
                throw;
            }
        }

        static bool IsEndingWithSeparator(string s)
        {
            return (s.Length != 0) && ((s[s.Length - 1] == Path.DirectorySeparatorChar) || (s[s.Length - 1] == Path.AltDirectorySeparatorChar));
        }

        static string ComputeContentHash(byte[] buffer, int length)
        {
            using var hasher = new DropboxContentHasher();
            hasher.TransformBlock(buffer, 0, length, buffer, 0);
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return DropboxContentHasher.ToHex(hasher.Hash);
        }

        /// <summary>
        /// Executes an upload operation with retry logic for timeout exceptions.
        /// Recreates the MemoryStream on each retry to ensure a fresh stream state.
        /// </summary>
        static async Task<T> RetryUploadAsync<T>(Func<MemoryStream, Task<T>> uploadOperation, byte[] buffer, long length, int maxRetries = 10)
        {
            for (int retry = 0;; retry++)
            {
                var bufferStream = new MemoryStream(buffer);
                bufferStream.Position = 0;
                bufferStream.SetLength(length);

                try
                {
                    return await uploadOperation(bufferStream);
                }
                catch (TaskCanceledException) when (retry < maxRetries)
                {
                    // Timeout occurred, will retry with fresh stream
                }
            }
        }

        /// <summary>
        /// Executes an upload operation with retry logic for timeout exceptions (non-generic version).
        /// </summary>
        static async Task RetryUploadAsync(Func<MemoryStream, Task> uploadOperation, byte[] buffer, long length, int maxRetries = 10)
        {
            await RetryUploadAsync(async stream =>
            {
                await uploadOperation(stream);
                return 0; // Dummy return value
            }, buffer, length, maxRetries);
        }

        /// <summary>
        /// Uploads a chunk using session-based upload. Starts a new session if needed, otherwise appends to existing session.
        /// </summary>
        static async Task<UploadSessionStartResult> UploadChunkAsync(
            DropboxClient dropbox,
            UploadSessionStartResult session,
            long offset,
            byte[] buffer,
            long length,
            string contentHash)
        {
            if (session == null)
            {
                return await RetryUploadAsync(stream =>
                    dropbox.Files.UploadSessionStartAsync(contentHash: contentHash, body: stream),
                    buffer, length);
            }
            else
            {
                await RetryUploadAsync(stream =>
                    dropbox.Files.UploadSessionAppendV2Async(
                        new UploadSessionCursor(session.SessionId, (ulong)offset),
                        contentHash: contentHash,
                        body: stream),
                    buffer, length);
                return session;
            }
        }

        /// <summary>
        /// Completes an upload. Uses simple upload for single-chunk files, or finishes a session for multi-chunk files.
        /// </summary>
        static async Task FinishUploadAsync(
            DropboxClient dropbox,
            UploadSessionStartResult session,
            long offset,
            CommitInfo commitInfo,
            byte[] buffer,
            long length,
            string contentHash)
        {
            if (session == null)
            {
                await RetryUploadAsync(stream =>
                    dropbox.Files.UploadAsync(
                        commitInfo.Path,
                        commitInfo.Mode,
                        commitInfo.Autorename,
                        commitInfo.ClientModified,
                        contentHash: contentHash,
                        body: stream),
                    buffer, length);
            }
            else
            {
                await RetryUploadAsync(stream =>
                    dropbox.Files.UploadSessionFinishAsync(
                        new UploadSessionCursor(session.SessionId, (ulong)offset),
                        commitInfo,
                        contentHash: contentHash,
                        body: stream),
                    buffer, length);
            }
        }

        /// <summary>
        /// Prepares the file reader for the next file and pre-opens the following file for optimization.
        /// </summary>
        static void PrepareReader(AsyncMultiFileReader reader, List<string> filesList, int currentIndex, string localDirectory)
        {
            var relativePath = filesList[currentIndex];
            string fullPath = Path.Combine(localDirectory, relativePath);
            reader.NextFile = (fullPath, null);
            reader.OpenNextFile();

            if (currentIndex < filesList.Count - 1)
                reader.NextFile = (Path.Combine(localDirectory, filesList[currentIndex + 1]), null);
        }

        /// <summary>
        /// Creates a CommitInfo object for uploading to Dropbox.
        /// </summary>
        static CommitInfo CreateCommitInfo(string dropboxDirectory, string relativePath, string fileExtension, DateTime clientModifiedAt)
        {
            return new CommitInfo(
                Path.Combine(dropboxDirectory, relativePath.Replace("\\", "/")) + fileExtension,
                WriteMode.Overwrite.Instance,
                false,
                clientModifiedAt);
        }

        /// <summary>
        /// Reports upload progress to the console.
        /// </summary>
        static void ReportProgress(string relativePath, long bytesProcessed, long totalBytes)
        {
            if (totalBytes > 0)
                Console.Write($"\r {relativePath} {bytesProcessed / (double)totalBytes * 100:F0}%");
        }

        /// <summary>
        /// Reports upload completion to the console.
        /// </summary>
        static void ReportComplete(string relativePath, string suffix = "")
        {
            Console.Write($"\r {relativePath} 100%");
            if (!string.IsNullOrEmpty(suffix))
                Console.Write($" {suffix}");
            Console.WriteLine();
        }
    }
}