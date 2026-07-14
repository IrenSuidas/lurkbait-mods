using HarmonyLib;

namespace LurkBait.NegativeCatches
{
    // A negative catch subtracts via the game's "gold += value", which has no floor, so a big
    // penalty could push a player negative. Clamp to zero right after PushCatch records it (before
    // the reveal reads the balance). Disable via config to allow negative balances.
    [HarmonyPatch(typeof(PlayersManager), nameof(PlayersManager.PushCatch))]
    internal static class PushCatchFloorPatch
    {
        private static void Prefix(string username)
        {
            CatchRevealPatches.HasPreGold = false;
            var pm = PlayersManager.Instance;
            if (
                pm != null
                && pm.Players != null
                && pm.Players.TryGetValue(username, out var data)
                && data != null
            )
            {
                CatchRevealPatches.PreGold = data.GetGold();
                CatchRevealPatches.PreGoldUser = username;
                CatchRevealPatches.HasPreGold = true;
            }
        }

        private static void Postfix(string username, SnaggedCatch snaggedCatch)
        {
            if (!Plugin.PreventNegativeBalance || snaggedCatch == null || snaggedCatch.Value >= 0)
                return;
            var pm = PlayersManager.Instance;
            if (pm == null || pm.Players == null || !pm.Players.TryGetValue(username, out var data))
                return;
            if (data.gold >= 0)
                return;
            data.gold = 0;
            if (data.goldSnapshot > data.gold)
                data.goldSnapshot = data.gold;
            pm.SaveData();
        }
    }
}
