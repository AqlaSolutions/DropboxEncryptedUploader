using System;
using System.IO;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;
using ICSharpCode.SharpZipLib.Zip;

namespace DropboxEncrypedUploader.Upload;

/// <summary>
/// Upload strategy that encrypts files into password-protected ZIP archives.
/// </summary>
public class EncryptedUploadStrategy : BaseUploadStrategy, IUploadStrategy
{
    private readonly IProgressReporter _progress;
    private readonly Configuration.Configuration _config;
    private readonly ZipEntryFactory _entryFactory = new();

    public EncryptedUploadStrategy(
        IUploadSessionManager sessionManager,
        IProgressReporter progress,
        Configuration.Configuration config)
        : base(sessionManager)
    {
        _progress = progress;
        _config = config;
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
                    await zipWriter.PutNextEntryAsync(entry);

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

                        // Upload chunk
                        session = await UploadChunkAsync(session, offset, buffer, length);

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
                    _progress.ReportMessage("Error during encrypted upload: " + ex);
                    // Disposing ZipOutputStream causes writing to bufferStream
                    if (!bufferStream.CanRead && !bufferStream.CanWrite)
                        zipWriterUnderlyingStream.CopyTo = new MemoryStream(buffer);
                    throw;
                }
            }

            // Upload final chunk
            bufferStream.Position = 0;
            var finalLength = bufferStream.Length;
            var commitInfo = CreateCommitInfo(fileToUpload);

            await FinishUploadAsync(session, offset, commitInfo, buffer, finalLength);
        }
    }
}
