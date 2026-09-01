using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sesame.Services;

namespace Sesame.Services.Mii;

/// <summary>
/// Client for the optional FFL-Testing native renderer. The renderer stays in
/// its own process because FFL is C++/OpenGL and is not part of the WPF app.
/// </summary>
public sealed class FflRenderer : IDisposable
{
    private const int RequestSize = 155;
    private const int ResourceTypeHigh = 1;
    private const int ShaderTypeSwitch = 1;
    private const int ResponseFormatTgaBgraFlipY = 2;
    private const int Resolution = 256;
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private Process? _process;
    private int _port;
    private string? _processResource;
    private bool _disposed;

    public static void SaveResourcePath(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("FFL resource file was not found.", path);
        var saved = AppDataPaths.Combine("mii-ffl-resource.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        File.WriteAllText(saved, Path.GetFullPath(path));
        AppDataPaths.RestrictFile(saved);
    }

    public async Task<ImageSource?> RenderAsync(byte[] edenRecord, CancellationToken cancellationToken = default)
    {
        if (_disposed || edenRecord.Length != MiiFormatSwitch.RecordSize)
            return null;

        var helper = FindHelper();
        var resource = FindResource();
        if (helper is null || resource is null)
            return null;

        await _renderLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var port = await EnsureProcessAsync(helper, resource, cancellationToken).ConfigureAwait(false);
            if (port is null) return null;

            try
            {
                return await SendRequestAsync(port.Value, edenRecord, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                StopProcess();
                return null;
            }
        }
        finally { _renderLock.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProcess();
        _renderLock.Dispose();
    }

    private async Task<int?> EnsureProcessAsync(string helper, string resource, CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } &&
            string.Equals(_processResource, resource, StringComparison.OrdinalIgnoreCase))
            return _port;

        StopProcess();
        var port = FindFreePort();
        var start = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helper) ?? AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString());
        start.ArgumentList.Add("--resource-high");
        start.ArgumentList.Add(resource);

        try
        {
            _process = Process.Start(start);
            if (_process is null) return null;
            _processResource = resource;
            _port = port;
            var ready = await WaitForPortAsync(port, _process, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                StopProcess();
                return null;
            }
            return port;
        }
        catch
        {
            StopProcess();
            return null;
        }
    }

    private static async Task<ImageSource?> SendRequestAsync(int port, byte[] record,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
        using var stream = client.GetStream();
        var request = BuildRequest(record);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var header = new byte[18];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var width = BitConverter.ToUInt16(header, 12);
        var height = BitConverter.ToUInt16(header, 14);
        var bitsPerPixel = header[16];
        if (width is 0 or > 2048 || height is 0 or > 2048 || bitsPerPixel != 32)
            return null;

        var pixels = new byte[checked(width * height * 4)];
        await ReadExactlyAsync(stream, pixels, cancellationToken).ConfigureAwait(false);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] BuildRequest(byte[] record)
    {
        using var stream = new MemoryStream(RequestSize);
        using var writer = new BinaryWriter(stream);
        writer.Write(record);
        writer.Write(new byte[96 - record.Length]);
        writer.Write((ushort)record.Length);
        writer.Write((byte)1); // normal model
        writer.Write((byte)ResponseFormatTgaBgraFlipY);
        writer.Write((ushort)Resolution);
        writer.Write((short)-256); // FFL texture resolution + mipmaps
        writer.Write((byte)0); // face + body
        writer.Write((sbyte)ResourceTypeHigh);
        writer.Write((byte)ShaderTypeSwitch);
        writer.Write((byte)0); // normal expression
        writer.Write(new byte[12]); // expression flags
        writer.Write(new byte[12]); // camera and model rotation
        writer.Write(new byte[] { 232, 244, 246, 255 });
        writer.Write((byte)0); // AA method
        writer.Write((byte)0); // all draw stages
        writer.Write(true); // verify the converted FFL CharInfo
        writer.Write(false); // record is core data; no CRC16 check here
        writer.Write(true); // lighting
        writer.Write((sbyte)-1); // default clothes colour
        writer.Write((sbyte)-1); // default pants colour
        writer.Write((sbyte)-1); // default body for shader
        writer.Write((sbyte)-1); // no headwear
        writer.Write((sbyte)-1);
        writer.Write((byte)1); // one instance
        writer.Write((byte)0);
        writer.Write(new byte[6]); // default light direction
        writer.Write((byte)0); // no split render
        var bytes = stream.ToArray();
        if (bytes.Length != RequestSize)
            throw new InvalidDataException($"FFL request was {bytes.Length} bytes; expected {RequestSize}.");
        return bytes;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("FFL renderer closed the response socket.");
            offset += read;
        }
    }

    private static async Task<bool> WaitForPortAsync(int port, Process process,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SocketException) { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
        }
        return false;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindHelper()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Renderer", "ffl_testing_2.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffl_testing_2.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindResource()
    {
        var candidates = new List<string>();
        var environment = Environment.GetEnvironmentVariable("SESAME_FFL_RES_HIGH");
        if (!string.IsNullOrWhiteSpace(environment)) candidates.Add(environment);
        var saved = AppDataPaths.Combine("mii-ffl-resource.txt");
        try
        {
            if (File.Exists(saved)) candidates.Add(File.ReadAllText(saved).Trim());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var names = new[] { "FFLResHigh.dat", "AFLResHigh_2_3.dat" };
        var basePath = AppContext.BaseDirectory;
        foreach (var name in names) candidates.Add(Path.Combine(basePath, name));
        foreach (var ancestor in Ancestors(basePath, 8))
        {
            foreach (var name in names) candidates.Add(Path.Combine(ancestor, name));
            var assets = Path.Combine(ancestor, "Assets");
            if (!Directory.Exists(assets)) continue;
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(assets, "*.dat", SearchOption.AllDirectories)
                    .Where(x => names.Any(n => string.Equals(Path.GetFileName(x), n,
                        StringComparison.OrdinalIgnoreCase))));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> Ancestors(string path, int max)
    {
        var current = new DirectoryInfo(path);
        for (var i = 0; current is not null && i < max; i++, current = current.Parent)
            yield return current.FullName;
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _processResource = null;
        _port = 0;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch { }
        finally { process.Dispose(); }
    }
}
