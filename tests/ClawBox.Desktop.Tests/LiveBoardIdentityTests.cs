using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class LiveBoardFactAttribute : FactAttribute
{
    public LiveBoardFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAWBOX_BOARD_PROFILE"))) Skip = "Opt-in public host-key observation requires an existing local device pairing.";
    }
}
public sealed class LiveBoardIdentityTests
{
    [LiveBoardFact]
    public async Task Public_key_observation_matches_existing_pairing_without_login_password_or_tools()
    {
        string profilePath = Environment.GetEnvironmentVariable("CLAWBOX_BOARD_PROFILE")!;
        BoardConnectionProfile profile = BoardConnectionProfile.Load(profilePath);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        string known = await SshBoardSetupTransport.ObserveHostKey(profile.BoxHost, profile.SshPort, deadline.Token);
        BoardSetup.ValidateKnownHost(new(profile.BoxHost, profile.SshPort, profile.HostKeyFingerprint), known);
    }
}
