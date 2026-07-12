using BepInEx;
using HarmonyLib;

namespace LurkBait.NoChatbotOutage
{
    [BepInPlugin(Guid, "LurkBait No Chatbot Outage", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.irensuidas.lurkbait.nochatbotoutage";

        private void Awake() => new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
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
