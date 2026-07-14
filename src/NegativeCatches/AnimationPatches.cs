using AssetKits.ParticleImage;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LurkBait.NegativeCatches
{
    // The reveal only counts gold UP. For a negative catch, tick the counter down to the new
    // balance instead; timing, audio and the hide/finish sequence are left untouched.
    [HarmonyPatch(typeof(CatchUIController))]
    internal static class CatchRevealPatches
    {
        internal static bool Negative;
        internal static bool HasPreGold;
        internal static string PreGoldUser;
        internal static int PreGold;

        private static bool _defaultsCaptured;
        private static Color _goldTextColor;
        private static Color _valueTextColor;
        private static ParticleSystem.MinMaxGradient _particleColor;

        private static readonly AccessTools.FieldRef<CatchUIController, int> AmountGoldRef =
            AccessTools.FieldRefAccess<CatchUIController, int>("amountGold");
        private static readonly AccessTools.FieldRef<CatchUIController, int> TargetGoldRef =
            AccessTools.FieldRefAccess<CatchUIController, int>("targetGold");
        private static readonly AccessTools.FieldRef<CatchUIController, float> TimeLastPlayedRef =
            AccessTools.FieldRefAccess<CatchUIController, float>("timeLastPlayed");
        private static readonly AccessTools.FieldRef<CatchUIController, float> MinAudioTimeRef =
            AccessTools.FieldRefAccess<CatchUIController, float>("minAudioTime");
        private static readonly AccessTools.FieldRef<CatchUIController, bool> ChangingGoldRef =
            AccessTools.FieldRefAccess<CatchUIController, bool>("changingGold");
        private static readonly AccessTools.FieldRef<
            CatchUIController,
            TextMeshProUGUI
        > GoldTextRef = AccessTools.FieldRefAccess<CatchUIController, TextMeshProUGUI>("goldText");
        private static readonly AccessTools.FieldRef<
            CatchUIController,
            TextMeshProUGUI
        > ValueTextRef = AccessTools.FieldRefAccess<CatchUIController, TextMeshProUGUI>(
            "valueText"
        );
        private static readonly AccessTools.FieldRef<
            CatchUIController,
            AudioSource
        > GoldCollectAudioRef = AccessTools.FieldRefAccess<CatchUIController, AudioSource>(
            "goldCollectAudio"
        );
        private static readonly AccessTools.FieldRef<
            CatchUIController,
            AudioSource
        > GoldEmitAudioRef = AccessTools.FieldRefAccess<CatchUIController, AudioSource>(
            "goldEmitAudio"
        );
        private static readonly AccessTools.FieldRef<
            CatchUIController,
            ParticleImage
        > GoldParticlesRef = AccessTools.FieldRefAccess<CatchUIController, ParticleImage>(
            "goldParticles2"
        );
        private static readonly AccessTools.FieldRef<CatchUIController, Image> BackdropRef =
            AccessTools.FieldRefAccess<CatchUIController, Image>("catchBackdropImage");

        // Blend a rarity color toward the loss red so negative catches still read by tier but with a
        // cursed cast, rather than all collapsing to one flat red. Shared with the editor badge.
        internal static Color Cursed(Color rarityColor) =>
            Color.Lerp(rarityColor, Plugin.LossBackdropColor, Plugin.CursedBlend);

        [HarmonyPatch("Preload")]
        [HarmonyPostfix]
        private static void PreloadPostfix(
            CatchUIController __instance,
            SnaggedCatch snagged,
            string user
        )
        {
            Negative = snagged != null && snagged.Value < 0;

            var goldText = GoldTextRef(__instance);
            var valueText = ValueTextRef(__instance);
            var particles = GoldParticlesRef(__instance);

            if (!_defaultsCaptured)
            {
                if (goldText != null)
                    _goldTextColor = goldText.color;
                if (valueText != null)
                    _valueTextColor = valueText.color;
                if (particles != null)
                    _particleColor = particles.startColor;
                _defaultsCaptured = true;
            }

            // Darken the sustained draining sound for a loss; restore default pitch on a win so it
            // never bleeds over. It runs in ShowGold, right after this.
            var goldEmitAudio = GoldEmitAudioRef(__instance);
            if (goldEmitAudio != null)
                goldEmitAudio.pitch = Negative ? Plugin.LossEmitPitch : 1f;

            if (goldText != null)
                goldText.color = Negative ? Plugin.LossTextColor : _goldTextColor;
            if (valueText != null)
                valueText.color = Negative ? Plugin.LossTextColor : _valueTextColor;
            if (particles != null)
                particles.startColor = Negative
                    ? new ParticleSystem.MinMaxGradient(Plugin.LossParticleColor)
                    : _particleColor;

            if (!Negative)
                return;

            var backdrop = BackdropRef(__instance);
            if (backdrop != null)
                backdrop.color = Cursed(backdrop.color);

            // Count down from the real pre-catch balance to the post-catch one. Reconstructing the
            // start as (post - Value) over-counts once gold was floored to 0 - a broke player would
            // see it start at the full penalty - so use the captured pre-balance.
            int post = TargetGoldRef(__instance);
            int start = (HasPreGold && PreGoldUser == user) ? PreGold : post - snagged.Value;
            if (start < post)
                start = post;
            int steps = start - post;

            __instance.gold = start;
            if (goldText != null)
                goldText.SetText(start.ValueToString() + "g");
            AmountGoldRef(__instance) = steps;

            if (valueText != null)
                valueText.SetText(snagged.Value.ValueToString());
        }

        [HarmonyPatch("IncGold")]
        [HarmonyPrefix]
        private static bool IncGoldPrefix(CatchUIController __instance)
        {
            if (!Negative)
                return true;

            __instance.gold--;
            if (
                Time.time - TimeLastPlayedRef(__instance) > MinAudioTimeRef(__instance)
                && ChangingGoldRef(__instance)
            )
            {
                var goldText = GoldTextRef(__instance);
                if (goldText != null)
                    goldText.SetText(__instance.gold.ValueToString() + "g");
                var goldCollectAudio = GoldCollectAudioRef(__instance);
                if (goldCollectAudio != null)
                {
                    // Per-coin ping stays at stock pitch (the drain sound is the darkened one).
                    goldCollectAudio.pitch = Random.Range(0.9f, 1.1f);
                    goldCollectAudio.Play();
                }
                TimeLastPlayedRef(__instance) = Time.time;
            }
            return false;
        }

        [HarmonyPatch("HideCatch")]
        [HarmonyPostfix]
        private static void HideCatchPostfix()
        {
            Negative = false;
            HasPreGold = false;
        }
    }

    // NumberCounter's count-up loop leaves negatives showing 0; render the signed value directly.
    // Guarded by Value >= 0 so positive counters are untouched.
    [HarmonyPatch(typeof(NumberCounter), nameof(NumberCounter.UpdateText))]
    internal static class NumberCounterPatch
    {
        private static bool Prefix(NumberCounter __instance)
        {
            if (__instance.Value >= 0)
                return true;
            if (__instance.Text != null)
                __instance.Text.SetText(__instance.Value.ValueToString());
            return false;
        }
    }
}
