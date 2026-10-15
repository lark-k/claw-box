using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class LiveTunnelLifecycleTests
{
    [LiveBoardFact]
    public async Task Normal_disconnect_releases_both_listeners_for_immediate_reconnect()
    {
        BoardConnectionProfile profile = BoardConnectionProfile.Load(Environment.GetEnvironmentVariable("CLAWBOX_BOARD_PROFILE")!);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using SshTunnel tunnel = new(profile);
            await tunnel.Connect(deadline.Token);
            Assert.True(tunnel.Alive);
            tunnel.Dispose(); tunnel.Dispose();
            Assert.False(tunnel.Alive);
            // The next real SSH request must immediately reclaim the same remote port.
        }
    }
}
