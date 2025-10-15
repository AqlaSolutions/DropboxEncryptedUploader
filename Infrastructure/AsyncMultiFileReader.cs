using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DropboxEncrypedUploader;

public class AsyncMultiFileReader : IDisposable
{
    public virtual byte[] CurrentBuffer { get; protected set; }
    public virtual (string Name, object Tag) NextFile { get; set; }
    public virtual (string Name, object Tag) CurrentFile { get; protected set; }

    readonly Func<string, object, Stream> _asyncFileStreamFactory;
    byte[] _buffer1;
    byte[] _buffer2;
    Stream _stream;
    Task<(int read, Stream stream, string fileName, object tag)> _preOpenedFile;
    Task<int> _latestReadBytesCount;

    public AsyncMultiFileReader(int bufferSize, Func<string, object, Stream> asyncFileStreamFactory = null)
    {
        _asyncFileStreamFactory = asyncFileStreamFactory
                                  ?? ((fileName, _) => new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, _buffer1.Length, true));
        _buffer1 = new byte[bufferSize];
        _buffer2 = new byte[bufferSize];
        CurrentBuffer = _buffer1;
    }

    (int read, Stream stream, string fileName, object tag) OpenAndRead(string fileName, object tag, byte[] buffer)
    {
        Stream f = null;
        try
        {
            f = _asyncFileStreamFactory(fileName, tag);
            return (f.Read(buffer, 0, buffer.Length), f, fileName, tag);
        }
        catch
        {
            f?.Dispose();
            throw;
        }
    }

    public virtual int ReadNextBlock()
    {
        if (_stream == null) return 0;

        int read = _latestReadBytesCount.Result;

        if (_stream.Position < _stream.Length)
        {
            _latestReadBytesCount = _stream.ReadAsync(_buffer2, 0, _buffer2.Length);
        }
        else
        {
            _latestReadBytesCount = null;
            // in threadpool thread we shouldn't block for another threadpool task
            if (NextFile.Name != null && !Thread.CurrentThread.IsThreadPoolThread)
            {
                var fn2 = NextFile;
                var b2 = _buffer2;
                _preOpenedFile = Task.Run(() => OpenAndRead(fn2.Name, fn2.Tag, b2));
            }

            _stream.Dispose();
            _stream = null;
        }

        CurrentBuffer = _buffer1;

        (_buffer2, _buffer1) = (_buffer1, _buffer2);

        return read;
    }

    /// <summary>
    ///
    /// </summary>
    /// <exception cref="AggregateException"></exception>
    /// <exception cref="IOException"></exception>
    public virtual void OpenNextFile()
    {
        if (_stream != null)
        {
            _stream.Dispose();
            _stream = null;
        }

        CurrentFile = (null, null);
        _latestReadBytesCount = null;

        try
        {
            var nextFile = NextFile;

            (var read, _stream, var opened, var openedTag) = _preOpenedFile?.Result ?? OpenAndRead(nextFile.Name, nextFile.Tag, _buffer1);

            if (opened != nextFile.Name || openedTag != nextFile.Tag)
            {
                _stream.Dispose();
                (read, _stream, _, _) = OpenAndRead(nextFile.Name, nextFile.Tag, _buffer1);
            }

            CurrentFile = nextFile;;
            _latestReadBytesCount = Task.FromResult(read);
        }
        finally
        {
            NextFile = (null, null);
            _preOpenedFile = null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        try
        {
            _preOpenedFile?.Result.stream.Dispose();
        }
        catch
        {
        }
    }
}