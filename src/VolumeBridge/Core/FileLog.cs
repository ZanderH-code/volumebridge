using System.Text;

namespace VolumeBridge.Core;

/// <summary>Timestamped, thread-safe log file that rolls over to "&lt;name&gt;.1.log" at a size limit.</summary>
public sealed class FileLog : IDisposable
{
    readonly string _path;
    readonly long _maxBytes;
    readonly object _gate = new();
    StreamWriter _writer;

    public FileLog(string path, long maxBytes = 2 * 1024 * 1024)
    {
        _path = path;
        _maxBytes = maxBytes;
        _writer = Open(append: false);
    }

    public string Path => _path;

    /// <summary>Extra sink (e.g. the console in --speaker mode).</summary>
    public Action<string>? Echo { get; set; }

    StreamWriter Open(bool append) =>
        new(new FileStream(_path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        { AutoFlush = true };

    public void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (_gate)
        {
            try
            {
                if (_writer.BaseStream.Length > _maxBytes)
                {
                    _writer.Dispose();
                    string old = System.IO.Path.ChangeExtension(_path, ".1.log");
                    File.Move(_path, old, overwrite: true);
                    _writer = Open(append: false);
                }
                _writer.WriteLine(line);
            }
            catch (IOException) { /* logging must never take the audio down */ }
            Echo?.Invoke(line);
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
