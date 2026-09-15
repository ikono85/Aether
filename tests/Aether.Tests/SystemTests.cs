using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Aether.Models;
using Aether.Services;
using Aether.Services.Infrastructure;
using Aether.Services.Optimization;

namespace Aether.Tests;

public sealed class RegistryAllowlistTests : IDisposable
{
    private const string Root = @"Software\AetherTests";
    private const string Allowed = Root + @"\Allowed";
    private const string Forbidden = Root + @"\Forbidden";

    private readonly string _dir = TestPaths.NewDir();
    private readonly RestoreStore _store;
    private readonly TestAction _action = new();

    public RegistryAllowlistTests() => _store = new RestoreStore(Path.Combine(_dir, "restore.json"));

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class TestAction : OptimizationAction
    {
        public override string Id => "test_reg";

        protected override bool AllowsRegistryTarget(string hive, string subKey, string valueName) =>
            hive == "HKCU" && subKey == Allowed;

        public override ActionResult Apply(RestoreStore store, CancellationToken ct)
        {
            SetRegistry(store, Registry.CurrentUser, Allowed, "Value", 42, RegistryValueKind.DWord);
            return ActionResult.Applied("");
        }

        public override ActionResult Revert(RestoreStore store) =>
            FinishRevert(store, RevertRegistry(store), "restauré", "rien");

        public void WriteForbidden(RestoreStore store) =>
            SetRegistry(store, Registry.CurrentUser, Forbidden, "Value", 1, RegistryValueKind.DWord);
    }

    private static object? ReadValue(string sub)
    {
        using var key = Registry.CurrentUser.OpenSubKey(sub);
        return key?.GetValue("Value");
    }

    [Fact]
    public void Revert_restores_the_original_value_and_clears_the_backup()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(Allowed)) key.SetValue("Value", 7, RegistryValueKind.DWord);

        _action.Apply(_store, CancellationToken.None);
        Assert.Equal(42, ReadValue(Allowed));

        var result = _action.Revert(_store);

        Assert.Equal(ActionOutcome.Reverted, result.Outcome);
        Assert.Equal(7, ReadValue(Allowed));
        Assert.False(_store.HasBackup("test_reg"));
    }

    [Fact]
    public void Value_created_by_the_action_is_deleted_on_revert()
    {
        _action.Apply(_store, CancellationToken.None);
        _action.Revert(_store);

        Assert.Null(ReadValue(Allowed));
    }

    [Fact]
    public void Undeclared_target_cannot_be_written()
    {
        Assert.Throws<InvalidOperationException>(() => _action.WriteForbidden(_store));
        Assert.Null(ReadValue(Forbidden));
    }

    [Fact]
    public void Forged_restore_entry_is_rejected_instead_of_being_applied()
    {
        _store.Write($"test_reg/reg/HKCU|{Forbidden}|Value", new OptimizationAction.RegBackup(true, "DWord", "1"));

        _action.Revert(_store);

        Assert.Null(ReadValue(Forbidden));
        Assert.False(_store.HasBackup("test_reg"));
    }
}

public class NetworkDiagnosisTests
{
    private static void Answer(NetworkNode node, double rtt, double loss = 0)
    {
        node.Unresolved = false;
        node.EverAnswered = true;
        node.Reachable = true;
        node.Rtt = rtt;
        node.Loss = loss;
    }

    [Fact]
    public void Nothing_measurable_is_not_reported_as_an_outage()
    {
        var v = NetworkDiagnosis.Evaluate(new NetworkService());
        Assert.Equal(1, v.Severity);
        Assert.Equal("DIAGNOSTIC INDISPONIBLE", v.Title);
    }

    [Fact]
    public void Silent_gateway_points_to_the_local_link()
    {
        var net = new NetworkService();
        Answer(net.Gateway, 2);
        Answer(net.Cdn, 30);
        net.Gateway.Reachable = false;

        Assert.Equal("LIEN LOCAL COUPÉ", NetworkDiagnosis.Evaluate(net).Title);
    }

    [Fact]
    public void Loss_to_the_gateway_is_blamed_before_anything_further()
    {
        var net = new NetworkService();
        Answer(net.Gateway, 2, loss: 33);
        Answer(net.Isp, 150);

        Assert.Equal("PERTES SUR LE LIEN LOCAL", NetworkDiagnosis.Evaluate(net).Title);
    }

