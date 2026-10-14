using System.Security.Cryptography;
using System.Text;

namespace ClawBox.Desktop.Services;

// Each completed input combination gets at most one automatic login attempt.
public sealed class ConnectionCheckGate
{
    private string last = "";
    public int Version { get; private set; }
    public void Invalidate() => Version++;
    public void RetryAfterNetworkSettings() { last = ""; Invalidate(); }
    public bool TryStart(string host, int port, string password, bool networkReady, bool confirmed)
    {
        if (!networkReady || !confirmed || password.Length == 0) return false;
        try { SetupRequest.ValidateAddress(host, port); } catch (SetupException) { return false; }
        string signature = host + ":" + port + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        if (signature == last) return false;
        last = signature; return true;
    }
}
