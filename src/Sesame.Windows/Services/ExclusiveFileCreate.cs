namespace Sesame.Services;

/// <summary>Shared local half of DeckClient's create-new contract; never follows an existing target link.</summary>
internal static class ExclusiveFileCreate
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    public static void Upload(byte[] data, Action<Stream, bool> upload)
    {
        using var stream = new MemoryStream(data, writable: false);
        // false maps to SSH_FXF_EXCL in SSH.NET: an existing file or symlink is never replaced.
        upload(stream, false);
    }
}
