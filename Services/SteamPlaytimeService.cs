using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LocalAchievements.Services
{
    public enum PlaytimeSyncResult
    {
        Synced,
        SkippedNotHigher,
        NoSteamTime,
        NotSupported,
        Error
    }

    public class SteamPlaytimeService
    {
        private readonly IPlayniteAPI _api;
        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, CachedApps> appsCache = new Dictionary<string, CachedApps>(StringComparer.OrdinalIgnoreCase);
        private static bool warnedUnknownAccount;

        private class CachedApps
        {
            public DateTime WriteTime;
            public Dictionary<string, object> Apps;
        }

        private class AppTime
        {
            public ulong Minutes;
            public DateTime? LastPlayed;
        }

        public SteamPlaytimeService(IPlayniteAPI api)
        {
            _api = api;
        }

        public bool IsSteamRunning()
        {
            return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;
        }

        public bool RestartSteamAndApply(Game game, ulong minutes, out string error)
        {
            error = null;

            try
            {
                string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString()?.Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(steamPath))
                {
                    error = "Steam installation path not found.";
                    return false;
                }

                string steamExe = Path.Combine(steamPath, "steam.exe");
                if (!File.Exists(steamExe))
                {
                    error = "steam.exe not found.";
                    return false;
                }

                if (IsSteamRunning())
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = steamExe,
                        Arguments = "-shutdown",
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    });

                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (IsSteamRunning() && DateTime.UtcNow < deadline)
                    {
                        System.Threading.Thread.Sleep(250);
                    }

                    if (IsSteamRunning())
                    {
                        error = "Steam did not close in time. Close Steam manually and try again.";
                        return false;
                    }

                    System.Threading.Thread.Sleep(500);
                }

                if (!SetSteamPlaytime(game, minutes, out error, out _))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(steamExe) { UseShellExecute = true }); } catch { }
                    return false;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(steamExe) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AdvancedLogger.Log($"[HORAS] ERRO ao reiniciar Steam para editar horas de {game?.Name}: {ex.Message}");
                return false;
            }
        }

        public ulong? GetSteamPlaytimeMinutes(Game game)
        {
            try
            {
                if (game == null || string.IsNullOrEmpty(game.GameId)) return null;

                string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString()?.Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(steamPath)) return null;

                string userdata = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(userdata)) return null;

                string me = SteamAccount.Resolve(_api, steamPath, out _);
                if (me == null) return null;

                var mine = ReadAppTime(userdata, me, game.GameId);
                return mine != null ? (ulong?)mine.Minutes : null;
            }
            catch { return null; }
        }

        public bool SetSteamPlaytime(Game game, ulong minutes, out string error, out bool steamRunning)
        {
            error = null;
            steamRunning = System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;

            try
            {
                if (game == null || string.IsNullOrEmpty(game.GameId))
                {
                    error = "Invalid game or missing Steam AppID.";
                    return false;
                }

                if (minutes > 6000000UL)
                {
                    error = "Invalid playtime value.";
                    return false;
                }

                string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString()?.Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(steamPath))
                {
                    error = "Steam installation path not found.";
                    return false;
                }

                string userdata = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(userdata))
                {
                    error = "Steam userdata folder not found.";
                    return false;
                }

                string me = SteamAccount.Resolve(_api, steamPath, out _);
                if (me == null)
                {
                    error = "Could not identify your active Steam account.";
                    return false;
                }

                string cfg = Path.Combine(userdata, me, "config", "localconfig.vdf");
                if (!File.Exists(cfg))
                {
                    error = "localconfig.vdf not found for your Steam account.";
                    return false;
                }

                string text;
                using (var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();

                if (!VdfText.SetAppPlaytime(text, game.GameId, minutes, out string updated))
                {
                    error = "Could not locate the Steam apps section in localconfig.vdf.";
                    return false;
                }

                try { File.Copy(cfg, cfg + ".bak", true); } catch { }

                File.WriteAllText(cfg, updated, new UTF8Encoding(false));

                lock (cacheLock)
                {
                    appsCache.Remove(cfg);
                }

                _api.MainView.UIDispatcher.Invoke(() =>
                {
                    game.Playtime = minutes * 60UL;
                    _api.Database.Games.Update(game);
                });

                AdvancedLogger.Log($"[HORAS] {game.Name}: Steam playtime set to {minutes} min ({minutes / 60}h {minutes % 60:00}m) in localconfig.vdf.");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AdvancedLogger.Log($"[HORAS] ERRO ao editar horas de {game?.Name}: {ex.Message}");
                return false;
            }
        }

        public PlaytimeSyncResult Sync(Game game)
        {
            try
            {
                if (string.IsNullOrEmpty(game.GameId)) return PlaytimeSyncResult.NotSupported;

                string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString()?.Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(steamPath)) return PlaytimeSyncResult.NotSupported;

                string userdata = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(userdata)) return PlaytimeSyncResult.NotSupported;

                string me = SteamAccount.Resolve(_api, steamPath, out _);
                if (me == null)
                {
                    if (!warnedUnknownAccount)
                    {
                        warnedUnknownAccount = true;
                        AdvancedLogger.Log("[FAIL] Playtime sync skipped: could not tell which Steam account is yours (several accounts on this PC and none is the active one). Start Steam and log into your account.");
                    }
                    return PlaytimeSyncResult.NotSupported;
                }

                var mine = ReadAppTime(userdata, me, game.GameId);

                if (RepairForeignPlaytime(game, userdata, me, mine)) return PlaytimeSyncResult.Synced;

                ulong minutes = mine?.Minutes ?? 0;
                if (minutes == 0) return PlaytimeSyncResult.NoSteamTime;

                ulong steamSeconds = minutes * 60UL;
                if (steamSeconds <= game.Playtime) return PlaytimeSyncResult.SkippedNotHigher;

                _api.MainView.UIDispatcher.Invoke(() =>
                {
                    ulong old = game.Playtime;
                    game.Playtime = steamSeconds;
                    if (mine.LastPlayed.HasValue && (!game.LastActivity.HasValue || game.LastActivity.Value < mine.LastPlayed.Value))
                        game.LastActivity = mine.LastPlayed.Value;

                    _api.Database.Games.Update(game);

                    AdvancedLogger.Log($"[HORAS] {game.Name}: {old / 60} min -> {minutes} min (Steam era maior).");
                });
                return PlaytimeSyncResult.Synced;
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log($"[HORAS] ERRO em {game?.Name}: {ex.Message}");
                return PlaytimeSyncResult.Error;
            }
        }

        private bool RepairForeignPlaytime(Game game, string userdata, string me, AppTime mine)
        {
            if (game.Playtime == 0) return false;

            ulong mineSeconds = (mine?.Minutes ?? 0) * 60UL;
            if (game.Playtime == mineSeconds) return false;

            foreach (var dir in Directory.GetDirectories(userdata))
            {
                string id = Path.GetFileName(dir);
                if (id == me) continue;

                var other = ReadAppTime(userdata, id, game.GameId);
                if (other == null || other.Minutes == 0 || other.Minutes * 60UL != game.Playtime) continue;

                _api.MainView.UIDispatcher.Invoke(() =>
                {
                    ulong old = game.Playtime;
                    game.Playtime = mineSeconds;
                    game.LastActivity = mine?.LastPlayed;
                    _api.Database.Games.Update(game);

                    AdvancedLogger.Log($"[HORAS] {game.Name}: {old / 60} min came from another Steam account on this PC; reset to {mineSeconds / 60} min (your account).");
                });
                return true;
            }

            return false;
        }

        private AppTime ReadAppTime(string userdata, string accountId, string appId)
        {
            var apps = GetApps(Path.Combine(userdata, accountId, "config", "localconfig.vdf"));
            var app = apps == null ? null : VdfText.Child(apps, appId);
            if (app == null) return null;

            var time = new AppTime();

            if (VdfText.Value(app, "Playtime") is string pt && ulong.TryParse(pt, out ulong minutes))
                time.Minutes = minutes;

            if (VdfText.Value(app, "LastPlayed") is string lp && long.TryParse(lp, out long unix) && unix > 946684800)
                time.LastPlayed = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

            return time;
        }

        private Dictionary<string, object> GetApps(string cfg)
        {
            if (!File.Exists(cfg)) return null;

            DateTime write = File.GetLastWriteTimeUtc(cfg);
            lock (cacheLock)
            {
                if (appsCache.TryGetValue(cfg, out CachedApps cached) && cached.WriteTime == write) return cached.Apps;
            }

            try
            {
                string text;
                using (var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();

                var apps = VdfText.Find(VdfText.Parse(text), "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");

                lock (cacheLock)
                {
                    appsCache[cfg] = new CachedApps { WriteTime = write, Apps = apps };
                }
                return apps;
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log($"[HORAS] Failed to read a Steam localconfig.vdf: {ex.Message}");
                return null;
            }
        }
    }

    internal static class VdfText
    {
        public static Dictionary<string, object> Parse(string text)
        {
            int pos = 0;
            return ParseBlock(text, ref pos, true);
        }

        private static Dictionary<string, object> ParseBlock(string s, ref int i, bool top)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string key = NextToken(s, ref i, out bool isBrace);
                if (key == null) return dict;
                if (isBrace) { if (key == "}") return dict; continue; }

                string val = NextToken(s, ref i, out isBrace);
                if (val == null) return dict;
                if (isBrace)
                {
                    if (val == "{") dict[key] = ParseBlock(s, ref i, false);
                    else return dict;
                }
                else dict[key] = val;
            }
        }

        private static string NextToken(string s, ref int i, out bool isBrace)
        {
            isBrace = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                break;
            }
            if (i >= s.Length) return null;

            if (s[i] == '{' || s[i] == '}') { isBrace = true; return s[i++].ToString(); }

            if (s[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    sb.Append(s[i++]);
                }
                i++;
                return sb.ToString();
            }

            int st = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}') i++;
            return s.Substring(st, i - st);
        }

        public static Dictionary<string, object> Find(Dictionary<string, object> node, params string[] path)
        {
            var cur = node;
            foreach (var p in path)
            {
                cur = Child(cur, p);
                if (cur == null) return null;
            }
            return cur;
        }

        public static Dictionary<string, object> Child(Dictionary<string, object> node, string key)
            => node != null && node.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static object Value(Dictionary<string, object> node, string key)
            => node != null && node.TryGetValue(key, out var v) ? v : null;

        public static bool SetAppPlaytime(string text, string appId, ulong minutes, out string updated)
        {
            updated = null;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(appId)) return false;

            int curStart = 0;
            int curEnd = text.Length;
            foreach (string segment in new[] { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps" })
            {
                if (!FindBlockRange(text, curStart, curEnd, segment, out int openB, out int closeB))
                    return false;
                curStart = openB + 1;
                curEnd = closeB;
            }

            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string minutesStr = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (FindBlockRange(text, curStart, curEnd, appId, out int appOpenB, out int appCloseB))
            {
                if (FindScalarValueSpan(text, appOpenB + 1, appCloseB, "Playtime", out int valStart, out int valEnd))
                {
                    updated = text.Substring(0, valStart) + "\"" + minutesStr + "\"" + text.Substring(valEnd);
                    return true;
                }

                string entry = $"{newline}\t\t\t\t\t\t\"Playtime\"\t\t\"{minutesStr}\"";
                updated = text.Substring(0, appOpenB + 1) + entry + text.Substring(appOpenB + 1);
                return true;
            }

            string block = $"{newline}\t\t\t\t\t\"{appId}\"{newline}\t\t\t\t\t{{{newline}\t\t\t\t\t\t\"Playtime\"\t\t\"{minutesStr}\"{newline}\t\t\t\t\t}}";
            updated = text.Substring(0, curStart) + block + text.Substring(curStart);
            return true;
        }

        private static string NextTokenWithSpan(string s, ref int i, int limit, out bool isBrace, out int tokStart, out int tokEnd)
        {
            isBrace = false;
            tokStart = i;
            tokEnd = i;

            while (i < limit)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < limit && s[i + 1] == '/') { while (i < limit && s[i] != '\n') i++; continue; }
                break;
            }
            if (i >= limit) return null;

            tokStart = i;
            if (s[i] == '{' || s[i] == '}')
            {
                isBrace = true;
                tokEnd = i + 1;
                return s[i++].ToString();
            }

            if (s[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < limit && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < limit) i++;
                    sb.Append(s[i++]);
                }
                if (i < limit && s[i] == '"') i++;
                tokEnd = i;
                return sb.ToString();
            }

            int st = i;
            while (i < limit && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}') i++;
            tokEnd = i;
            return s.Substring(st, i - st);
        }

        private static int FindMatchingCloseBrace(string s, int openBracePos, int limit)
        {
            int i = openBracePos + 1;
            int depth = 1;
            while (i < limit && depth > 0)
            {
                string tok = NextTokenWithSpan(s, ref i, limit, out bool isBrace, out int tokStart, out _);
                if (tok == null) return -1;
                if (isBrace)
                {
                    if (tok == "{") depth++;
                    else if (tok == "}")
                    {
                        depth--;
                        if (depth == 0) return tokStart;
                    }
                }
            }
            return -1;
        }

        private static bool FindBlockRange(string s, int start, int end, string targetKey, out int openBracePos, out int closeBracePos)
        {
            openBracePos = -1;
            closeBracePos = -1;
            int i = start;

            while (i < end)
            {
                string key = NextTokenWithSpan(s, ref i, end, out bool isBrace, out _, out _);
                if (key == null) break;
                if (isBrace)
                {
                    if (key == "}") break;
                    continue;
                }

                string val = NextTokenWithSpan(s, ref i, end, out isBrace, out int valStart, out _);
                if (val == null) break;

                if (isBrace && val == "{")
                {
                    int closeB = FindMatchingCloseBrace(s, valStart, end);
                    if (closeB < 0) break;
                    if (string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
                    {
                        openBracePos = valStart;
                        closeBracePos = closeB;
                        return true;
                    }
                    i = closeB + 1;
                }
            }

            return false;
        }

        private static bool FindScalarValueSpan(string s, int start, int end, string targetKey, out int valStart, out int valEnd)
        {
            valStart = -1;
            valEnd = -1;
            int i = start;

            while (i < end)
            {
                string key = NextTokenWithSpan(s, ref i, end, out bool isBrace, out _, out _);
                if (key == null) break;
                if (isBrace)
                {
                    if (key == "}") break;
                    continue;
                }

                string val = NextTokenWithSpan(s, ref i, end, out isBrace, out int vStart, out int vEnd);
                if (val == null) break;

                if (isBrace && val == "{")
                {
                    int closeB = FindMatchingCloseBrace(s, vStart, end);
                    if (closeB < 0) break;
                    i = closeB + 1;
                }
                else if (string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
                {
                    valStart = vStart;
                    valEnd = vEnd;
                    return true;
                }
            }

            return false;
        }
    }
}
