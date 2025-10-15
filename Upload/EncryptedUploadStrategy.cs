using System;
using System.IO;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using ICSharpCode.SharpZipLib.Zip;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;

namespace DropboxEncrypedUploader.Upload
{
    /// <summary>
    /// Upload strategy that encrypts files into password-protected ZIP archives.
    /// </summary>
    public class EncryptedUploadStrategy : IUploadStrategy
    {
        private readonly IUploadSessionManager _sessionManager;
        private readonly IProgressReporter _progress;
        private readonly Configuration.Configuration _config;
        private readonly ZipEntryFactory _entryFactory;

        public EncryptedUploadStrategy(
            IUploadSessionManager sessionManager,
            IProgressReporter progress,
            Configuration.Configuration config)
        {
            _sessionManager = sessionManager;
            _progress = progress;
            _config = config;
            _entryFactory = new ZipEntryFactory();
        }

        public async Task UploadFileAsync(FileToUpload fileToUpload, AsyncMultiFileReader reader, byte[] buffer, string nextFilePathForPreOpen)
        {
            _progress.ReportProgress(fileToUpload.RelativePath, 0, fileToUpload.FileSize);

            reader.OpenNextFile();

            // Set next file for pre-opening optimization (after OpenNextFile clears it)
            if (nextFilePathForPreOpen != null)
            {
                reader.NextFile = (nextFilePathForPreOpen, null);
            }

            using (var zipWriterUnderlyingStream = new CopyStream())
            {
                var bufferStream = new MemoryStream(buffer);
                bufferStream.SetLength(0);

                UploadSessionStartResult session = null;
                long offset = 0;
                long totalBytesRead = 0;

                using (var zipWriter = new ZipOutputStream(zipWriterUnderlyingStream, _config.ReadBufferSize)
                {
                    IsStreamOwner = false,
                    Password = _config.Password,
                    UseZip64 = UseZip64.On
                })
                {
                    try
                    {
                        zipWriterUnderlyingStream.CopyTo = bufferStream;
                        zipWriter.SetLevel(0); // No compression

                        // Create ZIP entry with leading slash
                        var entry = _entryFactory.MakeFileEntry(
                            fileToUpload.FullPath,
                            '/' + Path.GetFileName(fileToUpload.RelativePath),
                            useFileSystem: true);
                        entry.AESKeySize = 256;
                        zipWriter.PutNextEntry(entry);

                        // Read and encrypt file in chunks
                        int read;
                        while ((read = reader.ReadNextBlock()) > 0)
                        {
                            totalBytesRead += read;
                            _progress.ReportProgress(fileToUpload.RelativePath, totalBytesRead, fileToUpload.FileSize);

                            zipWriter.Write(reader.CurrentBuffer, 0, read);
                            zipWriter.Flush();
                            bufferStream.Position = 0;
                            var length = bufferStream.Length;
                            string contentHash = UploadSessionManager.ComputeContentHash(buffer, (int)length);

                            // Upload chunk
                            session = await UploadChunkAsync(session, offset, buffer, length, contentHash);

                            offset += length;
                            zipWriterUnderlyingStream.CopyTo = bufferStream = new MemoryStream(buffer);
                            bufferStream.SetLength(0);
                        }

                        _progress.ReportComplete(fileToUpload.RelativePath);

                        zipWriter.CloseEntry();
                        zipWriter.Finish();
                        zipWriter.Close();
                    }
                    catch (Exception ex)
                    {
                        _progress.ReportMessage("Error during encrypted upload: " + ex.ToString());
                        // Disposing ZipOutputStream causes writing to bufferStream
                        if (!bufferStream.CanRead && !bufferStream.CanWrite)
                            zipWriterUnderlyingStream.CopyTo = bufferStream = new MemoryStream(buffer);
                        throw;
                    }
                }

                // Upload final chunk
                bufferStream.Position = 0;
                var finalLength = bufferStream.Length;
                string finalContentHash = UploadSessionManager.ComputeContentHash(buffer, (int)finalLength);
                var commitInfo = CreateCommitInfo(fileToUpload);

                await FinishUploadAsync(session, offset, commitInfo, buffer, finalLength, finalContentHash);
            }
        }

        private async Task<UploadSessionStartResult> UploadChunkAsync(
            UploadSessionStartResult session,
            long offset,
            byte[] buffer,
            long length,
            string contentHash)
        {
            if (session == null)
            {
                return await _sessionManager.StartSessionAsync(buffer, length, contentHash);
            }
            else
            {
                await _sessionManager.AppendSessionAsync(session.SessionId, offset, buffer, length, contentHash);
                return session;
            }
        }

        private async Task FinishUploadAsync(
            UploadSessionStartResult session,
            long offset,
            CommitInfo commitInfo,
            byte[] buffer,
            long length,
            string contentHash)
        {
            if (session == null)
            {
                await _sessionManager.SimpleUploadAsync(commitInfo, buffer, length, contentHash);
            }
            else
            {
                await _sessionManager.FinishSessionAsync(session.SessionId, offset, commitInfo, buffer, length, contentHash);
            }
        }

        private CommitInfo CreateCommitInfo(FileToUpload fileToUpload)
        {
            return new CommitInfo(
                fileToUpload.RemotePath,
                WriteMode.Overwrite.Instance,
                autorename: false,
                clientModified: fileToUpload.ClientModified);
        }
    }
}
