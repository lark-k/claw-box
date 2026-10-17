using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

// Startup recovery reads network state only. It never submits Wi-Fi or initialization writes.
public sealed class WirelessReconnect(
    Func<string, int, CancellationToken, Task<string>>? observe = null,
    Func<BoardConnectionProfile, CancellationToken, Task<string?>>? locate = null,
    TimeSpan? recoveryTimeout = null)
{
    private readonly Func<string, int, CancellationToken, Task<string>> observe = observe ?? SshBoardSetupTransport.ObserveHostKey;
    private readonly Func<BoardConnectionProfile, CancellationToken, Task<string?>> locate = locate ?? Locate;

    public async Task<bool> Resolve(DesktopSettings settings, CancellationToken cancellation, Action<string>? progress = null)
    {
        if (settings.ConnectionMode != "wireless") return false;
        BoardConnectionProfile profile = BoardConnectionProfile.Load(settings.ProfilePath);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(recoveryTimeout ?? TimeSpan.FromSeconds(30));
        try
        {
            try
            {
                string known = await Probe(profile.BoxHost, profile.SshPort, deadline.Token);
                BoardSetup.ValidateKnownHost(new(profile.BoxHost, profile.SshPort, profile.HostKeyFingerprint), known);
                return false;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { deadline.Token.ThrowIfCancellationRequested(); }

            progress?.Invoke("正在检查原盒子的无线网络；若当前网络尚未配置，将提示设置 Wi-Fi…");
            string? host = await locate(profile, deadline.Token).WaitAsync(deadline.Token);
            if (string.IsNullOrEmpty(host)) throw new WirelessException("wifi_setup_required");
            SetupRequest.ValidateAddress(host, 22);
            string verified;
            try { verified = await Probe(host, 22, deadline.Token); }
            catch (SetupException error) when (error.Step == "network") { throw new WirelessException("wifi_setup_required"); }
            BoardSetup.ValidateKnownHost(new(host, 22, profile.HostKeyFingerprint), verified);
            deadline.Token.ThrowIfCancellationRequested();
            await new BoardIdentity((_, _, _) => Task.FromResult(verified)).UpdateAddress(settings.ProfilePath, host, 22, deadline.Token);
            settings.WirelessHost = host; settings.WirelessPort = 22;
            return true;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new WirelessException("wifi_setup_required"); }
    }

    private async Task<string> Probe(string host, int port, CancellationToken cancellation)
    {
        using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        probe.CancelAfter(TimeSpan.FromSeconds(4));
        return await observe(host, port, probe.Token).WaitAsync(probe.Token);
    }

    private static async Task<string?> Locate(BoardConnectionProfile profile, CancellationToken cancellation)
    {
        foreach (NearbyBoard board in await BluetoothNetwork.Discover(cancellation))
        {
            using BluetoothNetwork link = await BluetoothNetwork.Connect(board.Address, cancellation);
            System.Text.Json.JsonElement info = await link.Info(cancellation);
            if (info.GetProperty("identity").GetString() != profile.HostKeyFingerprint) continue;
            using WirelessSession session = new(link, new(profile.IdentityFile, profile.HostKeyFingerprint, false));
            await session.Authenticate(cancellation);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                WirelessStatus status = await session.Status(cancellation);
                if (status.Connected && status.Addresses.FirstOrDefault() is string address) return address;
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            }
            return null;
        }
        throw new WirelessException("bluetooth_connection");
    }
}
