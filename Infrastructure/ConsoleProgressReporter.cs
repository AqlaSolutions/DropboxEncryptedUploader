using System;

namespace DropboxEncrypedUploader.Infrastructure;

/// <summary>
/// Console-based implementation of progress reporting.
/// </summary>
public class ConsoleProgressReporter : IProgressReporter
{
    private readonly bool _useCarriageReturn;
    private bool _progressLineActive;

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
        if (_useCarriageReturn && _progressLineActive)
        {
            // Clear the active progress line before writing the message
            try
            {
                var width = Console.BufferWidth;
                Console.Write("\r" + new string(' ', width - 1) + "\r");
            }
            catch
            {
                // If we can't get buffer width, just use \r
                Console.Write("\r");
            }
            _progressLineActive = false;
        }
        Console.WriteLine(message);
    }

    public void ReportFileCount(int count)
    {
        Console.WriteLine($"Uploading files: {count}");
        _progressLineActive = false;
    }

    public void ReportProgress(string relativePath, long bytesProcessed, long totalBytes)
    {
        if (totalBytes > 0)
        {
            var progressText = $" {relativePath} {bytesProcessed / (double)totalBytes * 100:F0}%";
            if (_useCarriageReturn)
            {
                Console.Write($"\r{progressText}");
                _progressLineActive = true;
            }
            else
            {
                Console.WriteLine(progressText);
                _progressLineActive = false;
            }
        }
    }

    public void ReportComplete(string relativePath, string suffix = "")
    {
        var completeText = $" {relativePath} 100%";
        if (!string.IsNullOrEmpty(suffix))
            completeText += $" {suffix}";

        if (_useCarriageReturn)
        {
            Console.Write($"\r{completeText}\n");
            _progressLineActive = false;
        }
        else
        {
            Console.WriteLine(completeText);
            _progressLineActive = false;
        }
    }
}