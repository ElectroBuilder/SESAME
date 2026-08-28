using System.Diagnostics;
using System.IO;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sesame.Models;

namespace Sesame.Services;

public sealed class DeckClient : IDisposable
{
    private enum Kind { None, Ssh, Local }

    private readonly object _gate = new();
    private Kind _kind;
    private SshClient? _ssh;
    private SftpClient? _sftp;
    private ShellStream? _shell;
    private Process? _localShell;
    private StreamWriter? _localIn;
    private CancellationTokenSource? _shellCts;

    public bool IsLocal
    {
        get { lock (_gate) return _kind == Kind.Local; }
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                if (_kind == Kind.Local) return ActiveProfile is not null;
                return ActiveProfile is not null && (_sftp is { IsConnected: true } || _ssh is { IsConnected: true });
            }
        }
    }

    public ConnectionProfile? ActiveProfile { get; private set; }
    public string Home => IsLocal ? HostEnvironment.Home : "/home/deck";

    public event Action<string>? ShellOutput;

    public void Connect(ConnectionProfile profile)
    {
        if (profile.IsLocal || string.Equals(profile.Host, "local", StringComparison.OrdinalIgnoreCase))
        {
            ConnectLocal();
            return;
        }

        lock (_gate)
        {
            DisconnectLocked();
            OpenLocked(profile);
        }
    }

    public void ConnectLocal()
    {
        lock (_gate)
        {
            if (!HostEnvironment.LocalAvailable)
                throw new InvalidOperationException(
                    "Lokale modus is alleen beschikbaar op de Steam Deck zelf. Verbind via SSH vanaf een andere pc, of start SESAME op de Deck.");
            DisconnectLocked();
            _kind = Kind.Local;
            ActiveProfile = ConnectionProfile.LocalDeck();
            try
            {
                StartLocalShell();
            }
            catch (Exception ex)
            {
                ShellOutput?.Invoke("Terminal kon niet starten: " + ex.Message + "\r\n");
            }
        }
    }

    public void ConnectWithFallback(IEnumerable<ConnectionProfile> profiles)
    {
        Exception? last = null;
        foreach (var profile in profiles)
        {
            try
            {
                Connect(profile);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last ?? new InvalidOperationException("Geen profiel om mee te verbinden.");
    }

    public IReadOnlyList<RemoteItem> List(string path)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local) return ListLocal(path);
            return WithSftp(path, ListLocked);
        }
    }

    public bool Exists(string path)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local) return File.Exists(path) || Directory.Exists(path);
            return WithSftp(path, p => _sftp!.Exists(p));
        }
    }

    public void CreateDirectory(string path)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                Directory.CreateDirectory(path);
                return;
            }
            WithSftp(path, p =>
            {
                _sftp!.CreateDirectory(p);
                return true;
            });
        }
    }

    public void EnsureDirectory(string path)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    Directory.CreateDirectory(path);
                return;
            }
            EnsureDirectoryLocked(path);
        }
    }

    public long FileLength(string path)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                try { return new FileInfo(path).Length; }
                catch { return -1L; }
            }
            return WithSftp(path, p =>
            {
                try { return _sftp!.GetAttributes(p).Size; }
                catch { return -1L; }
            });
        }
    }

    public byte[] ReadBytes(string path)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local) return File.ReadAllBytes(path);
            return WithSftp(path, p =>
            {
                using var ms = new MemoryStream();
                _sftp!.DownloadFile(p, ms);
                return ms.ToArray();
            });
        }
    }

    public void WriteBytes(string path, byte[] data)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                var parent = Parent(path);
                if (!string.IsNullOrWhiteSpace(parent) && parent != "/")
                    Directory.CreateDirectory(parent);
                File.WriteAllBytes(path, data);
                return;
            }
            WithSftp(path, p =>
            {
                EnsureDirectoryLocked(Parent(p));
                using var ms = new MemoryStream(data);
                _sftp!.UploadFile(ms, p, true);
                return true;
            });
        }
    }

    public void WriteText(string path, string text) =>
        WriteBytes(path, Encoding.UTF8.GetBytes(text ?? ""));

    public void Rename(string from, string to)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to, overwrite: true);
                return;
            }
            EnsureAliveLocked();
            try
            {
                _sftp!.RenameFile(from, to);
            }
            catch
            {
                ReconnectLocked();
                _sftp!.RenameFile(from, to);
            }
        }
    }

    public void Delete(RemoteItem item)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                if (item.IsDirectory) Directory.Delete(item.FullPath, recursive: true);
                else File.Delete(item.FullPath);
                return;
            }
            EnsureAliveLocked();
            try
            {
                DeleteLocked(item);
            }
            catch
            {
                ReconnectLocked();
                DeleteLocked(item);
            }
        }
    }

    public void DeletePath(string path, bool directory = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Exists(path)) return;
        Delete(new RemoteItem
        {
            IsDirectory = directory,
            Name = Path.GetFileName(path.TrimEnd('/')),
            FullPath = path
        });
    }

    public void UploadFile(string localPath, string remoteDir, Action<string>? progress = null, string? remoteName = null)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                Directory.CreateDirectory(remoteDir);
                var name = string.IsNullOrWhiteSpace(remoteName) ? Path.GetFileName(localPath) : remoteName;
                progress?.Invoke("Kopiëren " + name);
                File.Copy(localPath, Combine(remoteDir, name), overwrite: true);
                return;
            }
            EnsureAliveLocked();
            UploadFileLocked(localPath, remoteDir, progress, remoteName);
        }
    }

    public void UploadFolder(string localDir, string remoteDir, Action<string>? progress = null)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                var name = Path.GetFileName(localDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                CopyDirectoryLocal(localDir, Combine(remoteDir, name), progress);
                return;
            }
            EnsureAliveLocked();
            UploadFolderLocked(localDir, remoteDir, progress);
        }
    }

    public void UploadContents(string localDir, string remoteDir, Action<double, string>? progress = null)
    {
        lock (_gate)
        {
            var files = Directory.Exists(localDir)
                ? Directory.GetFiles(localDir, "*", SearchOption.AllDirectories)
                : [];
            var total = files.Sum(f => new FileInfo(f).Length);
            if (_kind == Kind.Local)
            {
                long done = 0;
                CopyContentsLocal(localDir, remoteDir, total, ref done, progress);
                return;
            }
            EnsureAliveLocked();
            long sent = 0;
            UploadContentsLocked(localDir, remoteDir, total, ref sent, progress);
        }
    }

    public void DownloadFile(string remotePath, string localPath, Action<string>? progress = null)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                progress?.Invoke("Kopiëren " + Path.GetFileName(remotePath));
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                File.Copy(remotePath, localPath, overwrite: true);
                return;
            }
            EnsureAliveLocked();
            DownloadFileLocked(remotePath, localPath, progress);
        }
    }

    public void DownloadItem(RemoteItem item, string localDir, Action<string>? progress = null)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                var dest = Path.Combine(localDir, item.Name);
                if (!item.IsDirectory)
                {
                    Directory.CreateDirectory(localDir);
                    File.Copy(item.FullPath, dest, overwrite: true);
                    return;
                }
                CopyDirectoryLocal(item.FullPath, dest, progress);
                return;
            }
            EnsureAliveLocked();
            DownloadItemLocked(item, localDir, progress);
        }
    }

    public bool HasShell
    {
        get
        {
            lock (_gate)
            {
                if (_kind == Kind.Local)
                    return _localIn is not null && _localShell is { HasExited: false };
                return _shell is { CanWrite: true };
            }
        }
    }

    public void SendShell(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_kind == Kind.Local)
            {
                if (_localIn is null)
                {
                    ShellOutput?.Invoke("Niet verbonden — klik eerst op Verbinden.\r\n");
                    return;
                }
                try { _localIn.Write(text); _localIn.Flush(); }
                catch (Exception ex)
                {
                    ShellOutput?.Invoke("\r\n[terminal] schrijven mislukt: " + ex.Message + "\r\n");
                }
                return;
            }
            if (_shell is null)
            {
                ShellOutput?.Invoke("Niet verbonden — klik eerst op Verbinden.\r\n");
                return;
            }
            try
            {
                _shell.Write(text);
            }
            catch (Exception ex)
            {
                ShellOutput?.Invoke("\r\n[terminal] schrijven mislukt: " + ex.Message + "\r\n");
            }
        }
    }

    public void ResizeShell(uint columns, uint rows)
    {
        lock (_gate)
        {
            if (_shell is null) return;
            try
            {
                columns = Math.Max(20, columns);
                rows = Math.Max(8, rows);
                _shell.ChangeWindowSize(columns, rows, columns * 8, rows * 16);
            }
            catch
            {
                // resize is optioneel
            }
        }
    }

    public string Execute(string command, int timeoutSeconds = 20)
    {
        lock (_gate)
        {
            if (_kind == Kind.Local)
                return ExecuteLocal(command, timeoutSeconds);
            EnsureAliveLocked();
            if (_ssh is null)
                throw new InvalidOperationException("Niet verbonden met de Steam Deck.");
            using var cmd = _ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            return cmd.Execute() ?? "";
        }
    }

    public static string ShQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public void Disconnect()
    {
        lock (_gate)
            DisconnectLocked();
    }

    public void Dispose() => Disconnect();

    private T WithSftp<T>(string path, Func<string, T> work)
    {
        EnsureAliveLocked();
        try
        {
            return work(path);
        }
        catch (Exception) when (ActiveProfile is not null)
        {
            ReconnectLocked();
            return work(path);
        }
    }

    private IReadOnlyList<RemoteItem> ListLocked(string path)
    {
        return _sftp!.ListDirectory(path)
            .Where(f => f.Name is not "." and not "..")
            .Select(f => new RemoteItem
            {
                IsDirectory = f.IsDirectory,
                Name = f.Name,
                FullPath = Combine(path, f.Name),
                Size = f.Length,
                LastWrite = f.LastWriteTime
            })
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureDirectoryLocked(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return;
        EnsureAliveLocked();
        if (_sftp!.Exists(path)) return;
        EnsureDirectoryLocked(Parent(path));
        if (!_sftp.Exists(path))
            _sftp.CreateDirectory(path);
    }

    private void DeleteLocked(RemoteItem item)
    {
        if (item.IsDirectory)
            DeleteDirectoryLocked(item.FullPath);
        else
            _sftp!.DeleteFile(item.FullPath);
    }

    private void DeleteDirectoryLocked(string path)
    {
        foreach (var entry in _sftp!.ListDirectory(path).Where(e => e.Name is not "." and not ".."))
        {
            if (entry.IsDirectory)
                DeleteDirectoryLocked(Combine(path, entry.Name));
            else
                _sftp.DeleteFile(Combine(path, entry.Name));
        }
        _sftp.DeleteDirectory(path);
    }

    private void UploadFileLocked(string localPath, string remoteDir, Action<string>? progress, string? remoteName = null)
    {
        var name = string.IsNullOrWhiteSpace(remoteName) ? Path.GetFileName(localPath) : remoteName;
        var remote = Combine(remoteDir, name);
        progress?.Invoke("Uploaden " + name);
        using var fs = File.OpenRead(localPath);
        _sftp!.UploadFile(fs, remote, true);
    }

    private void UploadFolderLocked(string localDir, string remoteDir, Action<string>? progress)
    {
        var name = Path.GetFileName(localDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var target = Combine(remoteDir, name);
        if (!_sftp!.Exists(target))
            _sftp.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(localDir))
            UploadFileLocked(file, target, progress);
        foreach (var dir in Directory.GetDirectories(localDir))
            UploadFolderLocked(dir, target, progress);
    }

    private void UploadContentsLocked(string localDir, string remoteDir, long totalBytes, ref long done,
        Action<double, string>? progress)
    {
        EnsureDirectoryLocked(remoteDir);
        foreach (var file in Directory.GetFiles(localDir))
        {
            var name = Path.GetFileName(file);
            var size = new FileInfo(file).Length;
            var remote = Combine(remoteDir, name);
            progress?.Invoke(Percent(done, totalBytes), "Installeren " + name);
            using var fs = File.OpenRead(file);
            var sentBase = done;
            _sftp!.UploadFile(fs, remote, true, uploaded =>
            {
                progress?.Invoke(Percent(sentBase + (long)uploaded, totalBytes), "Installeren " + name);
            });
            done += size;
        }

        foreach (var dir in Directory.GetDirectories(localDir))
        {
            var name = Path.GetFileName(dir);
            UploadContentsLocked(dir, Combine(remoteDir, name), totalBytes, ref done, progress);
        }
    }

    private static double Percent(long done, long total) =>
        total <= 0 ? 0 : Math.Min(100, done * 100.0 / total);

    private void DownloadFileLocked(string remotePath, string localPath, Action<string>? progress)
    {
        progress?.Invoke("Downloaden " + Path.GetFileName(remotePath));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        using var fs = File.Create(localPath);
        _sftp!.DownloadFile(remotePath, fs);
    }

    private void DownloadItemLocked(RemoteItem item, string localDir, Action<string>? progress)
    {
        var dest = Path.Combine(localDir, item.Name);
        if (!item.IsDirectory)
        {
            DownloadFileLocked(item.FullPath, dest, progress);
            return;
        }
        Directory.CreateDirectory(dest);
        foreach (var child in ListLocked(item.FullPath))
            DownloadItemLocked(child, dest, progress);
    }

    private void EnsureAliveLocked()
    {
        if (_kind == Kind.Local)
        {
            if (ActiveProfile is null)
                throw new InvalidOperationException("Niet verbonden met de Steam Deck.");
            return;
        }
        if (_sftp is { IsConnected: true }) return;
        if (ActiveProfile is null)
            throw new InvalidOperationException("Niet verbonden met de Steam Deck.");
        ReconnectLocked();
    }

    private void ReconnectLocked()
    {
        var profile = ActiveProfile ?? throw new InvalidOperationException("Niet verbonden met de Steam Deck.");
        OpenLocked(profile);
    }

    private void OpenLocked(ConnectionProfile profile)
    {
        CloseClients();
        _kind = Kind.Ssh;

        var ssh = new SshClient(CreateInfo(profile));
        var sftp = new SftpClient(CreateInfo(profile));
        ssh.KeepAliveInterval = TimeSpan.FromSeconds(20);
        sftp.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ssh.Connect();
        sftp.Connect();
        _ssh = ssh;
        _sftp = sftp;
        ActiveProfile = profile;
        try
        {
            StartShell();
        }
        catch (Exception ex)
        {
            ShellOutput?.Invoke("Terminal kon niet starten: " + ex.Message + "\r\n");
        }
    }

    private static ConnectionInfo CreateInfo(ConnectionProfile profile)
    {
        var methods = new List<AuthenticationMethod>();
        try
        {
            var key = SshSecrets.OpenKey(profile.Id);
            if (key is not null)
                methods.Add(new PrivateKeyAuthenticationMethod(profile.User, key));
        }
        catch
        {
            throw new InvalidOperationException(
                "SSH-sleutel of wachtwoordzin ongeldig. Importeer de sleutel opnieuw bij Sessies.");
        }

        var password = SecretStore.Load(SshSecrets.PasswordName(profile.Id));
        if (password.Length > 0)
            methods.Add(new PasswordAuthenticationMethod(profile.User, password));

        if (methods.Count == 0)
            throw new InvalidOperationException(
                "Geen SSH-sleutel of wachtwoord opgeslagen. Importeer een private key bij Sessies.");

        return new ConnectionInfo(profile.Host, profile.Port, profile.User, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    private void DisconnectLocked()
    {
        CloseClients();
        ActiveProfile = null;
        _kind = Kind.None;
    }

    private void CloseClients()
    {
        StopShellLocked();
        try { _sftp?.Disconnect(); } catch { /* ignore */ }
        try { _ssh?.Disconnect(); } catch { /* ignore */ }
        _sftp?.Dispose();
        _ssh?.Dispose();
        _sftp = null;
        _ssh = null;
        try { _localIn?.Dispose(); } catch { /* ignore */ }
        _localIn = null;
        try
        {
            if (_localShell is { HasExited: false })
                _localShell.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }
        try { _localShell?.Dispose(); } catch { /* ignore */ }
        _localShell = null;
    }

    private void StopShellLocked()
    {
        var cts = _shellCts;
        var shell = _shell;
        _shellCts = null;
        _shell = null;
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { shell?.Dispose(); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }
    }

    private void StartShell()
    {
        if (_ssh is null) return;
        StopShellLocked();

        var modes = new Dictionary<TerminalModes, uint>
        {
            [TerminalModes.ECHO] = 1,
            [TerminalModes.ICANON] = 1,
            [TerminalModes.ICRNL] = 1,
            [TerminalModes.ONLCR] = 1,
            [TerminalModes.ISIG] = 1,
            [TerminalModes.IEXTEN] = 1,
            [TerminalModes.OPOST] = 1,
        };

        _shell = _ssh.CreateShellStream("linux", 120, 32, 960, 512, 65536, modes);
        _shellCts = new CancellationTokenSource();
        var shell = _shell;
        var token = _shellCts.Token;
        _ = Task.Run(() => ReadShellLoop(shell, token));
    }

    private void ReadShellLoop(ShellStream shell, CancellationToken token)
    {
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = shell.Read(buffer, 0, buffer.Length);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                if (n <= 0) break;
                var count = decoder.GetChars(buffer, 0, n, chars, 0, flush: false);
                if (count > 0)
                    ShellOutput?.Invoke(new string(chars, 0, count));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
                ShellOutput?.Invoke("\r\n[terminal] " + ex.Message + "\r\n");
        }
        if (!token.IsCancellationRequested)
            ShellOutput?.Invoke("\r\n[terminal gesloten]\r\n");
    }

    private IReadOnlyList<RemoteItem> ListLocal(string path)
    {
        if (!Directory.Exists(path)) return [];
        return Directory.GetFileSystemEntries(path)
            .Select(full =>
            {
                var name = Path.GetFileName(full);
                var unix = full.Replace('\\', '/');
                var dir = Directory.Exists(full);
                long size = 0;
                var write = DateTime.MinValue;
                try
                {
                    if (dir)
                    {
                        write = Directory.GetLastWriteTime(full);
                    }
                    else
                    {
                        var info = new FileInfo(full);
                        size = info.Length;
                        write = info.LastWriteTime;
                    }
                }
                catch { /* metadata is optioneel */ }
                return new RemoteItem
                {
                    IsDirectory = dir,
                    Name = name,
                    FullPath = unix,
                    Size = size,
                    LastWrite = write
                };
            })
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CopyDirectoryLocal(string source, string dest, Action<string>? progress)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            progress?.Invoke("Kopiëren " + name);
            File.Copy(file, Path.Combine(dest, name), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryLocal(dir, Path.Combine(dest, Path.GetFileName(dir)), progress);
    }

    private void CopyContentsLocal(string localDir, string remoteDir, long totalBytes, ref long done,
        Action<double, string>? progress)
    {
        Directory.CreateDirectory(remoteDir);
        foreach (var file in Directory.GetFiles(localDir))
        {
            var name = Path.GetFileName(file);
            var size = new FileInfo(file).Length;
            progress?.Invoke(Percent(done, totalBytes), "Installeren " + name);
            File.Copy(file, Combine(remoteDir, name), overwrite: true);
            done += size;
        }
        foreach (var dir in Directory.GetDirectories(localDir))
            CopyContentsLocal(dir, Combine(remoteDir, Path.GetFileName(dir)), totalBytes, ref done, progress);
    }

    private string ExecuteLocal(string command, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = HostEnvironment.Home
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Kon bash niet starten.");
        if (!proc.WaitForExit(Math.Max(1, timeoutSeconds) * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException("Commando duurde te lang.");
        }
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        return stdout + stderr;
    }

    private void StartLocalShell()
    {
        StopShellLocked();
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-l",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = HostEnvironment.Home
        };
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Kon bash niet starten.");
        _localShell = proc;
        _localIn = proc.StandardInput;
        _shellCts = new CancellationTokenSource();
        var token = _shellCts.Token;
        _ = Task.Run(() => ReadLocalStream(proc.StandardOutput, token), token);
        _ = Task.Run(() => ReadLocalStream(proc.StandardError, token), token);
    }

    private void ReadLocalStream(StreamReader reader, CancellationToken token)
    {
        try
        {
            var buffer = new char[4096];
            while (!token.IsCancellationRequested)
            {
                var n = reader.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                ShellOutput?.Invoke(new string(buffer, 0, n));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
                ShellOutput?.Invoke("\r\n[terminal] " + ex.Message + "\r\n");
        }
    }

    public static string Combine(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || left == "/")
            return "/" + right.TrimStart('/');
        return left.TrimEnd('/') + "/" + right.TrimStart('/');
    }

    public static string Parent(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }
}
