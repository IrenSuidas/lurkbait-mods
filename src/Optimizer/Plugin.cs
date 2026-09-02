using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ScoredProductions.StreamLinked.IRC;
using UnityEngine;
using UnityEngine.Profiling;

namespace LurkBait.Optimizer
{
    // One place for LurkBait memory/perf fixes: frees the GIF frame textures the game leaks (see
    // GifTexturePatches), periodically clears the write-only IRC chat backlog that grows all session,
    // and can log memory on an interval for diagnosis. Each part is independently toggleable.
    [BepInPlugin(PluginGuid, "LurkBait Optimizer", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.irensuidas.lurkbait.optimizer";

        internal static ManualLogSource Log;
        internal static bool FreeGifFrames;

        private ConfigEntry<bool> _clearChatBacklog;
        private ConfigEntry<float> _clearInterval;
        private ConfigEntry<bool> _logMemory;
        private ConfigEntry<float> _logInterval;

        private float _sinceClear;
        private float _sinceLog;

        private void Awake()
        {
            Log = Logger;

            FreeGifFrames = Config
                .Bind(
                    "GifTextures",
                    "FreeGifFramesImmediately",
                    true,
                    "Destroy custom-catch GIF frame textures the moment a GIF is unloaded or reloaded, "
                        + "instead of leaving them for the game's periodic UnloadUnusedAssets sweep. "
                        + "Lower peak memory and fewer hitches on GIF-heavy setups."
                )
                .Value;

            _clearChatBacklog = Config.Bind(
                "ChatBacklog",
                "ClearChatBacklog",
                true,
                "Periodically clear the IRC message backlog. The connection keeps every chat line "
                    + "forever in a list nothing ever reads, so it grows for the whole session."
            );
            _clearInterval = Config.Bind(
                "ChatBacklog",
                "ClearIntervalSeconds",
                60f,
                new ConfigDescription(
                    "How often to clear the backlog.",
                    new AcceptableValueRange<float>(10f, 600f)
                )
            );

            _logMemory = Config.Bind(
                "Diagnostics",
                "LogMemory",
                false,
                "Log allocated/reserved/managed memory and the chat backlog size on an interval. Off "
                    + "by default; turn on to diagnose a suspected leak."
            );
            _logInterval = Config.Bind(
                "Diagnostics",
                "LogIntervalSeconds",
                60f,
                new ConfigDescription(
                    "How often to log memory when LogMemory is on.",
                    new AcceptableValueRange<float>(10f, 600f)
                )
            );

            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo(
                $"Loaded - GIF cleanup {OnOff(FreeGifFrames)}, chat-backlog clear "
                    + $"{OnOff(_clearChatBacklog.Value)}, memory log {OnOff(_logMemory.Value)}."
            );
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            if (_clearChatBacklog.Value)
            {
                _sinceClear += dt;
                if (_sinceClear >= _clearInterval.Value)
                {
                    _sinceClear = 0f;
                    ClearChatBacklog();
                }
            }

            if (_logMemory.Value)
            {
                _sinceLog += dt;
                if (_sinceLog >= _logInterval.Value)
                {
                    _sinceLog = 0f;
                    LogMemory();
                }
            }
        }

        // The IRC client stores every received line in a public Stack that nothing reads, so clearing
        // it is safe and caps the one confirmed managed leak.
        private static void ClearChatBacklog()
        {
            var connector = TwitchConnectorEventSub.Instance;
            if (connector == null)
                return;
            Clear(connector.ircClientMain);
            Clear(connector.ircClientSecondary);
        }

        private static void Clear(TwitchIRCClientInstance irc)
        {
            var backlog = irc?.AllMessages;
            if (backlog != null && backlog.Count > 0)
                backlog.Clear();
        }

        private static void LogMemory()
        {
            const long mb = 1024 * 1024;
            int backlog = TwitchConnectorEventSub.Instance?.ircClientMain?.AllMessages?.Count ?? -1;
            Log.LogInfo(
                $"mem: allocated={Profiler.GetTotalAllocatedMemoryLong() / mb}MB "
                    + $"reserved={Profiler.GetTotalReservedMemoryLong() / mb}MB "
                    + $"managed={Profiler.GetMonoUsedSizeLong() / mb}MB chatBacklog={backlog}"
            );
        }

        private static string OnOff(bool on) => on ? "on" : "off";
    }
}
