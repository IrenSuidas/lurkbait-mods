using System;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ScoredProductions.StreamLinked.API;
using ScoredProductions.StreamLinked.EventSub;
using ScoredProductions.StreamLinked.IRC;
using ScoredProductions.StreamLinked.Utility;
using UnityEngine;

namespace LurkBait.TwitchWatchdog
{
    // The bundled StreamLinked asset never reconnects after a mid-session drop or silent stall, so
    // long streams quietly stop responding to chat, points and subs. This watches both connections'
    // public state plus a liveness clock and, when one looks dead, calls the reconnect entry points
    // the game leaves unused. Defaults to observe-only so a session can be captured first.
    [BepInPlugin(PluginGuid, "LurkBait Twitch Watchdog", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.irensuidas.lurkbait.twitchwatchdog";

        internal static ManualLogSource Log;

        private ConfigEntry<bool> _autoReconnect;
        private ConfigEntry<bool> _superviseEventSub;
        private ConfigEntry<bool> _superviseIrc;
        private ConfigEntry<float> _checkInterval;
        private ConfigEntry<float> _eventSubSilence;
        private ConfigEntry<float> _ircSilence;
        private ConfigEntry<float> _baseBackoff;
        private ConfigEntry<float> _maxBackoff;

        private float _sinceCheck;
        private readonly ConnectionWatch _es = new ConnectionWatch("EventSub");
        private readonly ConnectionWatch _irc = new ConnectionWatch("IRC");

        private void Awake()
        {
            Log = Logger;

            _autoReconnect = Config.Bind(
                "General",
                "AutoReconnect",
                false,
                "Off (default): observe only - log when a connection looks dead but don't touch it. "
                    + "On: actually reconnect. Run a session observe-only first to confirm, then enable."
            );
            _superviseEventSub = Config.Bind(
                "General",
                "SuperviseEventSub",
                true,
                "Watch the EventSub connection (channel points, bits, subs, gift subs)."
            );
            _superviseIrc = Config.Bind(
                "General",
                "SuperviseIRC",
                true,
                "Watch the IRC/chat connection (chat commands like !fish)."
            );
            _checkInterval = Config.Bind(
                "Detection",
                "CheckIntervalSeconds",
                5f,
                new ConfigDescription(
                    "How often to evaluate connection health.",
                    new AcceptableValueRange<float>(1f, 60f)
                )
            );
            _eventSubSilence = Config.Bind(
                "Detection",
                "EventSubSilenceSeconds",
                40f,
                new ConfigDescription(
                    "Treat EventSub as stalled after this many seconds with no message. Twitch sends "
                        + "a keepalive about every 10s, so 40 is a safe 'clearly dead' margin.",
                    new AcceptableValueRange<float>(15f, 600f)
                )
            );
            _ircSilence = Config.Bind(
                "Detection",
                "IrcSilenceSeconds",
                360f,
                new ConfigDescription(
                    "Treat IRC as stalled after this many seconds with no inbound line. Twitch PINGs "
                        + "roughly every 5 minutes even on a silent channel, so 360 avoids false alarms.",
                    new AcceptableValueRange<float>(120f, 1200f)
                )
            );
            _baseBackoff = Config.Bind(
                "Reconnect",
                "BaseBackoffSeconds",
                5f,
                new ConfigDescription(
                    "First reconnect wait; doubles each consecutive failure up to MaxBackoffSeconds.",
                    new AcceptableValueRange<float>(1f, 60f)
                )
            );
            _maxBackoff = Config.Bind(
                "Reconnect",
                "MaxBackoffSeconds",
                60f,
                new ConfigDescription(
                    "Cap on the backoff wait, so we never hammer Twitch into a rate limit.",
                    new AcceptableValueRange<float>(10f, 600f)
                )
            );

            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo(
                $"Loaded - {(_autoReconnect.Value ? "AUTO-RECONNECT active" : "observe-only")}; "
                    + $"watching {(_superviseEventSub.Value ? "EventSub " : "")}{(_superviseIrc.Value ? "IRC" : "")}".Trim()
            );
        }

        private void Update()
        {
            _sinceCheck += Time.unscaledDeltaTime;
            if (_sinceCheck < _checkInterval.Value)
                return;
            _sinceCheck = 0f;

            // Nothing to supervise until the game is actually authenticated with Twitch.
            if (!SafeAuthAvailable())
            {
                _es.Reset();
                _irc.Reset();
                return;
            }

            if (_superviseEventSub.Value)
                EvaluateEventSub();
            if (_superviseIrc.Value)
                EvaluateIrc();
        }

        private void EvaluateEventSub()
        {
            if (!SingletonInstance<TwitchEventSubClient>.GetInstance(out var es) || es == null)
                return;
            if (!es.EventSubEnabled)
            {
                _es.Reset(); // the game hasn't turned EventSub on (or turned it off), not our call
                return;
            }
            if (TwitchEventSubClient.EventSubStartingUp)
                return; // already (re)connecting, give it time

            double silence = Liveness.EventSubSilenceSeconds;
            bool active = TwitchEventSubClient.EventSubConnectionActive;
            bool stalled = silence >= 0 && silence > _eventSubSilence.Value;

            string reason =
                !active ? "socket not open / no session"
                : stalled ? $"no message for {silence:F0}s (keepalive expected ~10s)"
                : null;

            if (_es.Evaluate(reason, _autoReconnect.Value, _baseBackoff.Value, _maxBackoff.Value))
            {
                // Fire-and-forget, but exceptions are observed/logged inside, so the task never
                // faults unobserved. Only the long-lived singleton is captured.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await es.BeginConnectionSession(restart: true, resubscribe: true);
                    }
                    catch (Exception e)
                    {
                        Log.LogError("EventSub reconnect threw: " + e);
                    }
                });
            }
        }

        private void EvaluateIrc()
        {
            var irc = TwitchConnectorEventSub.Instance?.ircClientMain;
            if (irc == null)
                return;
            if (!irc.IRCEnabled)
            {
                _irc.Reset();
                return;
            }

            double silence = Liveness.IrcSilenceSeconds;
            bool connected = irc.IsConnected;
            bool stalled = silence >= 0 && silence > _ircSilence.Value;

            string reason =
                !connected ? "socket reports disconnected"
                : stalled ? $"no inbound line for {silence:F0}s (PING expected ~5min)"
                : null;

            if (_irc.Evaluate(reason, _autoReconnect.Value, _baseBackoff.Value, _maxBackoff.Value))
                irc.ReconnectToTwitch(); // just enqueues a main-thread reconnect
        }

        private static bool SafeAuthAvailable()
        {
            try
            {
                return TwitchAPIClient.APIOAuthAvailable;
            }
            catch
            {
                return false;
            }
        }
    }
}
