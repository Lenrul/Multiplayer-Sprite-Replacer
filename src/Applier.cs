using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Msr
{
    /// <summary>
    /// Swaps the world sprites of player bodies (limbs, tail, face, eyes) and remembers the
    /// vanilla ones so a body can always be restored.
    /// </summary>
    internal static class Applier
    {
        private sealed class BodyState
        {
            public Body Body;
            public long Applied = -1;
            public readonly Dictionary<SpriteRenderer, Sprite> Renderers = new Dictionary<SpriteRenderer, Sprite>();
            public readonly List<FaceBackup> Faces = new List<FaceBackup>();
        }

        private sealed class FaceBackup
        {
            public FacialExpression Face;
            public Sprite DefaultHead, Mouth, MouthHalf, EyesGone, EyesGoneHealed;
            public Sprite[] Disfigured, DisfiguredHeal;
            public List<Eye> Eyes;
        }

        private static readonly Dictionary<int, BodyState> Bodies = new Dictionary<int, BodyState>();
        private static readonly List<int> Dead = new List<int>();
        public static bool Dirty;

        // Eye order of FacialExpression.eyeList, as Experiment sprite names (front, back).
        private static readonly string[,] EyeNames =
        {
            { "experimentEyeOpen", "experimentEyeLookBack" },
            { "experimentEyeHalfClosed", "experimentEyeHalfClosedBack" },
            { "experimentEyeClosed", "experimentEyeClosed" },
            { "experimentEyeScared", "experimentEyeScaredBack" },
            { "experimentEyePanic", "experimentEyePanic" },
            { "experimentEyeSad", "experimentEyeSadBack" },
            { "experimentEyeHappy", "experimentEyeHappy" },
            { "experimentEyeLockedIn", "experimentEyeLockedInBack" },
        };

        public static void Track(Body body)
        {
            if (body == null) return;
            int id = body.GetInstanceID();
            if (!Bodies.ContainsKey(id)) Bodies[id] = new BodyState { Body = body };
            Dirty = true;
        }

        public static void OnSceneLoaded()
        {
            Bodies.Clear();
            WoundSkin.Reset();
            foreach (var b in UnityEngine.Object.FindObjectsOfType<Body>(true)) Track(b);
            Dirty = true;
        }

        /// <summary>Brings every tracked body in line with the skin it should currently wear. Cheap when nothing changed.</summary>
        public static void Tick()
        {
            Dirty = false;
            int flags = Cfg.FlagsStamp();
            foreach (var kv in Bodies)
            {
                var st = kv.Value;
                if (st.Body == null) { Dead.Add(kv.Key); continue; }
                SkinSet skin = SkinManager.Resolve(st.Body);
                long want = ((long)(skin != null ? skin.Serial : 0) << 8) | (long)flags;
                if (want == st.Applied) continue;
                try
                {
                    Revert(st);
                    if (skin != null) Apply(st, skin);
                    Blood.ApplyBody(st.Body, skin != null && Cfg.Blood.Value ? skin.Blood : null);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Applying skin to " + st.Body.name + " failed: " + e);
                }
                st.Applied = want;
            }
            if (Dead.Count > 0)
            {
                foreach (var id in Dead) Bodies.Remove(id);
                Dead.Clear();
            }
            MinigameSkin.Sync();
        }

        // ------------------------------------------------------------------ apply / revert

        private static void Apply(BodyState st, SkinSet skin)
        {
            bool cross = Cfg.CrossCharacter.Value;
            var body = st.Body;

            if (body.limbs != null)
            {
                foreach (var limb in body.limbs)
                {
                    if (limb == null) continue;
                    Swap(st, limb.GetComponent<SpriteRenderer>(), skin, cross ? RoleName(limb.gameObject.name) : null);
                }
            }
            foreach (var tail in body.GetComponentsInChildren<TailScript>(true))
                Swap(st, tail.GetComponent<SpriteRenderer>(), skin, cross ? "experimentTail" : null);

            foreach (var face in body.GetComponentsInChildren<FacialExpression>(true))
            {
                var bk = new FaceBackup
                {
                    Face = face,
                    DefaultHead = face.defaultHead, Mouth = face.defaultHeadMouth, MouthHalf = face.defaultHeadMouthHalf,
                    EyesGone = face.eyesGone, EyesGoneHealed = face.eyesGoneHealed,
                    Disfigured = face.disfiguredHead != null ? (Sprite[])face.disfiguredHead.Clone() : null,
                    DisfiguredHeal = face.disfiguredHeadHeal != null ? (Sprite[])face.disfiguredHeadHeal.Clone() : null,
                    Eyes = face.eyeList != null ? new List<Eye>(face.eyeList) : null,
                };
                st.Faces.Add(bk);

                // The head renderer is also a limb renderer and was swapped above; FacialExpression
                // overwrites it every frame from these fields, so the fields are what really matters.
                face.defaultHead = Face(skin, face.defaultHead, cross, "experimentHeadBack", "experimentHead");
                face.defaultHeadMouth = Face(skin, face.defaultHeadMouth, cross, "experimentHeadBackMouth");
                face.defaultHeadMouthHalf = Face(skin, face.defaultHeadMouthHalf, cross, "experimentHeadBackMouthMini");
                face.eyesGone = Face(skin, face.eyesGone, cross, "experimentEyeGone");
                face.eyesGoneHealed = Face(skin, face.eyesGoneHealed, cross, "experimentEyeGoneHealed", "experimentEyeGone");
                if (face.disfiguredHead != null)
                    for (int i = 0; i < face.disfiguredHead.Length; i++)
                        face.disfiguredHead[i] = Face(skin, face.disfiguredHead[i], cross, "experimentHeadDisfigured" + (i + 1));
                if (face.disfiguredHeadHeal != null)
                    for (int i = 0; i < face.disfiguredHeadHeal.Length; i++)
                        face.disfiguredHeadHeal[i] = Face(skin, face.disfiguredHeadHeal[i], cross, "experimentHeadDisfigured" + (i + 1) + "Healed");
                if (face.eyeList != null)
                {
                    for (int i = 0; i < face.eyeList.Count; i++)
                    {
                        Eye eye = face.eyeList[i];
                        string front = i < EyeNames.GetLength(0) ? EyeNames[i, 0] : null;
                        string back = i < EyeNames.GetLength(0) ? EyeNames[i, 1] : null;
                        // "Locked in" eyes are newer than most skins: fall back to the half-closed pair.
                        string frontAlt = i == 7 ? "experimentEyeHalfClosed" : null;
                        string backAlt = i == 7 ? "experimentEyeHalfClosedBack" : front;
                        eye.front = Face(skin, eye.front, cross, front, frontAlt);
                        eye.back = Face(skin, eye.back, cross, back, backAlt);
                        face.eyeList[i] = eye;
                    }
                }
                if (face.nosebleedSprite != null) Swap(st, face.nosebleedSprite, skin, "experimentNosebleed");
                if (face.eyes != null) Swap(st, face.eyes, skin, null);
            }
        }

        private static Sprite Face(SkinSet skin, Sprite current, bool cross, params string[] roles)
        {
            if (current == null) return null;
            var img = skin.Find(Cat.Body, current.name);
            if (img == null && cross && roles != null)
                for (int i = 0; i < roles.Length && img == null; i++)
                    if (roles[i] != null) img = skin.Find(Cat.Body, roles[i]);
            return img != null ? img.For(current) : current;
        }

        private static void Swap(BodyState st, SpriteRenderer sr, SkinSet skin, string role)
        {
            if (sr == null || sr.sprite == null) return;
            Sprite original = st.Renderers.TryGetValue(sr, out var o) && o != null ? o : sr.sprite;
            if (SkinSet.IsOurs(original)) return; // never treat one of our own sprites as the vanilla one
            Sprite repl = skin.Replace(Cat.Body, original, role);
            if (repl == null) return;
            st.Renderers[sr] = original;
            sr.sprite = repl;
        }

        private static void Revert(BodyState st)
        {
            foreach (var kv in st.Renderers)
                if (kv.Key != null && kv.Value != null) kv.Key.sprite = kv.Value;
            st.Renderers.Clear();

            foreach (var bk in st.Faces)
            {
                var f = bk.Face;
                if (f == null) continue;
                f.defaultHead = bk.DefaultHead;
                f.defaultHeadMouth = bk.Mouth;
                f.defaultHeadMouthHalf = bk.MouthHalf;
                f.eyesGone = bk.EyesGone;
                f.eyesGoneHealed = bk.EyesGoneHealed;
                if (bk.Disfigured != null) f.disfiguredHead = bk.Disfigured;
                if (bk.DisfiguredHeal != null) f.disfiguredHeadHeal = bk.DisfiguredHeal;
                if (bk.Eyes != null) f.eyeList = bk.Eyes;
            }
            st.Faces.Clear();
        }

        /// <summary>Limb object name -> the Experiment sprite that body part uses.</summary>
        internal static string RoleName(string limbObject)
        {
            switch (limbObject)
            {
                case "Head": return "experimentHeadBack";
                case "UpTorso": return "experimentUpTorso";
                case "DownTorso": return "experimentDownTorso";
                case "UpArmF": case "UpArmB": return "experimentUpArm";
                case "DownArmF": case "DownArmB": return "experimentDownArm";
                case "HandF": return "experimentHandF";
                case "HandB": return "experimentHandB";
                case "ThighF": case "ThighB": return "experimentThigh";
                case "CrusF": case "CrusB": return "experimentCrus";
                case "FootF": case "FootB": return "experimentFoot";
                default: return null;
            }
        }

        /// <summary>Replacement for a sprite the game is about to attach to a limb (bandage wrap, ash, broken bone...).</summary>
        internal static Sprite ForLimbOverlay(Limb limb, Sprite sprite)
        {
            if (limb == null || sprite == null) return sprite;
            // Resources.Load may already have handed out the local player's version; start from vanilla
            // so the limb gets the art of the skin its own body wears.
            Sprite vanilla = SkinSet.Vanilla(sprite);
            var skin = SkinManager.Resolve(limb.body);
            if (skin == null) return vanilla;
            return skin.Replace(Cat.Body, vanilla) ?? vanilla;
        }
    }

    // ---------------------------------------------------------------------- patches

    [HarmonyPatch(typeof(Body), "Awake")]
    internal static class Patch_Body_Awake
    {
        private static void Postfix(Body __instance) => Applier.Track(__instance);
    }

    [HarmonyPatch(typeof(Limb), nameof(Limb.CreateTemporarySprite))]
    internal static class Patch_Limb_CreateTemporarySprite
    {
        private static void Prefix(Limb __instance, ref Sprite spr)
        {
            try { spr = Applier.ForLimbOverlay(__instance, spr); } catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    [HarmonyPatch(typeof(Limb), "MakeBoneSprite")]
    internal static class Patch_Limb_MakeBoneSprite
    {
        private static readonly AccessTools.FieldRef<Limb, GameObject> Bone = AccessTools.FieldRefAccess<Limb, GameObject>("brokenBoneObj");

        private static void Postfix(Limb __instance)
        {
            try
            {
                var go = Bone(__instance);
                if (go == null) return;
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null) sr.sprite = Applier.ForLimbOverlay(__instance, sr.sprite);
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }
}
