using HarmonyLib;
using UnityEngine;

namespace LurkBait.NegativeCatches
{
    // Rarity and drop-chance are derived from a catch's value, and the game's formulas assume it's
    // positive (so every negative catch would collapse to junk). Feed them the magnitude, so a
    // -300g catch is rated like a +300g one. Positive values are unaffected.
    [HarmonyPatch(
        typeof(CustomCatchesManager),
        nameof(CustomCatchesManager.CalculateRarityFromValue)
    )]
    internal static class CalculateRarityFromValuePatch
    {
        private static void Prefix(ref int value) => value = Mathf.Abs(value);
    }

    [HarmonyPatch(
        typeof(CustomCatchesManager),
        nameof(CustomCatchesManager.CalculateChanceFromValue)
    )]
    internal static class CalculateChanceFromValuePatch
    {
        private static void Prefix(ref int value) => value = Mathf.Abs(value);
    }
}
