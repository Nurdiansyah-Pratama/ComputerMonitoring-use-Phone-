using System.Text.Json;
using System.Xml.Linq;
#if WINDOWS
using Microsoft.Data.Sqlite;
#endif

namespace PhoneBox.Agent;

/// <summary>
/// Watches Windows 10/11 Action Center toast notifications (wpndatabase.db) and forwards them in real time to
/// connected PhoneBox panels over both Wi-Fi and USB via PushStore ("notify" events) and GET /api/notifications.
/// Reads from a private snapshot copy so it never locks the Windows Push Notifications service.
/// </summary>
public sealed class WindowsNotificationMonitor(PushStore push, ILogger<WindowsNotificationMonitor> log) : BackgroundService
{
    public sealed record PcNotification(long Id, string Key, string App, string Title, string Text, string Level, long At);

    private const int MaxRecent = 15;
    private readonly Lock _gate = new();
    private readonly List<PcNotification> _recent = [];
    private long _lastSeenId;

    public IReadOnlyList<PcNotification> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    internal static (string Title, string Text) ParseToastXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return ("", "");
        try
        {
            var doc = XDocument.Parse(xml);
            var lines = doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase))
                .Select(e => CleanText(e.Value))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            if (lines.Count == 0) return ("", "");
            var title = Truncate(lines[0], 90);
            var body = lines.Count > 1 ? Truncate(string.Join(" · ", lines.Skip(1)), 200) : "";
            return (title, body);
        }
        catch
        {
            return ("", "");
        }
    }

    internal static string CleanText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // Strip invisible directional markers (WhatsApp Desktop inserts U+200E / U+200F) and collapse whitespace
        var cleaned = s.Replace("\u200E", "").Replace("\u200F", "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal)) cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        return cleaned;
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    internal static bool ShouldSkipApp(string? primaryId)
    {
        if (string.IsNullOrWhiteSpace(primaryId)) return false;
        // Spotify track-change toasts duplicate the full Now Playing card already on screen
        if (primaryId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return true;
        if (primaryId.Contains("PhoneBox", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static string FormatAppName(string? primaryId)
    {
        if (string.IsNullOrWhiteSpace(primaryId)) return "Windows";
        var id = primaryId.Trim();
        if (id.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        if (id.Contains("Telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
        if (id.Contains("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (id.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)) return "Antigravity";
        if (id.Contains("Windows.Defender", StringComparison.OrdinalIgnoreCase) || id.Contains("SecurityCenter", StringComparison.OrdinalIgnoreCase)) return "Windows Security";
        if (id.Contains("Slack", StringComparison.OrdinalIgnoreCase)) return "Slack";
        if (id.Contains("Teams", StringComparison.OrdinalIgnoreCase)) return "Teams";
        if (id.Contains("Outlook", StringComparison.OrdinalIgnoreCase) || id.Contains("WindowsCommunicationsApps", StringComparison.OrdinalIgnoreCase)) return "Mail";
        if (id.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase))
        {
            var bang = id.IndexOf('!');
            if (bang > 0 && bang + 1 < id.Length && id[(bang + 1)..].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(id[(bang + 1)..], UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                {
                    var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
                    return $"Edge · {host}";
                }
            }
            return "Edge";
        }
        if (id.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (id.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";

        // Generic AUMID cleanup: "5319275A.SomeApp_hash!App" -> "SomeApp"
        var beforeBang = id.Split('!')[0];
        var beforeUnderscore = beforeBang.Split('_')[0];
        var lastPart = beforeUnderscore.Split('.').LastOrDefault(p => !string.IsNullOrWhiteSpace(p) && !p.Equals("App", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(lastPart) ? "Windows" : Truncate(lastPart, 36);
    }

    internal static string InferLevel(string? primaryId, string title, string text)
    {
        var combined = $"{primaryId} {title} {text}";
        if (combined.Contains("SecurityCenter", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("warning", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return "warn";
        if (combined.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("error", StringComparison.OrdinalIgnoreCase))
            return "error";
        return "info";
    }

    /// <summary>Converts Windows FILETIME (100-ns ticks since 1601-01-01 UTC) to Unix milliseconds.</summary>
    internal static long FileTimeToUnixMs(long fileTime)
    {
        if (fileTime <= 0) return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try { return DateTimeOffset.FromFileTime(fileTime).ToUnixTimeMilliseconds(); }
        catch { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    }

#if WINDOWS
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications");
        var dbPath = Path.Combine(dir, "wpndatabase.db");
        if (!File.Exists(dbPath))
        {
            log.LogInformation("WindowsNotificationMonitor: wpndatabase.db not found, skipping");
            return;
        }

        // Initial seed: record existing notifications without spamming popups for old ones,
        // while keeping the latest ones in Recent() for GET /api/notifications.
        try
        {
            var initial = ReadLatestToasts(dbPath, 0, MaxRecent);
            if (initial.Count > 0)
            {
                _lastSeenId = initial.Max(n => n.Id);
                lock (_gate)
                {
                    _recent.Clear();
                    _recent.AddRange(initial.OrderByDescending(n => n.At).Take(MaxRecent));
                }
            }
            log.LogInformation("WindowsNotificationMonitor: watching Windows notifications (seeded {Count}, lastId={Id})", initial.Count, _lastSeenId);
        }
        catch (Exception ex)
        {
            log.LogWarning("WindowsNotificationMonitor: initial read failed: {Msg}", ex.Message);
        }

        var lastStamp = GetDbStamp(dbPath);
        using var wake = new SemaphoreSlim(0, 1);
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(dir, "wpndatabase.db*")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            void Poke(object _, FileSystemEventArgs __) { try { if (wake.CurrentCount == 0) wake.Release(); } catch { } }
            watcher.Changed += Poke;
            watcher.Created += Poke;
        }
        catch { /* fallback to polling below */ }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await wake.WaitAsync(TimeSpan.FromMilliseconds(1500), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

                // Brief debounce so Windows finishes committing WAL before snapshot copy
                var stamp = GetDbStamp(dbPath);
                if (stamp == lastStamp) continue;
                await Task.Delay(180, stoppingToken);
                lastStamp = GetDbStamp(dbPath);

                try
                {
                    var fresh = ReadLatestToasts(dbPath, _lastSeenId, 20);
                    if (fresh.Count == 0) continue;
                    foreach (var n in fresh.OrderBy(x => x.Id))
                    {
                        if (n.Id <= _lastSeenId) continue;
                        _lastSeenId = n.Id;
                        lock (_gate)
                        {
                            _recent.RemoveAll(x => x.Key == n.Key);
                            _recent.Insert(0, n);
                            if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
                        }
                        var payload = JsonSerializer.SerializeToElement(new
                        {
                            key = n.Key,
                            source = "pc",
                            app = n.App,
                            title = n.Title,
                            text = n.Text,
                            level = n.Level,
                            seconds = 25,
                            vibrate = true,
                            at = n.At,
                        });
                        push.Emit("notify", payload);
                        log.LogInformation("WindowsNotificationMonitor: forwarded [{App}] {Title}", n.App, n.Title);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    log.LogDebug("WindowsNotificationMonitor: poll error: {Msg}", ex.Message);
                }
            }
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    private static long GetDbStamp(string dbPath)
    {
        long stamp = 0;
        foreach (var path in new[] { dbPath, dbPath + "-wal" })
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) stamp ^= fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 17);
            }
            catch { }
        }
        return stamp;
    }

    private static void CopyShared(string src, string dst)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static List<PcNotification> ReadLatestToasts(string dbPath, long afterId, int limit)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "phonebox-wpn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var copyDb = Path.Combine(tempDir, "wpndatabase.db");
        try
        {
            CopyShared(dbPath, copyDb);
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                var side = dbPath + ext;
                if (File.Exists(side))
                {
                    try { CopyShared(side, copyDb + ext); } catch { }
                }
            }

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = copyDb,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();

            var list = new List<PcNotification>();
            using var con = new SqliteConnection(cs);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT n.Id, n.ArrivalTime, h.PrimaryId, CAST(n.Payload AS TEXT)
                FROM Notification n
                LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                WHERE n.Type = 'toast' AND n.Id > $afterId
                ORDER BY n.Id DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$afterId", afterId);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var arrival = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                var primaryId = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var xml = reader.IsDBNull(3) ? "" : reader.GetString(3);
                if (ShouldSkipApp(primaryId)) continue;
                var (title, text) = ParseToastXml(xml);
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(text)) continue;
                var app = FormatAppName(primaryId);
                var level = InferLevel(primaryId, title, text);
                list.Add(new PcNotification(id, $"win:{id}", app, title, text, level, FileTimeToUnixMs(arrival)));
            }
            return list;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
#else
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
#endif
}
