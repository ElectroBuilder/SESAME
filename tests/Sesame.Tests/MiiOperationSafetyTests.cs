using Sesame.Services;
using Sesame.Services.Mii;

namespace Sesame.Tests;

public sealed class MiiOperationSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sesame-exclusive-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProductionLocalWriteNewRejectsCollisionWithoutChangingExistingBytes()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "staging.bin");
        ExclusiveFileCreate.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<IOException>(() => ExclusiveFileCreate.WriteAllBytes(path, [9, 9, 9]));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public void ProductionLocalWriteNewRejectsSymlinkWithoutChangingVictim()
    {
        Directory.CreateDirectory(_root);
        var victim = Path.Combine(_root, "victim.bin");
        var link = Path.Combine(_root, "staging-link.bin");
        File.WriteAllBytes(victim, [4, 5, 6]);
        try { File.CreateSymbolicLink(link, victim); }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) == 1314)
        {
            // Some Windows hosts disable symlink creation. The collision test above still exercises
            // the identical FileMode.CreateNew production path; do not replace it with weaker Exists+write.
            return;
        }
        Assert.Throws<IOException>(() => ExclusiveFileCreate.WriteAllBytes(link, [9, 9, 9]));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(victim));
    }

    [Fact]
    public void ProductionRemoteWriteNewPassesExclusiveFlagAndPreservesCollision()
    {
        var existing = new byte[] { 7, 8, 9 };
        var canOverrideSeen = true;
        Assert.Throws<IOException>(() => ExclusiveFileCreate.Upload([1, 2, 3], (stream, canOverride) =>
        {
            canOverrideSeen = canOverride;
            if (!canOverride) throw new IOException("SSH_FXF_EXCL collision");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            existing = copy.ToArray();
        }));
        Assert.False(canOverrideSeen);
        Assert.Equal([7, 8, 9], existing);
    }

    [Fact]
    public void OperationLockBlocksSecondMutationDisconnectAndExitUntilReleased()
    {
        var gate = new MiiOperationLock();
        var states = new List<bool>();
        gate.StateChanged += states.Add;
        Assert.True(gate.TryBegin());
        Assert.False(gate.TryBegin());
        Assert.False(gate.CanStartMutation);
        Assert.False(gate.CanDisconnect);
        Assert.False(gate.CanClose);
        gate.End();
        Assert.True(gate.CanStartMutation);
        Assert.True(gate.CanDisconnect);
        Assert.True(gate.CanClose);
        Assert.Equal([true, false], states);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
