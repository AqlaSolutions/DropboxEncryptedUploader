using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;
using ICSharpCode.SharpZipLib.Zip;

namespace DropboxEncrypedUploader
{
    internal class Program
    {
        static readonly char[] DirectorySeparatorChars = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        static async Task Main(string[] args)
        {
            var token = args[0];
            var localDirectory = Path.GetFullPath(args[1]);
            if (!IsEndingWithSeparator(localDirectory))
                localDirectory += Path.DirectorySeparatorChar;
            var dropboxDirectory = args[2];
            if (!IsEndingWithSeparator(dropboxDirectory))
                dropboxDirectory += Path.AltDirectorySeparatorChar;
            string password = args[3];

            var newFiles = new HashSet<string>(
                Directory.GetFiles(localDirectory, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(localDirectory.Length)), StringComparer.OrdinalIgnoreCase);

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


                for (var list = await dropbox.Files.ListFolderAsync(dropboxDirectory.TrimEnd('/'), true, limit: 2000); 
                    list != null;
                    list = list.HasMore ? await dropbox.Files.ListFolderContinueAsync(list.Cursor) : null)
                {
                    foreach (var entry in list.Entries)
                    {
                        if (!entry.IsFile) continue;
                        var relativePath = entry.PathLower.Substring(dropboxDirectory.Length);
                        if (!relativePath.EndsWith(".zip")) continue;
                        var withoutZip = relativePath.Substring(0, relativePath.Length - 4).Replace("/", Path.DirectorySeparatorChar + "");
                        if (newFiles.Contains(withoutZip))
                        {
                            var info = new FileInfo(Path.Combine(localDirectory, withoutZip));
                            if ((info.LastWriteTimeUtc - entry.AsFile.ClientModified).TotalSeconds < 1f)
                                newFiles.Remove(withoutZip);
                        }
                        else
                            filesToDelete.Add(entry.PathLower);
                    }
                }

                if (filesToDelete.Count > 0)
                {
                    Console.WriteLine($"Deleting files: \n{string.Join("\n", filesToDelete)}");
                    await dropbox.Files.DeleteBatchAsync(filesToDelete.Select(x => new DeleteArg(x)));
                }

                if (newFiles.Count == 0) return;
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
                    using (var zipOutputStream = new CopyStream())
                    using (var zipInputStream = new ZipOutputStream(zipOutputStream, bufferSize) { IsStreamOwner = false, Password = password, UseZip64 = UseZip64.On })
                    {
                        var bufferStream = new MemoryStream(msBuffer);
                        bufferStream.SetLength(0);
                        zipOutputStream.CopyTo = bufferStream;
                        zipInputStream.SetLevel(0);
                        var entry = entryFactory.MakeFileEntry(fullPath, '/' + Path.GetFileName(relativePath), true);
                        entry.AESKeySize = 256;
                        zipInputStream.PutNextEntry(entry);
                        UploadSessionStartResult session = null;

                        long offset = 0;
                        int read;
                        while ((read = reader.ReadNextBlock()) > 0)
                        {
                            Console.Write($"\r {relativePath} {offset / (double) info.Length * 100:F0}%");
                            zipInputStream.Write(reader.CurrentBuffer, 0, read);
                            zipInputStream.Flush();
                            bufferStream.Position = 0;
                            var length = bufferStream.Length;
                            if (session == null)
                                session = await dropbox.Files.UploadSessionStartAsync(new UploadSessionStartArg(), bufferStream);
                            else
                                await dropbox.Files.UploadSessionAppendV2Async(new UploadSessionCursor(session.SessionId, (ulong) offset), false, bufferStream);
                            offset += length;
                            zipOutputStream.CopyTo = bufferStream = new MemoryStream(msBuffer);
                            bufferStream.SetLength(0);
                        }

                        Console.Write($"\r {relativePath} 100%");
                        Console.WriteLine();

                        zipInputStream.CloseEntry();
                        zipInputStream.Finish();
                        zipInputStream.Close();
                        
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

            Console.WriteLine("All done");
        }

        static bool IsEndingWithSeparator(string s)
        {
            return (s.Length != 0) && ((s[s.Length - 1] == Path.DirectorySeparatorChar) || (s[s.Length - 1] == Path.AltDirectorySeparatorChar));
        }
    }
}