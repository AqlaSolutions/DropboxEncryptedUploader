using System;

namespace DropboxEncrypedUploader.Infrastructure
{
    /// <summary>
    /// Console-based implementation of progress reporting.
    /// </summary>
    public class ConsoleProgressReporter : IProgressReporter
    {
        public void ReportMessage(string message)
        {
            Console.WriteLine(message);
        }

        public void ReportFileCount(int count)
        {
            Console.WriteLine($"Uploading files: {count}");
        }

        public void ReportProgress(string relativePath, long bytesProcessed, long totalBytes)
        {
            if (totalBytes > 0)
                Console.Write($"\r {relativePath} {bytesProcessed / (double)totalBytes * 100:F0}%");
        }

        public void ReportComplete(string relativePath, string suffix = "")
        {
            Console.Write($"\r {relativePath} 100%");
            if (!string.IsNullOrEmpty(suffix))
                Console.Write($" {suffix}");
            Console.WriteLine();
        }
    }
}
