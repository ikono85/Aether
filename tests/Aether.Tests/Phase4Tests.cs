using Aether.Services;
using Aether.Services.History;
using Aether.Services.Infrastructure;
using Aether.ViewModels;

namespace Aether.Tests;

public class NetworkTimelineTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0);

    private static NetworkSample S(int seconds, bool outage = false, double ping = 20) =>
        new(T0.AddSeconds(seconds), outage ? double.NaN : ping, 0, 2, 1, 1, outage);

    [Fact]
    public void Outage_lasts_from_first_down_sample_to_recovery()
    {
        var t = new NetworkTimeline();
        foreach (var s in new[] { S(0), S(5, true), S(10, true), S(15, true), S(20) }) t.Add(s);

        var outage = Assert.Single(t.Outages());
        Assert.Equal(T0.AddSeconds(5), outage.Start);
        Assert.Equal(TimeSpan.FromSeconds(15), outage.Duration);
    }

    [Fact]
    public void Gap_in_measurements_is_not_counted_as_an_outage()
    {
        var t = new NetworkTimeline();
        foreach (var s in new[] { S(0, true), S(5, true), S(3600, false) }) t.Add(s);

        var outage = Assert.Single(t.Outages());
        Assert.Equal(T0.AddSeconds(5), outage.End);   // clôturée au dernier relevé coupé, pas une heure plus tard
    }

    [Fact]
    public void Ongoing_outage_is_reported()
    {
        var t = new NetworkTimeline();
        foreach (var s in new[] { S(0), S(5, true), S(10, true) }) t.Add(s);

        Assert.Single(t.Outages());
        Assert.Equal(100.0 / 3, t.AvailabilityPercent, 3);
    }

    [Fact]
    public void Samples_older_than_24_hours_are_dropped()
    {
        var t = new NetworkTimeline();
        t.Add(S(0));
        t.Add(S((int)TimeSpan.FromHours(25).TotalSeconds));

        Assert.Single(t.Samples);
    }

    [Fact]
    public void Csv_round_trip_keeps_values_and_missing_measurements()
    {
        var original = new NetworkSample(T0, 23.5, double.NaN, 2, 95.4, 12, true);
        var line = NetworkTimeline.ToCsvLine(original);

        Assert.True(NetworkTimeline.TryParseCsvLine(line, out var parsed));
        Assert.Equal(original.Time, parsed.Time);
        Assert.Equal(23.5, parsed.PingMs);
        Assert.True(double.IsNaN(parsed.LossPct));
        Assert.True(parsed.Outage);
        Assert.False(NetworkTimeline.TryParseCsvLine("garbage", out _));
    }

    [Theory]
    [InlineData(3, "moins de 5 s")]
    [InlineData(42, "42 s")]
    [InlineData(125, "2 min 05 s")]
    [InlineData(3900, "1 h 05 min")]
    public void Outage_durations_are_readable(int seconds, string expected) =>
        Assert.Equal(expected, HistoryViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));
}

public class DiagnosticsMaskTests
{
    [Fact]
    public void Public_ipv4_is_masked_but_local_addresses_are_kept()
    {
        var masked = Diagnostics.Mask("passerelle 192.168.1.254, FAI 81.12.34.56, DNS 1.1.1.1, box 10.0.0.1");
        Assert.Contains("192.168.1.254", masked);
        Assert.Contains("10.0.0.1", masked);
        Assert.DoesNotContain("81.12.34.56", masked);
        Assert.DoesNotContain("1.1.1.1", masked);
    }

    [Fact]
    public void Public_ipv6_is_masked_and_timestamps_are_untouched()
    {
        var masked = Diagnostics.Mask("2026-09-15 12:34:56.789 WARN route 2a01:cb00:1234:5600::1 via fe80::1");
        Assert.Contains("12:34:56.789", masked);
        Assert.Contains("fe80::1", masked);
        Assert.DoesNotContain("2a01:cb00", masked);
    }

    [Fact]
    public void User_and_machine_names_are_masked()
    {
        var text = $@"C:\Users\{Environment.UserName}\AppData sur {Environment.MachineName}";
        var masked = Diagnostics.Mask(text);
        Assert.DoesNotContain(Environment.MachineName, masked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<pc>", masked);
    }
}

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("1.3", 1, 3, -1)]
    [InlineData("V2.0.1-beta", 2, 0, 1)]
    public void Release_tags_are_parsed(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseTag(tag, out var v));
        Assert.Equal(new Version(major, minor, build < 0 ? 0 : build).ToString(3), new Version(v.Major, v.Minor, Math.Max(0, v.Build)).ToString(3));
    }

    [Fact]
    public void Unreadable_tag_is_rejected() => Assert.False(UpdateService.TryParseTag("latest", out _));

    [Fact]
    public void Only_a_strictly_newer_version_is_offered()
    {
        Assert.True(UpdateService.IsNewer(new Version(1, 3), new Version(1, 2, 0, 0)));
        Assert.False(UpdateService.IsNewer(new Version(1, 2), new Version(1, 2, 0, 0)));
        Assert.False(UpdateService.IsNewer(new Version(1, 1, 9), new Version(1, 2, 0, 0)));
    }
}

public class CommandLineTests
{
    [Fact]
    public void No_argument_starts_the_interface() => Assert.Null(CommandLine.TryParse(Array.Empty<string>()));

    [Fact]
    public void Known_commands_are_parsed()
    {
        Assert.Equal("--restore-all", CommandLine.TryParse(new[] { "--RESTORE-ALL" })!.Verb);

        var apply = CommandLine.TryParse(new[] { "--apply", "visualfx" })!;
        Assert.Equal("--apply", apply.Verb);
        Assert.Equal("visualfx", apply.Argument);
    }

    [Fact]
    public void Unknown_argument_is_flagged_instead_of_opening_the_interface() =>
        Assert.Equal("--invalid", CommandLine.TryParse(new[] { "/foo" })!.Verb);
}
