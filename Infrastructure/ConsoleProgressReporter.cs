using System;

namespace DropboxEncrypedUploader.Infrastructure;

/// <summary>
/// Console-based implementation of progress reporting.
/// </summary>
public class ConsoleProgressReporter : IProgressReporter
{
    private readonly bool _useCarriageReturn;

    public ConsoleProgressReporter()
    {
        // Use carriage return only if output is not redirected and console is available
        try
        {
            _useCarriageReturn = !Console.IsOutputRedirected && Console.CursorLeft >= 0;
        }
        catch
        {
            _useCarriageReturn = false;
        }
    }

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
        {
            var progressText = $" {relativePath} {bytesProcessed / (double)totalBytes * 100:F0}%";
            if (_useCarriageReturn)
                Console.Write($"\r{progressText}");
            // In non-interactive mode, don't spam with progress updates
        }
    }

    public void ReportComplete(string relativePath, string suffix = "")
    {
        var completeText = $" {relativePath} 100%";
        if (!string.IsNullOrEmpty(suffix))
            completeText += $" {suffix}";

        if (_useCarriageReturn)
            Console.Write($"\r{completeText}\n");
        else
            Console.WriteLine(completeText);
    }
}