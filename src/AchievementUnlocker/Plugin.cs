using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Steamworks;
using UnityEngine;

namespace LurkBait.AchievementUnlocker
{
    // Unlocks Steam achievements on a hotkey: F9 grants "Blam!" by default, or every achievement if
    // UnlockAll is set. F10 resets everything when EnableReset is on. These are real account-level
    // achievements on Valve's servers, so nothing fires automatically - it's all behind hotkeys.
    [BepInPlugin(PluginGuid, "LurkBait Achievement Unlocker", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.irensuidas.lurkbait.achievementunlocker";

        private const KeyCode UnlockKey = KeyCode.F9;
        private const KeyCode ResetKey = KeyCode.F10;

        private const string BlamAchievement = "BLAM";

        internal static ManualLogSource Log;

        private ConfigEntry<bool> _unlockAll;
        private ConfigEntry<bool> _enableReset;

        private bool _statsRequested;

        private void Awake()
        {
            Log = Logger;

            _unlockAll = Config.Bind(
                "General",
                "UnlockAll",
                false,
                "Off (default): F9 unlocks only the 'Blam!' achievement (the problematic one for this "
                    + "game). On: F9 unlocks every achievement the game defines."
            );
            _enableReset = Config.Bind(
                "General",
                "EnableReset",
                false,
                "Allow the F10 reset hotkey. WARNING: resetting clears ALL Steam stats and achievements "
                    + "for this game on your account - a real, server-side wipe, not a local save edit."
            );

            string unlocks = _unlockAll.Value ? "ALL achievements" : "the Blam! achievement";
            string reset = _enableReset.Value ? $"{ResetKey} resets all" : "reset disabled";
            Log.LogInfo($"Loaded - {UnlockKey} unlocks {unlocks}; {reset}.");
        }

        private void Update()
        {
            if (!SteamInitialized())
                return;

            if (!_statsRequested)
            {
                _statsRequested = true;
                try
                {
                    SteamUserStats.RequestCurrentStats();
                }
                catch (Exception e)
                {
                    Log.LogWarning("RequestCurrentStats failed: " + e.Message);
                }
            }

            if (Input.GetKeyDown(UnlockKey))
            {
                if (_unlockAll.Value)
                    UnlockAllAchievements();
                else
                    UnlockOne(BlamAchievement);
            }

            if (_enableReset.Value && Input.GetKeyDown(ResetKey))
                ResetAll();
        }

        private static bool SteamInitialized()
        {
            try
            {
                return SteamManager.Initialized;
            }
            catch
            {
                return false;
            }
        }

        private static void UnlockOne(string id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    Log.LogWarning("TargetAchievement is empty; nothing to unlock.");
                    Notify("No target achievement is set.");
                    return;
                }

                if (!SteamUserStats.GetAchievement(id, out bool achieved))
                {
                    Log.LogWarning(
                        $"Achievement '{id}' not found (stats not loaded yet, or the game changed "
                            + "its achievement ids)."
                    );
                    Notify($"Achievement '{id}' not found.");
                    return;
                }

                if (achieved)
                {
                    Log.LogInfo($"Achievement '{id}' ({Display(id)}) is already unlocked.");
                    Notify($"{Display(id)} is already unlocked.");
                    return;
                }

                if (SteamUserStats.SetAchievement(id) && SteamUserStats.StoreStats())
                {
                    Log.LogInfo($"Unlocked achievement '{id}' ({Display(id)}).");
                    Notify($"Unlocked {Display(id)}!");
                }
                else
                {
                    Log.LogWarning($"Failed to store the unlock for '{id}'.");
                }
            }
            catch (Exception e)
            {
                Log.LogError("Unlock failed: " + e);
            }
        }

        private static void UnlockAllAchievements()
        {
            try
            {
                uint count = SteamUserStats.GetNumAchievements();
                int total = 0,
                    newly = 0;
                for (uint i = 0; i < count; i++)
                {
                    string id = SteamUserStats.GetAchievementName(i);
                    if (string.IsNullOrEmpty(id))
                        continue;
                    total++;
                    if (SteamUserStats.GetAchievement(id, out bool achieved) && achieved)
                        continue;
                    if (SteamUserStats.SetAchievement(id))
                        newly++;
                }
                SteamUserStats.StoreStats();
                Log.LogInfo($"Unlocked {newly} new achievement(s) of {total} total.");
                Notify(
                    newly > 0
                        ? $"Unlocked {newly} achievement(s)!"
                        : "All achievements were already unlocked."
                );
            }
            catch (Exception e)
            {
                Log.LogError("Unlock failed: " + e);
            }
        }

        private static void ResetAll()
        {
            try
            {
                SteamUserStats.ResetAllStats(true);
                SteamUserStats.StoreStats();
                Log.LogWarning("Reset ALL Steam stats and achievements for this game.");
                Notify("Reset all achievements and stats.");
            }
            catch (Exception e)
            {
                Log.LogError("Reset failed: " + e);
            }
        }

        private static string Display(string id)
        {
            try
            {
                string name = SteamUserStats.GetAchievementDisplayAttribute(id, "name");
                return string.IsNullOrEmpty(name) ? id : name;
            }
            catch
            {
                return id;
            }
        }

        private static void Notify(string message)
        {
            try
            {
                NotificationController.Instance?.QueueNotif(message);
            }
            catch
            {
                /* toast is best-effort */
            }
        }
    }
}
