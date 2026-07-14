using BepInEx;
using HarmonyLib;

namespace LurkBait.NoChatbotOutage
{
    [BepInPlugin(PluginGuid, "LurkBait No Chatbot Outage", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.irensuidas.lurkbait.nochatbotoutage";

        private void Awake()
        {
            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Loaded - hiding the stale chatbot outage popup.");
        }
    }

    // Every announcement funnels through PushAnnouncement, skip only the stale outage box.
    [HarmonyPatch(
        typeof(AnnoucementController),
        nameof(AnnoucementController.PushAnnouncement),
        new[] { typeof(Announcement), typeof(bool), typeof(bool) }
    )]
    internal static class PushAnnouncementPatch
    {
        private static bool Prefix(Announcement announcement) =>
            announcement == null || announcement.title != "Temporary Chatbot Outage";
    }
}
