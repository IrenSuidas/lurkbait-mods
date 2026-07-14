using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace LurkBait.NegativeCatches
{
    // The default chat line sounds like a reward; for a loss, swap in loss-flavored wording. A
    // custom chat template is left alone (its {gold} token already resolves to the negative amount).
    [HarmonyPatch(typeof(TwitchConnectorEventSub), nameof(TwitchConnectorEventSub.CatchChat))]
    internal static class CatchChatPatch
    {
        private static bool Prefix(
            TwitchConnectorEventSub __instance,
            string username,
            SnaggedCatch snaggedCatch
        )
        {
            if (snaggedCatch == null || snaggedCatch.Value >= 0)
                return true;
            if (Preferences.Prefs.customChatMessage)
                return true;

            var player = PlayersManager.Instance.Players[username];
            int cost = Mathf.Abs(snaggedCatch.Value);
            string message = string.Format(
                CultureInfo.InvariantCulture,
                "@{0} you caught {1} {2} {3} {4} weighing {5}kg but it cost you {6} gold! You now have {7} gold 🎣",
                player.displayName,
                snaggedCatch.Catch.Rarity.name.Article(),
                snaggedCatch.Catch.Rarity.name.ToLowerInvariant(),
                "⭐".Repeat(snaggedCatch.Rating),
                snaggedCatch.Catch.FullName,
                snaggedCatch.Weight.WeightToString(),
                cost.ValueToString(),
                player.GetGold()
            );
            __instance.SendTwitchMessage(message);
            return false;
        }
    }
}
