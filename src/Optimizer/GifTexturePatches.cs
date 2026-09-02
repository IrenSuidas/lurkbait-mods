using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LurkBait.Optimizer
{
    // GifLoader.UnloadGif nulls its frame list without destroying the textures, and ReadGif rebuilds
    // the list without freeing the old one, so GIF frame textures linger in native memory until the
    // game's next (expensive) Resources.UnloadUnusedAssets sweep. Destroy them at those points so the
    // memory is freed immediately and doesn't depend on that sweep.
    [HarmonyPatch(typeof(GifLoader))]
    internal static class GifTexturePatches
    {
        private static readonly AccessTools.FieldRef<GifLoader, List<Texture>> FramesRef =
            AccessTools.FieldRefAccess<GifLoader, List<Texture>>("_frames");

        [HarmonyPatch("UnloadGif")]
        [HarmonyPrefix]
        private static void UnloadGifPrefix(GifLoader __instance) => DestroyFrames(__instance);

        [HarmonyPatch("ReadGif")]
        [HarmonyPrefix]
        private static void ReadGifPrefix(GifLoader __instance) => DestroyFrames(__instance);

        [HarmonyPatch("ReadGifAsync")]
        [HarmonyPrefix]
        private static void ReadGifAsyncPrefix(GifLoader __instance) => DestroyFrames(__instance);

        // Skipped mid-load so we never race an in-flight async decode building the new frames.
        private static void DestroyFrames(GifLoader loader)
        {
            if (!Plugin.FreeGifFrames || loader.loading)
                return;
            var frames = FramesRef(loader);
            if (frames == null)
                return;
            foreach (var tex in frames)
                if (tex != null)
                    Object.Destroy(tex);
        }
    }
}
