namespace DropboxEncrypedUploader.Infrastructure
{
    /// <summary>
    /// Reports progress and messages during upload operations.
    /// </summary>
    public interface IProgressReporter
    {
        /// <summary>
        /// Reports a general message.
        /// </summary>
        void ReportMessage(string message);

        /// <summary>
        /// Reports the number of files to process.
        /// </summary>
        void ReportFileCount(int count);

        /// <summary>
        /// Reports progress for a file being uploaded.
        /// </summary>
        /// <param name="relativePath">File path</param>
        /// <param name="bytesProcessed">Bytes processed so far</param>
        /// <param name="totalBytes">Total bytes to process</param>
        void ReportProgress(string relativePath, long bytesProcessed, long totalBytes);

        /// <summary>
        /// Reports completion of a file upload.
        /// </summary>
        /// <param name="relativePath">File path</param>
        /// <param name="suffix">Optional suffix message (e.g., "(empty file)")</param>
        void ReportComplete(string relativePath, string suffix = "");
    }
}
