using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LurkBait.NegativeCatches
{
    // Lets a custom catch have a negative value, so it takes gold instead of giving it. The game
    // already applies value as "gold += value", so this just unblocks negative input, gives those
    // catches a rarity, flips the reveal to count down, and rewords the chat line. Removing the DLL
    // removes every patch; negative catches already saved still work on the un-modded game.
    [BepInPlugin(PluginGuid, "LurkBait Negative Catches", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.irensuidas.lurkbait.negativecatches";

        internal static ManualLogSource Log;
        internal static int MaxPenalty;
        internal static bool PreventNegativeBalance;
        internal static float LossEmitPitch;
        internal static Color LossTextColor;
        internal static Color LossBackdropColor;
        internal static Color LossParticleColor;
        internal static float CursedBlend;

        private void Awake()
        {
            Log = Logger;

            MaxPenalty = Config
                .Bind(
                    "General",
                    "MaxPenalty",
                    1000,
                    "Largest amount of gold a single negative custom catch may take away. Mirrors the "
                        + "game's 1000 gold cap on positive catches. In the custom catch editor you can "
                        + "then enter values down to -MaxPenalty."
                )
                .Value;
            if (MaxPenalty < 0)
                MaxPenalty = -MaxPenalty;

            PreventNegativeBalance = Config
                .Bind(
                    "General",
                    "PreventNegativeBalance",
                    true,
                    "Clamp a player's gold to zero when a negative catch would take them below it, the "
                        + "way the rest of the game treats gold. Turn off to allow negative balances."
                )
                .Value;

            LossEmitPitch = Config
                .Bind(
                    "Animation",
                    "LossEmitPitch",
                    0.6f,
                    new ConfigDescription(
                        "Pitch of the sustained draining sound that plays while a negative catch takes "
                            + "gold away (the one that runs until the counter reaches the new amount, "
                            + "not the per-coin ping). Normally 1.0; a lower value sounds darker.",
                        new AcceptableValueRange<float>(0.1f, 3f)
                    )
                )
                .Value;

            LossTextColor = BindColor(
                "LossTextColor",
                "#FF4738",
                "Color of the gold counter and the '-N' value readout while a negative catch drains gold."
            );
            LossBackdropColor = BindColor(
                "LossBackdropColor",
                "#801212",
                "The 'cursed' red that a negative catch's rarity color is blended toward for the card "
                    + "backdrop and the editor rarity badge (see CursedBlend)."
            );
            LossParticleColor = BindColor(
                "LossParticleColor",
                "#FF3326",
                "Color of the gold particles while a negative catch drains gold."
            );
            CursedBlend = Config
                .Bind(
                    "Visuals",
                    "CursedBlend",
                    0.5f,
                    new ConfigDescription(
                        "How far a negative catch's rarity color is pushed toward LossBackdropColor: 0 keeps "
                            + "the true rarity color, 1 is full cursed red. Lower values keep the tiers more "
                            + "distinct from each other.",
                        new AcceptableValueRange<float>(0f, 1f)
                    )
                )
                .Value;

            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Loaded - negative catches down to -{MaxPenalty}g enabled.");
        }

        // Bound as hex strings so they're easy to hand-edit in the config file.
        private Color BindColor(string key, string defaultHex, string description)
        {
            string hex = Config.Bind("Visuals", key, defaultHex, description).Value;
            if (ColorUtility.TryParseHtmlString(hex, out var color))
                return color;
            Log.LogWarning(
                $"Config '{key}' value '{hex}' is not a valid hex color (e.g. #FF4738); using {defaultHex}."
            );
            if (!ColorUtility.TryParseHtmlString(defaultHex, out color))
                color = Color.magenta; // defaults are known-valid; magenta just flags a code bug
            return color;
        }
    }
}