    [Fact]
    public void Slow_isp_with_healthy_gateway_blames_the_operator()
    {
        var net = new NetworkService();
        Answer(net.Gateway, 2);
        Answer(net.Isp, 120);

        Assert.Equal("COLLECTE OPÉRATEUR DÉGRADÉE", NetworkDiagnosis.Evaluate(net).Title);
    }

    [Fact]
    public void Healthy_chain_is_green()
    {
        var net = new NetworkService();
        Answer(net.Gateway, 2);
        Answer(net.Isp, 10);
        Answer(net.Cdn, 30);

        Assert.Equal(0, NetworkDiagnosis.Evaluate(net).Severity);
    }

    [Fact]
    public void Resetting_a_node_forgets_its_history()
    {
        var node = new NetworkNode();
        Answer(node, 10); node.RecordSample();
        Answer(node, 30); node.RecordSample();
        Assert.True(node.Jitter >= 0);

        node.Reset();

        Assert.False(node.IsMeasurable);
        Assert.Equal(-1, node.Jitter);
        Assert.Equal("", node.Address);
    }
}

public class ConnectionServiceTests
{
    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.64.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("2606:4700::1111", false)]
    [InlineData("::", true)]
    public void Local_addresses_are_not_resolved_on_the_internet(string address, bool expected) =>
        Assert.Equal(expected, ConnectionService.IsLocal(address));

    [Fact]
    public void Port_is_decoded_from_network_byte_order() =>
        Assert.Equal(443, ConnectionService.DecodePort(BitConverter.ToUInt32(new byte[] { 0x01, 0xBB, 0, 0 })));

    [Fact]
    public void Ipv6_endpoint_is_bracketed()
    {
        var c = new ActiveConnection { RemoteAddress = "2606:4700::1111", RemotePort = 443 };
        Assert.Equal("[2606:4700::1111]:443", c.RemoteEndpoint);
    }

    [Fact]
    public void Process_with_a_different_name_under_the_same_pid_is_not_killed()
    {
        var c = new ActiveConnection { Pid = Environment.ProcessId + 0, ProcessName = "definitely-not-this" };
        // Le PID courant appartient au runner de tests : AETHER refuse de se terminer lui-même.
        Assert.DoesNotContain("terminé.", ConnectionService.KillProcess(c));
    }
}

public class NetworkToolboxTests
{
    [Theory]
    [InlineData("Wi-Fi", true)]
    [InlineData("Ethernet 2", true)]
    [InlineData("a\"b", false)]
    [InlineData("", false)]
    [InlineData("eth\n0", false)]
    public void Interface_names_are_validated_before_reaching_netsh(string name, bool expected) =>
        Assert.Equal(expected, NetworkToolbox.IsValidInterfaceName(name));

    [Fact]
    public void Address_families_are_checked()
    {
        Assert.True(NetworkToolbox.IsIPv4("1.1.1.1"));
        Assert.False(NetworkToolbox.IsIPv4("2606:4700:4700::1111"));
        Assert.True(NetworkToolbox.IsIPv6("2606:4700:4700::1111"));
        Assert.False(NetworkToolbox.IsIPv6("1.1.1.1; net user"));
    }

    [Fact]
    public void Every_public_resolver_has_valid_ipv6_addresses()
    {
        foreach (var c in NetworkToolbox.Candidates("192.168.1.1").Where(c => !c.IsCurrent))
        {
            Assert.True(NetworkToolbox.IsIPv6(c.Primary6), c.Name);
            Assert.True(NetworkToolbox.IsIPv6(c.Secondary6), c.Name);
        }
    }
}

public class ProcessRunnerTests
{
    [Fact]
    public void Exit_code_is_propagated() =>
        Assert.Equal(3, ProcessRunner.Run("cmd", new[] { "/c", "exit", "3" }).Code);

    [Fact]
    public void Timeout_is_enforced()
    {
        var sw = Stopwatch.StartNew();
        var r = ProcessRunner.Run("ping", new[] { "-n", "30", "127.0.0.1" }, timeoutMs: 1000);
        Assert.Equal(-1, r.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Cancellation_kills_the_process()
    {
        using var cts = new CancellationTokenSource(500);
        var sw = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() =>
            ProcessRunner.Run("ping", new[] { "-n", "30", "127.0.0.1" }, 60_000, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Argument_with_quotes_stays_a_single_argument()
    {
        var r = ProcessRunner.Run("cmd", new[] { "/c", "echo", "a\" & echo INJECTED" });
        Assert.DoesNotContain(r.Output.Split('\n'), line => line.Trim() == "INJECTED");
    }
}
