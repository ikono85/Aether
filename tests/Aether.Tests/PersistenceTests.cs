using System.IO;
using System.ServiceProcess;
using Aether.Services.Infrastructure;
using Aether.Services.Optimization;
using Aether.Services.WindowsServices;

namespace Aether.Tests;

internal static class TestPaths
{
    public static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aether-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class SafeFileTests : IDisposable
{
    private readonly string _dir = TestPaths.NewDir();
    private string PathOf(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_is_reported_as_missing()
    {
        var (value, status) = SafeFile.ReadJson<Dictionary<string, string>>(PathOf("none.json"));
        Assert.Null(value);
        Assert.Equal(LoadStatus.Missing, status);
    }

    [Fact]
    public void Second_write_keeps_previous_version_as_bak()
    {
        var path = PathOf("a.json");
        SafeFile.WriteJson(path, new Dictionary<string, string> { ["k"] = "1" });
        SafeFile.WriteJson(path, new Dictionary<string, string> { ["k"] = "2" });

        var (value, status) = SafeFile.ReadJson<Dictionary<string, string>>(path);
        Assert.Equal(LoadStatus.Loaded, status);
        Assert.Equal("2", value!["k"]);
        Assert.Contains("\"1\"", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Truncated_file_is_recovered_from_bak_and_quarantined()
    {
        var path = PathOf("b.json");
        SafeFile.WriteJson(path, new Dictionary<string, string> { ["k"] = "1" });
        SafeFile.WriteJson(path, new Dictionary<string, string> { ["k"] = "2" });
        File.WriteAllText(path, "{\"k\": \"2");

        var (value, status) = SafeFile.ReadJson<Dictionary<string, string>>(path);
        Assert.Equal(LoadStatus.RecoveredFromBackup, status);
        Assert.Equal("1", value!["k"]);
        Assert.Single(Directory.GetFiles(_dir, "b.json.corrupt-*"));
    }

    [Fact]
    public void Fully_corrupt_file_is_reported_and_never_overwritten()
    {
        var path = PathOf("c.json");
        File.WriteAllText(path, "garbage");
        File.WriteAllText(path + ".bak", "garbage");

        var (value, status) = SafeFile.ReadJson<Dictionary<string, string>>(path);
        Assert.Null(value);
        Assert.Equal(LoadStatus.Corrupt, status);
        Assert.Equal("garbage", File.ReadAllText(path));
    }

    [Fact]
    public void Delete_removes_bak_so_the_file_does_not_come_back()
    {
        var path = PathOf("d.json");
        SafeFile.WriteJson(path, new Dictionary<string, string>());
        SafeFile.WriteJson(path, new Dictionary<string, string>());

        SafeFile.Delete(path);

        Assert.Equal(LoadStatus.Missing, SafeFile.ReadJson<Dictionary<string, string>>(path).Status);
    }
}

public sealed class RestoreStoreTests : IDisposable
{
    private readonly string _dir = TestPaths.NewDir();
    private string StorePath => Path.Combine(_dir, "restore.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Entries_survive_a_restart()
    {
        new RestoreStore(StorePath).Write("mod/key", "value");

        var reloaded = new RestoreStore(StorePath);
        Assert.Equal("value", reloaded.Read<string>("mod/key"));
        Assert.True(reloaded.HasBackup("mod"));
    }

    [Fact]
    public void Corrupt_journal_becomes_read_only_instead_of_being_emptied()
    {
        File.WriteAllText(StorePath, "{broken");
        File.WriteAllText(StorePath + ".bak", "{broken");

        var store = new RestoreStore(StorePath);

        Assert.True(store.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => store.Write("mod/key", 1));
        Assert.Equal("{broken", File.ReadAllText(StorePath));
    }

    [Fact]
    public void Clear_only_removes_the_given_module()
    {
        var store = new RestoreStore(StorePath);
        store.Write("a/1", 1);
        store.Write("ab/1", 1);

        store.Clear("a");

        Assert.False(store.HasBackup("a"));
        Assert.True(store.HasBackup("ab"));
    }
}

public sealed class ServiceChangeLogTests : IDisposable
{
    private static readonly string[] Catalog = { "Spooler", "CDPUserSvc", "Fax" };
    private readonly string _dir = TestPaths.NewDir();
    private string LogPath => Path.Combine(_dir, "service-changes.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Original_start_type_survives_round_trips_and_restarts()
    {
        var log = new ServiceChangeLog(Catalog, LogPath);
        log.Record("Spooler", ServiceStartMode.Automatic, ServiceStartMode.Disabled);
        log.Record("Spooler", ServiceStartMode.Disabled, ServiceStartMode.Manual);

        var reloaded = new ServiceChangeLog(Catalog, LogPath);
        Assert.Equal(ServiceStartMode.Automatic, reloaded.OriginalStartType("Spooler"));
    }

    [Fact]
    public void Per_user_service_is_found_again_after_its_session_suffix_changes()
    {
        var log = new ServiceChangeLog(Catalog, LogPath);
        log.Record("CDPUserSvc_4a1b2", ServiceStartMode.Automatic, ServiceStartMode.Disabled);

        var nextSession = new ServiceChangeLog(Catalog, LogPath);
        Assert.Equal(new[] { "CDPUserSvc" }, nextSession.ChangedServices());
        Assert.Equal(ServiceStartMode.Automatic, nextSession.OriginalStartType("CDPUserSvc_99zz9"));
    }

    [Fact]
    public void Service_outside_the_catalog_is_refused()
    {
        var log = new ServiceChangeLog(Catalog, LogPath);
        Assert.Throws<ArgumentException>(() =>
            log.Record("EvilSvc", ServiceStartMode.Disabled, ServiceStartMode.Automatic));
    }

    [Fact]
    public void Forged_entry_on_disk_is_dropped_at_load()
    {
        File.WriteAllText(LogPath,
            "[{\"ServiceName\":\"EvilSvc\",\"OldStartType\":2,\"NewStartType\":4,\"Timestamp\":\"2026-01-01T00:00:00\"}]");

        var log = new ServiceChangeLog(Catalog, LogPath);

        Assert.Empty(log.ChangedServices());
    }

    [Fact]
    public void Discarded_entry_is_not_kept()
    {
        var log = new ServiceChangeLog(Catalog, LogPath);
        var entry = log.Record("Fax", ServiceStartMode.Manual, ServiceStartMode.Disabled);

        log.Discard(entry);

        Assert.Empty(new ServiceChangeLog(Catalog, LogPath).ChangedServices());
    }

    [Fact]
    public void Corrupt_journal_blocks_new_changes()
    {
        File.WriteAllText(LogPath, "[{");
        File.WriteAllText(LogPath + ".bak", "[{");

        var log = new ServiceChangeLog(Catalog, LogPath);

        Assert.NotNull(log.LoadError);
        Assert.Throws<InvalidOperationException>(() =>
            log.Record("Fax", ServiceStartMode.Manual, ServiceStartMode.Disabled));
    }
}
