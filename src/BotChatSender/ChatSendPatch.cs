using HarmonyLib;

namespace LurkBait.BotChatSender
{
    // Every LurkBait chat line funnels through TwitchConnectorEventSub.SendTwitchMessage. When a bot is
    // logged in and routing is on, the message goes out through the bot via Helix and this skips the
    // game's own IRC send (which posts as the main account). Otherwise the original send runs unchanged.
    [HarmonyPatch(typeof(TwitchConnectorEventSub), "SendTwitchMessage")]
    internal static class ChatSendPatch
    {
        private static bool Prefix(string message) =>
            Plugin.Instance == null || !Plugin.Instance.TryRouteViaBot(message);
    }
}
