using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Msr
{
    /// <summary>Health panel ("wound view") body diagram: follows the skin of whichever body the panel shows.</summary>
    internal static class WoundSkin
    {
        private static readonly Dictionary<Image, Sprite> Originals = new Dictionary<Image, Sprite>();
        private static WoundView lastView;
        private static long applied = -1;
        private static float nextRescan;
        private static bool dirty;

        public static void Reset()
        {
            Originals.Clear();
            lastView = null;
            applied = -1;
        }

        public static void MarkDirty() => dirty = true;

        /// <summary>Re-targets the open panel right now. Called before a skin's sprites are destroyed.</summary>
        public static void Flush()
        {
            if (lastView == null) return;
            dirty = true;
            try { Tick(lastView); } catch (Exception e) { Plugin.Log.LogError(e); }
        }

        public static void Tick(WoundView view)
        {
            if (view == null || view.limbImages == null || view.limbImages.Length == 0) return;
            if (view != lastView)
            {
                Originals.Clear();
                lastView = view;
                applied = -1;
            }

            SkinSet skin = Cfg.WoundView.Value ? SkinManager.Resolve(view.body) : null;
            if (skin != null && skin.Count(Cat.Wound) == 0) skin = null;
            long want = ((long)(skin != null ? skin.Serial : 0) << 8) | (long)Cfg.FlagsStamp();

            // Wound overlays (injection marks, splints...) are added to the diagram while it is open,
            // so an applied skin is swept again every second.
            float now = Time.realtimeSinceStartup;
            bool rescan = skin != null && now >= nextRescan;
            if (want == applied && !dirty && !rescan) return;
            nextRescan = now + 1f;
            dirty = false;
            applied = want;

            foreach (var img in Collect(view))
            {
                if (img == null) continue;
                Sprite current = img.sprite;
                Sprite original;
                bool known = Originals.TryGetValue(img, out original) && original != null;
                if (current == null && !known) continue;
                if (!known || (current != null && !SkinSet.IsOurs(current)))
                {
                    // First sight of this image, or the game put a new vanilla sprite on it.
                    original = SkinSet.Vanilla(current);
                    if (SkinSet.IsOurs(original)) continue;
                    Originals[img] = original;
                }
                Sprite target = original;
                if (skin != null)
                {
                    target = skin.Replace(Cat.Wound, original, Cfg.CrossCharacter.Value ? Alias(original.name) : null) ?? original;
                }
                if (img.sprite != target) img.sprite = target;
            }
        }

        private static IEnumerable<Image> Collect(WoundView view)
        {
            // The limb images share one parent that carries the full-body silhouette.
            var first = view.limbImages[0];
            if (first != null && first.transform.parent != null)
            {
                var root = first.transform.parent.GetComponent<Image>();
                if (root != null) yield return root;
            }
            foreach (var limb in view.limbImages)
            {
                if (limb == null) continue;
                foreach (var img in limb.GetComponentsInChildren<Image>(true)) yield return img;
            }
        }

        /// <summary>Milky and Dune use their own head art; skins only know the Experiment names.</summary>
        private static string Alias(string name)
        {
            switch (name)
            {
                case "milkheadouter": return "headoutline";
                case "milkheadmiddle": return "headMiddle";
                case "headMiddleDune": return "headMiddle";
                case "headOutlineDune": return "headoutline";
                default: return null;
            }
        }
    }

    [HarmonyPatch(typeof(WoundView), "Update")]
    internal static class Patch_WoundView_Update
    {
        private static void Postfix(WoundView __instance)
        {
            try { WoundSkin.Tick(__instance); } catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    [HarmonyPatch(typeof(WoundView), nameof(WoundView.AddImageToLimb))]
    internal static class Patch_WoundView_AddImageToLimb
    {
        private static void Postfix() => WoundSkin.MarkDirty();
    }

    /// <summary>
    /// Minigames (bandaging, lockpicking, defibrillator...): the hand / cursor sprites and the art
    /// of the minigame screens. Only ever the local player's skin, nobody else sees your minigames.
    /// </summary>
    internal static class MinigameSkin
    {
        private static MinigameBase lastBase;
        private static Sprite[] originalHands, originalHandsFurless;
        private static long applied = -1;

        internal static SkinSet Active
        {
            get
            {
                if (!Cfg.Minigame.Value) return null;
                var s = SkinManager.Local;
                return s != null && s.Count(Cat.Minigame) > 0 ? s : null;
            }
        }

        /// <summary>Furless characters use "...alt" hand sprites; most skins only ship the normal names.</summary>
        internal static Sprite Replace(SkinSet skin, Sprite original)
        {
            if (skin == null || original == null) return null;
            string n = original.name;
            string alt = n.EndsWith("alt", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 3) : null;
            return skin.Replace(Cat.Minigame, original, alt);
        }

        /// <summary>Keeps the hand sprite tables of the minigame system in line with the local skin.</summary>
        public static void Sync()
        {
            var mb = MinigameBase.main;
            if (mb == null) { lastBase = null; return; }
            if (mb.handSprites == null) return;

            if (mb != lastBase)
            {
                lastBase = mb;
                applied = -1;
                // MinigameBase.Start may alias handSprites to handSpritesFurless; back up whatever is there now.
                originalHands = (Sprite[])mb.handSprites.Clone();
                originalHandsFurless = mb.handSpritesFurless != null ? (Sprite[])mb.handSpritesFurless.Clone() : null;
            }

            SkinSet skin = Active;
            long want = skin != null ? skin.Serial : 0;
            if (want == applied) return;
            applied = want;

            Fill(mb.handSprites, originalHands, skin);
            if (mb.handSpritesFurless != null && !ReferenceEquals(mb.handSpritesFurless, mb.handSprites))
                Fill(mb.handSpritesFurless, originalHandsFurless, skin);
            try { mb.UpdateHandSprite(true); } catch { /* no minigame open */ }
        }

        private static void Fill(Sprite[] target, Sprite[] originals, SkinSet skin)
        {
            if (target == null || originals == null) return;
            for (int i = 0; i < target.Length && i < originals.Length; i++)
            {
                var o = originals[i];
                target[i] = (skin != null ? Replace(skin, o) : null) ?? o;
            }
        }

        /// <summary>Re-skins every image of the minigame screen that was just built.</summary>
        public static void SweepScreen(MinigameBase mb)
        {
            var skin = Active;
            if (skin == null || mb == null) return;
            Transform root = mb.spawnedMiniGame != null ? mb.spawnedMiniGame : null;
            if (root == null) return;
            foreach (var img in root.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img.sprite == null || SkinSet.IsOurs(img.sprite)) continue;
                var repl = Replace(skin, img.sprite);
                if (repl != null) img.sprite = repl;
            }
        }
    }

    [HarmonyPatch(typeof(MinigameBase), "Start")]
    internal static class Patch_MinigameBase_Start
    {
        private static void Postfix()
        {
            try { MinigameSkin.Sync(); } catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    [HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.StartMinigame))]
    internal static class Patch_MinigameBase_StartMinigame
    {
        private static void Postfix(MinigameBase __instance)
        {
            try
            {
                MinigameSkin.Sync();
                MinigameSkin.SweepScreen(__instance);
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    /// <summary>
    /// Minigames also load sprites by path while running (prosthetic variants, a missing eye...).
    /// Those go through Resources.Load, so the result is swapped there.
    /// </summary>
    [HarmonyPatch(typeof(Resources), nameof(Resources.Load), new[] { typeof(string), typeof(Type) })]
    internal static class Patch_Resources_Load
    {
        private static void Postfix(ref UnityEngine.Object __result)
        {
            if (!(__result is Sprite sprite)) return;
            var skin = MinigameSkin.Active;
            if (skin == null) return;
            try
            {
                var repl = MinigameSkin.Replace(skin, sprite);
                if (repl != null) __result = repl;
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }
}
