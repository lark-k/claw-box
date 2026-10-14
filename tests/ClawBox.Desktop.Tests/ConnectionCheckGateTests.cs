using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class ConnectionCheckGateTests
{
    [Fact]
    public void Automatic_checks_wait_for_complete_input_reachability_and_consent()
    {
        ConnectionCheckGate gate = new();
        Assert.False(gate.TryStart("192.168.137.2", 22, "password", false, true));
        Assert.False(gate.TryStart("192.168.137.2", 22, "password", true, false));
        Assert.False(gate.TryStart("192.168.137.2", 22, "", true, true));
        Assert.False(gate.TryStart("", 22, "password", true, true));
        Assert.True(gate.TryStart("192.168.137.2", 22, "password", true, true));
    }
    [Fact]
    public void Repeated_focus_changes_and_timer_ticks_do_not_repeat_login_even_after_failure()
    {
        ConnectionCheckGate gate = new();
        Assert.True(gate.TryStart("192.168.137.2", 22, "password", true, true));
        for (int i = 0; i < 10; i++) { gate.Invalidate(); Assert.False(gate.TryStart("192.168.137.2", 22, "password", true, true)); }
        Assert.True(gate.TryStart("192.168.137.2", 22, "corrected-password", true, true));
        Assert.True(gate.TryStart("192.168.137.3", 22, "corrected-password", true, true));
    }
    [Fact]
    public void Input_revision_invalidates_results_from_previous_checks()
    {
        ConnectionCheckGate gate = new(); int old = gate.Version;
        gate.Invalidate(); Assert.NotEqual(old, gate.Version);
    }
    [Fact]
    public void Explicit_network_settings_can_enable_one_read_only_retry()
    {
        ConnectionCheckGate gate = new();
        Assert.True(gate.TryStart("192.168.137.2", 22, "password", true, true));
        Assert.False(gate.TryStart("192.168.137.2", 22, "password", true, true));
        gate.RetryAfterNetworkSettings();
        Assert.True(gate.TryStart("192.168.137.2", 22, "password", true, true));
        Assert.False(gate.TryStart("192.168.137.2", 22, "password", true, true));
    }
    [Theory]
    [InlineData("manual", true, "ready")]
    [InlineData("manual", false, "missing")]
    [InlineData("auto", true, "missing")]
    [InlineData("unknown", false, "missing")]
    public void Live_address_is_not_confused_with_a_persistent_static_configuration(string method, bool persistent, string expected)
    {
        BoardNetwork network = new("192.168.137.2", "eth0", method, ["192.168.137.2/24"], persistent, "192.168.137.1");
        Assert.Equal(expected, network.Describe().Status);
    }
    [Fact]
    public void Persistent_address_for_a_different_interface_does_not_confirm_current_address()
        => Assert.Equal("missing", new BoardNetwork("192.168.137.2", "eth0", "manual", ["192.168.10.2/24"], true, "").Describe().Status);
}
