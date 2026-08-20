using System.Text;

namespace MesIngest.Tests;

internal static class ExclusiveFileWriter
{
    public static FileStream WriteUtf8AndHold(string path, string content)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        stream.SetLength(0);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return stream;
    }
}
