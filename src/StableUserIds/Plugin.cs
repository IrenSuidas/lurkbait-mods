using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace LurkBait.StableUserIds
{
    // Preserves gold across Twitch username changes. Captures the stable user-id
    // at the fishing path (PushPlayer), so "!fish @target" resolves the target rather than
    // the caster and non-fishers are never recorded, then migrates a viewer's record when
    // their id reappears under a new name. Cannot recover viewers who renamed before the
    // mod first saw them fish (Twitch can't map a released login back to its id).
    [BepInPlugin(PluginGuid, "LurkBait Stable User IDs", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.irensuidas.lurkbait.stableuserids";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        private bool _backfillStarted;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            var backfill = Config.Bind(
                "General",
                "BackfillOnStartup",
                true,
                "On launch, resolve your existing roster's usernames to ids so future renames are "
                    + "handled for players already in your save. Does not recover viewers who renamed earlier."
            );

            IdMap.Load();
            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Loaded - {IdMap.Count} known id(s) from {IdMap.FilePath}");

            if (!backfill.Value)
                return;
            if (PlayersManager.Initialized)
                StartBackfill();
            else
                PlayersManager.playerDataLoaded.AddListener(StartBackfill);
        }

        internal void StartBackfill()
        {
            if (_backfillStarted)
                return;
            _backfillStarted = true;
            StartCoroutine(Resolver.BackfillRoster());
        }
    }

    // Every cast trigger (chat, !fish @target, points, bits, subs, gifts, API) reaches
    // PushPlayer with the actual fisher's username.
    [HarmonyPatch(
        typeof(PlayersManager),
        nameof(PlayersManager.PushPlayer),
        new[] { typeof(string), typeof(bool) }
    )]
    internal static class PushPlayerPatch
    {
        private static void Postfix(string username)
        {
            try
            {
                if (!string.IsNullOrEmpty(username))
                    Resolver.EnsureResolved(username.ToLowerInvariant());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("PushPlayer hook: " + e);
            }
        }
    }

    internal static class Resolver
    {
        private const string HelixUsers = "https://api.twitch.tv/helix/users";
        private static readonly HashSet<string> InFlight = new(StringComparer.Ordinal);

        public static void EnsureResolved(string username)
        {
            if (IdMap.KnowsName(username) || !InFlight.Add(username))
                return;
            Plugin.Instance.StartCoroutine(ResolveLogins([username]));
        }

        // Resolve the existing roster in batches of 100 (helix's max) once Twitch is connected.
        public static IEnumerator BackfillRoster()
        {
            for (float waited = 0f; !AuthReady(); waited += 3f)
            {
                if (waited >= 300f)
                {
                    Plugin.Log?.LogWarning("Back-fill skipped: Twitch not connected.");
                    yield break;
                }
                yield return new WaitForSeconds(3f);
            }

            var players = PlayersManager.Instance?.Players;
            if (players == null)
                yield break;

            var todo = players.Keys.Where(name => !IdMap.KnowsName(name)).ToList();

            for (int i = 0; i < todo.Count; i += 100)
                yield return ResolveLogins(todo.GetRange(i, Math.Min(100, todo.Count - i)));

            var knownIds = IdMap.AllIds();
            for (int i = 0; i < knownIds.Count; i += 100)
                yield return ResolveByIds(knownIds.GetRange(i, Math.Min(100, knownIds.Count - i)));

            Plugin.Log?.LogInfo($"Back-fill done; {IdMap.Count} id(s) known.");
        }

        private static bool AuthReady()
        {
            var t = TwitchConnectorEventSub.Instance;
            return t?.UserAccessToken != null
                && !string.IsNullOrEmpty(t.UserAccessToken.Access_Token)
                && !string.IsNullOrEmpty(t.TwitchClientID);
        }

        private static IEnumerator ResolveLogins(List<string> logins)
        {
            if (AuthReady())
            {
                var url = new StringBuilder(HelixUsers);
                for (int i = 0; i < logins.Count; i++)
                    url.Append(i == 0 ? "?login=" : "&login=").Append(logins[i]);
                yield return Query(url.ToString());
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"Not resolving [{string.Join(", ", logins)}] - Twitch not connected yet."
                );
            }

            foreach (var l in logins)
                InFlight.Remove(l);
        }

        private static IEnumerator ResolveByIds(List<string> ids)
        {
            if (!AuthReady())
                yield break;
            var url = new StringBuilder(HelixUsers);
            for (int i = 0; i < ids.Count; i++)
                url.Append(i == 0 ? "?id=" : "&id=").Append(ids[i]);
            yield return Query(url.ToString());
        }

        private static IEnumerator Query(string url)
        {
            var t = TwitchConnectorEventSub.Instance;
            using (var www = UnityWebRequest.Get(url))
            {
                www.SetRequestHeader("Authorization", "Bearer " + t.UserAccessToken.Access_Token);
                www.SetRequestHeader("Client-Id", t.TwitchClientID);
                yield return www.SendWebRequest();
                HandleResponse(www);
            }
        }

        private static readonly Regex UserPattern = new Regex(
            "\"id\":\"(\\d+)\",\"login\":\"([^\"]*)\",\"display_name\":\"([^\"]*)\""
        );

        private static void HandleResponse(UnityWebRequest www)
        {
            try
            {
                if (www.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log?.LogWarning("helix/users failed: " + www.error);
                    return;
                }

                var matches = UserPattern.Matches(www.downloadHandler?.text ?? "");
                if (matches.Count == 0)
                {
                    Plugin.Log?.LogInfo(
                        "helix returned no matching users (login renamed/deleted?)."
                    );
                    return;
                }
                foreach (Match m in matches)
                    Reconciler.Reconcile(
                        m.Groups[1].Value,
                        m.Groups[2].Value.ToLowerInvariant(),
                        m.Groups[3].Value
                    );
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("Resolve: " + e);
            }
        }
    }

    internal static class Reconciler
    {
        public static void Reconcile(string userId, string username, string displayName)
        {
            if (IdMap.TryGetName(userId, out var previous))
            {
                if (previous == username)
                    return;
                SaveGuard.EnsurePreModBackup();
                if (Migrate(previous, username, displayName))
                {
                    Plugin.Log?.LogInfo(
                        $"Rename (id {userId}): '{previous}' -> '{username}' migrated."
                    );
                    string newName = string.IsNullOrEmpty(displayName) ? username : displayName;
                    NotificationController.Instance?.QueueNotif(
                        $"@{previous} is now known as @{newName}!"
                    );
                }
                else
                {
                    Plugin.Log?.LogInfo(
                        $"id {userId} renamed '{previous}' -> '{username}' (no old record to move)."
                    );
                }
            }
            else
            {
                Plugin.Log?.LogInfo($"Linked id {userId} -> '{username}'.");
            }

            IdMap.SetName(userId, username);
            IdMap.Save();
        }

        private static bool Migrate(string oldName, string newName, string displayName)
        {
            var pm = PlayersManager.Instance;
            var players = pm?.Players;
            if (
                players == null
                || !players.TryGetValue(oldName, out var oldData)
                || oldData == null
            )
                return false;

            if (players.TryGetValue(newName, out var newData) && newData != null)
            {
                newData.gold += oldData.gold;
                newData.goldSnapshot += oldData.goldSnapshot;
                newData.totalCasts += oldData.totalCasts;
                newData.totalCastsSnapshot += oldData.totalCastsSnapshot;
                newData.lastCast = Later(newData.lastCast, oldData.lastCast);
            }
            else
            {
                players[newName] = oldData;
            }
            players.Remove(oldName);
            if (!string.IsNullOrEmpty(displayName))
                players[newName].displayName = displayName;

            if (pm.Catches != null)
                foreach (var c in pm.Catches)
                    if (c != null && c.username == oldName)
                        c.username = newName;

            pm.SaveData();

            var dm = DexManager.Instance;
            if (dm?.Dex != null)
            {
                bool dexTouched = false;
                foreach (var entry in dm.Dex.Values)
                    if (entry != null && entry.biggestCaughtBy == oldName)
                    {
                        entry.biggestCaughtBy = newName;
                        dexTouched = true;
                    }
                if (dexTouched)
                    dm.SaveData();
            }

            return true;
        }

        private static string Later(string a, string b)
        {
            if (string.IsNullOrEmpty(a))
                return b;
            if (string.IsNullOrEmpty(b))
                return a;
            return DateTime.TryParse(a, out var da) && DateTime.TryParse(b, out var db) && db > da
                ? b
                : a;
        }
    }

    // Persistent user-id <-> username map ("userId<TAB>username" lines) beside the save.
    internal static class IdMap
    {
        private static readonly Dictionary<string, string> IdToName = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> NameToId = new(StringComparer.Ordinal);

        public static string FilePath =>
            Path.Combine(Application.persistentDataPath, "StableUserIds.map");
        public static int Count => IdToName.Count;

        public static bool TryGetName(string userId, out string username) =>
            IdToName.TryGetValue(userId, out username);

        public static bool KnowsName(string username) => NameToId.ContainsKey(username);

        public static List<string> AllIds() => new List<string>(IdToName.Keys);

        public static void SetName(string userId, string username)
        {
            if (NameToId.TryGetValue(username, out var otherId) && otherId != userId)
                IdToName.Remove(otherId);
            if (IdToName.TryGetValue(userId, out var oldName) && oldName != username)
                NameToId.Remove(oldName);
            IdToName[userId] = username;
            NameToId[username] = userId;
        }

        public static void Load()
        {
            IdToName.Clear();
            NameToId.Clear();
            try
            {
                if (!File.Exists(FilePath))
                    return;
                foreach (var line in File.ReadLines(FilePath))
                {
                    var p = line.Split('\t');
                    if (p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
                        SetName(p[0], p[1]);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("Load id map: " + e);
            }
        }

        public static void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, IdToName.Select(kv => kv.Key + "\t" + kv.Value));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("Save id map: " + e);
            }
        }
    }

    // One immutable pre-mod copy of the save, made before the first migration.
    internal static class SaveGuard
    {
        private static bool _done;

        public static void EnsurePreModBackup()
        {
            if (_done)
                return;
            _done = true;
            try
            {
                var dir = Application.persistentDataPath;
                CopyOnce(
                    Path.Combine(dir, "PlayerData.txt"),
                    Path.Combine(dir, "PlayerData.premod-backup.txt")
                );
                CopyOnce(
                    Path.Combine(dir, "CatchData.txt"),
                    Path.Combine(dir, "CatchData.premod-backup.txt")
                );
                CopyOnce(
                    Path.Combine(dir, "DexData.txt"),
                    Path.Combine(dir, "DexData.premod-backup.txt")
                );
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("Pre-mod backup: " + e);
            }
        }

        private static void CopyOnce(string src, string dst)
        {
            if (File.Exists(src) && !File.Exists(dst))
                File.Copy(src, dst);
        }
    }
}
