using System.Security.Cryptography;
using System.Text.Json;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class PortableConnectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClawBox.Portable.Tests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] HostKey = [0, 0, 0, 11, .. "ssh-ed25519"u8.ToArray(), 0, 0, 0, 32, .. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(HostKey)).TrimEnd('=');
    private static string Known(string host, int port) => (port == 22 ? host : $"[{host}]:{port}") + " ssh-ed25519 " + Convert.ToBase64String(HostKey);
    private string ProfilePath => Path.Combine(root, "connection.json");
    private BoardConnectionProfile Pairing()
    {
        Directory.CreateDirectory(root);
        BoardConnectionProfile profile = new()
        {
            Version = 1,
            BoxHost = "192.168.137.2",
            SshPort = 22,
            SshUser = "root",
            ExecutorPort = 18790,
            RemotePort = 18791,
            KnownHostsFile = Path.Combine(root, "known_hosts"),
            IdentityFile = Path.Combine(root, "key"),
            HostKeyFingerprint = Fingerprint
        };
        File.WriteAllText(profile.KnownHostsFile, Known(profile.BoxHost, profile.SshPort));
        File.WriteAllText(profile.IdentityFile, "unchanged-private-key");
        File.WriteAllText(Path.Combine(root, "gateway.secrets.json"), "unchanged-secret");
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return profile;
    }
    [Fact]
    public async Task First_connection_needs_explicit_trust_before_any_observation()
    {
        BoardIdentity identity = new((_, _, _) => throw new InvalidOperationException("No network access expected."));
        SetupException error = await Assert.ThrowsAsync<SetupException>(() => identity.Resolve("192.168.137.2", 22, ProfilePath, false, CancellationToken.None));
        Assert.Equal("identity", error.Step); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task First_connection_obtains_identity_without_accepting_a_password_parameter_or_writing_configuration()
    {
        bool observed = false;
        BoardIdentity identity = new((host, port, cancellation) => { observed = true; return Task.FromResult(Known(host, port)); });
        SetupRequest request = await identity.Resolve("192.168.137.2", 2222, ProfilePath, true, CancellationToken.None);
        Assert.True(observed); Assert.Equal(Fingerprint, request.Fingerprint); Assert.Equal(2222, request.Port);
        Assert.False(Directory.Exists(root));
    }
    [Theory]
    [InlineData("wrong-host ssh-ed25519 AAAA")]
    [InlineData("192.168.137.2 ssh-rsa AAAA")]
    [InlineData("broken-data")]
    public async Task Invalid_observed_key_never_becomes_a_pairing(string key)
    {
        BoardIdentity identity = new((_, _, _) => Task.FromResult(key));
        await Assert.ThrowsAsync<SetupException>(() => identity.Resolve("192.168.137.2", 22, ProfilePath, true, CancellationToken.None));
        Assert.False(File.Exists(ProfilePath));
    }
    [Fact]
    public async Task Saved_identity_is_reused_at_a_new_address_without_observing_or_trusting_a_new_device()
    {
        Pairing();
        BoardIdentity identity = new((_, _, _) => throw new InvalidOperationException("Saved pin should be used."));
        SetupRequest request = await identity.Resolve("192.168.20.2", 22, ProfilePath, false, CancellationToken.None);
        Assert.Equal(Fingerprint, request.Fingerprint); Assert.Equal("192.168.20.2", request.Host);
        Assert.Equal("192.168.137.2", BoardConnectionProfile.Load(ProfilePath).BoxHost);
    }
    [Fact]
    public async Task New_address_with_the_same_key_updates_only_endpoint_and_known_hosts()
    {
        BoardConnectionProfile previous = Pairing();
        BoardIdentity identity = new((host, port, _) => Task.FromResult(Known(host, port)));
        await identity.UpdateAddress(ProfilePath, "192.168.20.2", 2222, CancellationToken.None);
        BoardConnectionProfile current = BoardConnectionProfile.Load(ProfilePath);
        Assert.Equal("192.168.20.2", current.BoxHost); Assert.Equal(2222, current.SshPort);
        Assert.Equal(previous.IdentityFile, current.IdentityFile); Assert.Equal(previous.HostKeyFingerprint, current.HostKeyFingerprint);
        Assert.Equal(previous.ExecutorPort, current.ExecutorPort); Assert.Equal(previous.RemotePort, current.RemotePort);
        Assert.Equal("unchanged-private-key", File.ReadAllText(current.IdentityFile));
        Assert.Equal("unchanged-secret", File.ReadAllText(Path.Combine(root, "gateway.secrets.json")));
        BoardSetup.ValidateKnownHost(new(current.BoxHost, current.SshPort, current.HostKeyFingerprint), File.ReadAllText(current.KnownHostsFile));
    }
    [Fact]
    public async Task Different_board_at_new_address_cannot_overwrite_original_pairing()
    {
        BoardConnectionProfile profile = Pairing(); string original = File.ReadAllText(ProfilePath), originalKnown = File.ReadAllText(profile.KnownHostsFile);
        BoardIdentity identity = new((_, _, _) => Task.FromResult("192.168.20.2 ssh-ed25519 " + Convert.ToBase64String(new byte[32])));
        await Assert.ThrowsAsync<SetupException>(() => identity.UpdateAddress(ProfilePath, "192.168.20.2", 22, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(ProfilePath)); Assert.Equal(originalKnown, File.ReadAllText(profile.KnownHostsFile));
    }
    [Fact]
    public async Task Address_commit_failure_restores_original_host_key_record()
    {
        BoardConnectionProfile profile = Pairing(); string originalKnown = File.ReadAllText(profile.KnownHostsFile);
        File.SetAttributes(ProfilePath, FileAttributes.ReadOnly);
        BoardIdentity identity = new((host, port, _) => Task.FromResult(Known(host, port)));
        try { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.UpdateAddress(ProfilePath, "192.168.20.2", 22, CancellationToken.None)); }
        finally { File.SetAttributes(ProfilePath, FileAttributes.Normal); }
        Assert.Equal(originalKnown, File.ReadAllText(profile.KnownHostsFile)); Assert.Equal("192.168.137.2", BoardConnectionProfile.Load(ProfilePath).BoxHost);
    }
    [Fact]
    public async Task Damaged_saved_identity_does_not_trigger_new_trust()
    {
        BoardConnectionProfile profile = Pairing(); File.WriteAllText(profile.KnownHostsFile, "changed key");
        BoardIdentity identity = new((_, _, _) => throw new InvalidOperationException("Must not repin."));
        await Assert.ThrowsAsync<SetupException>(() => identity.Resolve("192.168.20.2", 22, ProfilePath, true, CancellationToken.None));
    }
    [Fact]
    public async Task Interrupted_pairing_keeps_its_pinned_identity_at_a_new_address()
    {
        Pairing(); File.Delete(ProfilePath);
        BoardIdentity identity = new((_, _, _) => throw new InvalidOperationException("Must not repin."));
        Assert.Equal(Fingerprint, (await identity.Resolve("192.168.20.2", 22, ProfilePath, false, CancellationToken.None)).Fingerprint);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
