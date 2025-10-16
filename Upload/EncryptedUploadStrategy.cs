using System;
using System.IO;
using System.Threading.Tasks;
using Dropbox.Api.Files;
using DropboxEncrypedUploader.Infrastructure;
using DropboxEncrypedUploader.Models;
using DropboxEncrypedUploader.Services;
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
    private readonly byte[] _buffer;

    public EncryptedUploadStrategy(
        IUploadSessionManager sessionManager,
        IProgressReporter progress,
        Configuration.Configuration config,
        ISessionPersistenceService sessionPersistence)
        : base(sessionManager, sessionPersistence, progress)
    {
        _progress = progress;
        _config = config;
        _buffer = new byte[config.MaxBufferAllocation]; // 99MB buffer for encrypted output
    }

    public async Task UploadFileAsync(FileToUpload fileToUpload, AsyncMultiFileReader reader)
    {
        _progress.ReportProgress(fileToUpload.RelativePath, 0, fileToUpload.FileSize);

        PrepareUpload(fileToUpload);

        // Get salt: either from saved session (resuming) or generate fresh (new upload)
        byte[] salt = GetResumedEncryptionSalt();
        if (salt == null)
        {
            // New upload - generate our own random salt
            salt = new byte[Configuration.Configuration.AES_SALT_SIZE];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
        }

        using (var zipWriterUnderlyingStream = new CopyStream())
        {
            var bufferStream = new MemoryStream(_buffer);
            bufferStream.SetLength(0);

            long totalBytesRead = 0;

            using (var zipWriter = new ZipOutputStream(zipWriterUnderlyingStream, _config.ReadBufferSize))
            {
                zipWriter.IsStreamOwner = false;
                zipWriter.Password = _config.Password;
                zipWriter.UseZip64 = UseZip64.On;
                try
                {
                    zipWriterUnderlyingStream.CopyTo = bufferStream;
                    zipWriter.SetLevel(0);

                    var entry = _entryFactory.MakeFileEntry(
                        fileToUpload.FullPath,
                        '/' + Path.GetFileName(fileToUpload.RelativePath),
                        useFileSystem: true);
                    entry.AESKeySize = 256;

                    // ALWAYS use deterministic salt (either loaded from session or freshly generated)
                    bool saltSetSuccessfully = ZipEncryptionHelper.SetDeterministicSaltGenerator(salt);
                    if (!saltSetSuccessfully)
                    {
                        throw new ResumeFailedException("Could not set deterministic salt - SharpZipLib field structure may have changed");
                    }

                    await zipWriter.PutNextEntryAsync(entry);

                    // Restore random generator after entry is created
                    ZipEncryptionHelper.RestoreRandomSaltGenerator();

                    int read;
                    while ((read = reader.ReadNextBlock()) > 0)
                    {
                        totalBytesRead += read;
                        _progress.ReportProgress(fileToUpload.RelativePath, totalBytesRead, fileToUpload.FileSize);

                        zipWriter.Write(reader.CurrentBuffer, 0, read);
                        zipWriter.Flush();
                        bufferStream.Position = 0;
                        var length = bufferStream.Length;

                        // Pass our salt (either loaded or generated) to UploadChunkAsync
                        await UploadChunkAsync(_buffer, length, salt);

                        zipWriterUnderlyingStream.CopyTo = bufferStream = new MemoryStream(_buffer);
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
                        zipWriterUnderlyingStream.CopyTo = new MemoryStream(_buffer);
                    throw;
                }
            }

            // Upload final chunk
            bufferStream.Position = 0;
            var finalLength = bufferStream.Length;
            var commitInfo = CreateCommitInfo(fileToUpload);

            await FinishUploadAsync(commitInfo, _buffer, finalLength);
        }
    }
}