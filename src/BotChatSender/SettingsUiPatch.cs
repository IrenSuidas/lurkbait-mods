using HarmonyLib;

namespace LurkBait.BotChatSender
{
    // The settings panel adds our bot-login button when it opens (OnEnable), so it sits alongside the
    // game's own Twitch login controls rather than in a floating overlay.
    [HarmonyPatch(typeof(SettingsUIController), "OnEnable")]
    internal static class SettingsUiPatch
    {
        private static void Postfix(SettingsUIController __instance)
        {
            if (Plugin.Instance != null)
                Plugin.Instance.InjectBotButton(__instance);
        }
    }
}
