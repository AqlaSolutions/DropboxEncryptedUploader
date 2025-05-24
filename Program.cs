using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

                bool localExists = Directory.Exists(localDirectory);
                if (!localExists)
                    Console.WriteLine("Local directory does not exist: " + localDirectory);

                var newFiles = new HashSet<string>(
                    localExists
                        ? Directory.GetFiles(localDirectory, "*", SearchOption.AllDirectories)
                            .Select(f => f.Substring(localDirectory.Length))
                        : [], StringComparer.OrdinalIgnoreCase);

                var filesToDelete = new HashSet<string>();

                using (var dropbox = new DropboxClient(token))
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
                            if (!relativePath.EndsWith(".zip")) continue;
                            var withoutZip = relativePath.Substring(0, relativePath.Length - 4).Replace("/", Path.DirectorySeparatorChar + "");
                            if (newFiles.Contains(withoutZip))
                            {
                                var info = new FileInfo(Path.Combine(localDirectory, withoutZip));
                                if ((info.LastWriteTimeUtc - entry.AsFile.ClientModified).TotalSeconds < 1f)
                                    newFiles.Remove(withoutZip);
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
                        ZipStrings.UseUnicode = true;
                        ZipStrings.CodePage = 65001;
                        var entryFactory = new ZipEntryFactory();
                        byte[] msBuffer = new byte[1000 * 1000 * 150];
                        int bufferSize = 1000 * 1000 * 140;
                        using var reader = new AsyncMultiFileReader(bufferSize, (f, t) => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true));
                        var newFilesList = newFiles.ToList();
                        for (int i = 0; i < newFilesList.Count; i++)
                        {
                            var relativePath = newFilesList[i];
                            Console.Write($" {relativePath}");
                            string fullPath = Path.Combine(localDirectory, relativePath);
                            reader.NextFile = (fullPath, null);
                            reader.OpenNextFile();
                            if (i < newFilesList.Count - 1)
                                reader.NextFile = (Path.Combine(localDirectory, newFilesList[i + 1]), null);

                            var info = new FileInfo(fullPath);
                            var clientModifiedAt = info.LastWriteTimeUtc;
                            using (var zipWriterUnderlyingStream = new CopyStream())
                            {
                                var bufferStream = new MemoryStream(msBuffer);
                                bufferStream.SetLength(0);
                                
                                UploadSessionStartResult session = null;
                                long offset = 0;

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
                                            Console.Write($"\r {relativePath} {offset / (double) info.Length * 100:F0}%");
                                            zipWriter.Write(reader.CurrentBuffer, 0, read);
                                            zipWriter.Flush();
                                            bufferStream.Position = 0;
                                            var length = bufferStream.Length;
                                            if (session == null)
                                                session = await dropbox.Files.UploadSessionStartAsync(new UploadSessionStartArg(), bufferStream);
                                            else
                                                await dropbox.Files.UploadSessionAppendV2Async(new UploadSessionCursor(session.SessionId, (ulong) offset), false, bufferStream);
                                            offset += length;
                                            zipWriterUnderlyingStream.CopyTo = bufferStream = new MemoryStream(msBuffer);
                                            bufferStream.SetLength(0);
                                        }

                                        Console.Write($"\r {relativePath} 100%");
                                        Console.WriteLine();

                                        zipWriter.CloseEntry();
                                        zipWriter.Finish();
                                        zipWriter.Close();
                                    }
                                    catch
                                    {
                                        // disposing ZipOutputStream causes writing to bufferStream
                                        if (!bufferStream.CanRead && !bufferStream.CanWrite)
                                            zipWriterUnderlyingStream.CopyTo = bufferStream = new MemoryStream(msBuffer);
                                        throw;
                                    }
                                }

                                bufferStream.Position = 0;
                                var commitInfo = new CommitInfo(Path.Combine(dropboxDirectory, relativePath.Replace("\\", "/")) + ".zip",
                                    WriteMode.Overwrite.Instance,
                                    false,
                                    clientModifiedAt);

                                if (session == null)
                                    await dropbox.Files.UploadAsync(commitInfo, bufferStream);
                                else
                                {
                                    await dropbox.Files.UploadSessionFinishAsync(new UploadSessionCursor(session.SessionId, (ulong) offset), commitInfo, bufferStream);
                                }
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
    }
}