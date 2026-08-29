using Sesame.Models;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck;

public sealed class DeckSession
{
    public static DeckSession Current { get; } = new();

    public DeckClient Client { get; } = new();
    public AppCatalog Catalog { get; } = new();
    public ProfileStore Profiles { get; } = new();
    public GameLibrary Library { get; } = new();

    public event Action? Changed;

    public bool Connected => Client.IsConnected;
    public bool Local => Client.IsLocal;
    public string Status =>
        !Client.IsConnected ? "Not connected"
        : Client.IsLocal ? ""
        : "SSH · " + (Client.ActiveProfile?.Name ?? Client.ActiveProfile?.Host ?? "");

    public DeckSession()
    {
        Profiles.Load(Catalog.Profiles);
    }

    public async Task ConnectLocalAsync()
    {
        await Task.Run(() =>
        {
            Client.ConnectLocal();
            LibraryLayout.Ensure(Client, Catalog);
        });
        Changed?.Invoke();
    }

    public async Task ConnectRemoteAsync(ConnectionProfile profile)
    {
        await Task.Run(() =>
        {
            Client.Connect(profile);
            LibraryLayout.Ensure(Client, Catalog);
        });
        Changed?.Invoke();
    }

    public void Disconnect()
    {
        Client.Disconnect();
        Changed?.Invoke();
    }

    public async Task EnsureConnectedAsync()
    {
        if (Client.IsConnected) return;
        if (HostEnvironment.LocalAvailable && !HostEnvironment.ForceRemote)
            await ConnectLocalAsync();
    }
}
