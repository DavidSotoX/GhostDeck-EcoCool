using System.Text;

namespace GhostDeck;

/// <summary>
/// Crash-safe persistence for small critical JSON/text files. Data is written to a sibling
/// temporary file, flushed to disk, validated by the caller before commit when needed, then
/// atomically replaces the destination while preserving a .bak recovery copy.
/// </summary>
public static class SafeFile
{
    public static void WriteAllTextAtomic(string path, string content, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false);
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = full + ".tmp." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
        string bak = full + ".bak";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                           16 * 1024, FileOptions.WriteThrough))
            using (var sw = new StreamWriter(fs, encoding))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                try { File.Replace(tmp, full, bak, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(full, bak, overwrite: true);
                    File.Move(tmp, full, overwrite: true);
                }
            }
            else File.Move(tmp, full);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
