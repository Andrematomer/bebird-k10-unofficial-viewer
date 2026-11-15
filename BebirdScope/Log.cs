using System.IO;
using System.Text;

namespace BebirdScope;

/// <summary>Tiny thread-safe file logger: %LOCALAPPDATA%\BebirdScope\log.txt (auto-reset above 512 KB).</summary>
internal static class Log
{
    static readonly object Gate = new object();

    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BebirdScope");

    public static readonly string FilePath = Path.Combine(Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
