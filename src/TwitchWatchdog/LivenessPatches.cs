using System;
using System.Threading;
using HarmonyLib;
using ScoredProductions.StreamLinked.EventSub;
using ScoredProductions.StreamLinked.IRC;

namespace LurkBait.TwitchWatchdog
{
    // Last-inbound-message timestamps per connection. A long gap means the socket has silently
    // stalled even while it still reports "open", the failure a state poll can't see. Written from
    // StreamLinked's background receive threads, so use Volatile (no Unity API here).
    internal static class Liveness
    {
        private static long _eventSubTicks;
        private static long _ircTicks;

        public static void MarkEventSub() =>
            Volatile.Write(ref _eventSubTicks, DateTime.UtcNow.Ticks);

        public static void MarkIrc() => Volatile.Write(ref _ircTicks, DateTime.UtcNow.Ticks);

        // Seconds since the last message, or -1 if none has been seen yet.
        public static double EventSubSilenceSeconds => Since(Volatile.Read(ref _eventSubTicks));

        public static double IrcSilenceSeconds => Since(Volatile.Read(ref _ircTicks));

        private static double Since(long ticks) =>
            ticks == 0
                ? -1
                : (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
    }

    // Fires once per received EventSub message (including ~10s keepalives).
    [HarmonyPatch(typeof(TwitchEventSubClient), "ParseSocketMessage")]
    internal static class EventSubLivenessPatch
    {
        private static void Prefix() => Liveness.MarkEventSub();
    }

    // Fires once per received IRC line (chat, PING, membership, ...).
    [HarmonyPatch(typeof(TwitchIRCClientInstance), "ProcessMessage")]
    internal static class IrcLivenessPatch
    {
        private static void Prefix() => Liveness.MarkIrc();
    }
}
