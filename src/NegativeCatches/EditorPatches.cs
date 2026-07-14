using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace LurkBait.NegativeCatches
{
    [HarmonyPatch]
    internal static class ClampTranspiler
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CustomCatchEditItem), "UpdateValue");
            yield return AccessTools.Method(typeof(CustomCatchEditItem), "SaveCatch");
            yield return AccessTools.Method(typeof(CustomCatchEditItem), "SendTest");
        }

        private static readonly MethodInfo MathfClamp = AccessTools.Method(
            typeof(Mathf),
            nameof(Mathf.Clamp),
            new[] { typeof(int), typeof(int), typeof(int) }
        );
        private static readonly MethodInfo Replacement = AccessTools.Method(
            typeof(ClampTranspiler),
            nameof(ClampValue)
        );

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        )
        {
            foreach (var ins in instructions)
            {
                if (ins.Calls(MathfClamp))
                    yield return new CodeInstruction(OpCodes.Call, Replacement);
                else
                    yield return ins;
            }
        }

        public static int ClampValue(int value, int min, int max) =>
            Mathf.Clamp(value, -Plugin.MaxPenalty, max);
    }

    [HarmonyPatch(typeof(CustomCatchEditItem), "Awake")]
    internal static class ValidateIntBoundPatch
    {
        private static void Postfix(CustomCatchEditItem __instance)
        {
            if (__instance.catchValueInput == null)
                return;
            var validator = __instance.catchValueInput.GetComponent<ValidateInt>();
            if (validator != null)
                validator.minValue = -Plugin.MaxPenalty;
        }
    }

    // Blend the editor's rarity badge toward the loss red for a negative catch, matching the reveal.
    // Runs after the game sets the badge (Populate + each value edit); it re-sets the tier color
    // each time, so the blend applies once, never cumulatively.
    [HarmonyPatch(typeof(CustomCatchEditItem))]
    internal static class EditorRarityColorPatch
    {
        [HarmonyPatch("Populate")]
        [HarmonyPostfix]
        private static void PopulatePostfix(CustomCatchEditItem __instance) => Apply(__instance);

        [HarmonyPatch("UpdateValue")]
        [HarmonyPostfix]
        private static void UpdateValuePostfix(CustomCatchEditItem __instance) => Apply(__instance);

        private static void Apply(CustomCatchEditItem item)
        {
            if (item.rarityBadgeImage == null || item.catchValueInput == null)
                return;
            if (int.TryParse(item.catchValueInput.text, out int value) && value < 0)
                item.rarityBadgeImage.color = CatchRevealPatches.Cursed(
                    item.rarityBadgeImage.color
                );
        }
    }
}
