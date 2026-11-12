using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Msr
{
    /// <summary>
    /// Custom blood colour for a body: the blood soaking into fur (limb shader), the dripping
    /// particles, and the stains those particles leave on walls and floors.
    /// </summary>
    internal static class Blood
    {
        private const string BloodTexProp = "_SampleTexture2D_722a3646334c45d58299ef3dd9fd21be_Texture_1_Texture2D";
        private static readonly int DarkId = Shader.PropertyToID("_BloodDark");
        private static readonly int LightId = Shader.PropertyToID("_BloodLight");
        private static readonly int TexId = Shader.PropertyToID(BloodTexProp);

        private static readonly AccessTools.FieldRef<Limb, Material> LimbMat = AccessTools.FieldRefAccess<Limb, Material>("mat");
        private static readonly AccessTools.FieldRef<BleedParticle, GameObject> WallBleed = AccessTools.FieldRefAccess<BleedParticle, GameObject>("wallBleed");
        private static readonly AccessTools.FieldRef<BleedParticle, GameObject> GroundBleed = AccessTools.FieldRefAccess<BleedParticle, GameObject>("groundBleed");

        private struct MatBackup { public Color Dark, Light; public Texture Tex; }
        private sealed class Stains { public GameObject Wall, Ground; }

        private static readonly Dictionary<Material, MatBackup> MatOriginals = new Dictionary<Material, MatBackup>();
        private static readonly Dictionary<ParticleSystem, ParticleSystem.MinMaxGradient> ParticleOriginals = new Dictionary<ParticleSystem, ParticleSystem.MinMaxGradient>();
        private static readonly Dictionary<Color32, Texture2D> TintedLimbTex = new Dictionary<Color32, Texture2D>();
        private static readonly Dictionary<Color32, Stains> StainTemplates = new Dictionary<Color32, Stains>();
        private static readonly Dictionary<int, Color32> BodyColors = new Dictionary<int, Color32>();
        private static Color[] limbBloodGray;
        private static int limbBloodW, limbBloodH;
        private static FilterMode limbBloodFilter;
        private static Transform holder;

        /// <summary>Sets (or with null, removes) the blood colour of everything that belongs to a body.</summary>
        public static void ApplyBody(Body body, Color? color)
        {
            if (body == null) return;
            int id = body.GetInstanceID();
            if (color.HasValue) BodyColors[id] = color.Value; else BodyColors.Remove(id);

            if (body.limbs != null)
            {
                foreach (var limb in body.limbs)
                {
                    if (limb == null) continue;
                    Material mat = null;
                    try { mat = LimbMat(limb); } catch { /* field renamed in a game update */ }
                    if (mat != null) ApplyMaterial(mat, color);
                }
            }
            foreach (var bp in body.GetComponentsInChildren<BleedParticle>(true)) ApplyParticle(bp, color);
        }

        public static Color? ColorOf(Body body)
        {
            if (body == null || !Cfg.Blood.Value) return null;
            return BodyColors.TryGetValue(body.GetInstanceID(), out var c) ? (Color?)(Color)c : null;
        }

        private static void ApplyMaterial(Material mat, Color? color)
        {
            if (!mat.HasProperty(DarkId) || !mat.HasProperty(LightId)) return;
            if (!MatOriginals.TryGetValue(mat, out var orig))
            {
                if (!color.HasValue) return; // untouched material, nothing to restore
                orig = new MatBackup
                {
                    Dark = mat.GetColor(DarkId),
                    Light = mat.GetColor(LightId),
                    Tex = mat.HasProperty(TexId) ? mat.GetTexture(TexId) : null,
                };
                MatOriginals[mat] = orig;
                Prune();
            }

            if (!color.HasValue)
            {
                mat.SetColor(DarkId, orig.Dark);
                mat.SetColor(LightId, orig.Light);
                if (orig.Tex != null) mat.SetTexture(TexId, orig.Tex);
                MatOriginals.Remove(mat);
                return;
            }

            Color c = color.Value;
            mat.SetColor(DarkId, new Color(c.r * 0.72f, c.g * 0.72f, c.b * 0.72f, orig.Dark.a));
            mat.SetColor(LightId, new Color(c.r, c.g, c.b, orig.Light.a));
            if (orig.Tex != null)
            {
                var tinted = TintedLimbTexture(orig.Tex, c);
                if (tinted != null) mat.SetTexture(TexId, tinted);
            }
        }

        /// <summary>Recolours one drip emitter and the stains it will leave. Safe to call before its Start ran.</summary>
        public static void ApplyParticle(BleedParticle bp, Color? color)
        {
            if (bp == null || bp.vomit) return;
            var ps = bp.GetComponent<ParticleSystem>();
            if (ps != null)
            {
                var main = ps.main;
                if (color.HasValue)
                {
                    if (!ParticleOriginals.ContainsKey(ps)) ParticleOriginals[ps] = main.startColor;
                    Color c = color.Value;
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(c.r * 0.82f, c.g * 0.82f, c.b * 0.82f, 1f), new Color(c.r, c.g, c.b, 1f));
                }
                else if (ParticleOriginals.TryGetValue(ps, out var orig))
                {
                    main.startColor = orig;
                    ParticleOriginals.Remove(ps);
                }
            }

            try
            {
                if (color.HasValue)
                {
                    var st = TemplatesFor(color.Value, bp.isHuman);
                    if (st != null)
                    {
                        if (st.Wall != null) WallBleed(bp) = st.Wall;
                        if (st.Ground != null) GroundBleed(bp) = st.Ground;
                    }
                }
                else if (IsTemplate(WallBleed(bp)) || IsTemplate(GroundBleed(bp)))
                {
                    WallBleed(bp) = Resources.Load<GameObject>(bp.isHuman ? "wallbloodhuman" : "wallblood");
                    GroundBleed(bp) = Resources.Load<GameObject>(bp.isHuman ? "Special/blockbloodhuman" : "Special/blockblood");
                }
            }
            catch (Exception e)
            {
                Plugin.Verbose("Blood stain recolour skipped: " + e.Message);
            }
        }

        private static bool IsTemplate(GameObject go)
        {
            return go != null && holder != null && go.transform.parent == holder;
        }

        /// <summary>
        /// Stain prefabs in the requested colour. They sit under an inactive holder; the game
        /// instantiates them without a parent, which yields normal active objects.
        /// </summary>
        private static Stains TemplatesFor(Color color, bool human)
        {
            Color32 key = color;
            key.a = (byte)(human ? 254 : 255);
            if (StainTemplates.TryGetValue(key, out var st) && st.Wall != null && st.Ground != null) return st;

            if (holder == null)
            {
                var h = new GameObject("MSR_BloodTemplates");
                h.SetActive(false);
                h.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(h);
                holder = h.transform;
            }

            st = new Stains();
            var wallPrefab = Resources.Load<GameObject>(human ? "wallbloodhuman" : "wallblood");
            if (wallPrefab != null)
            {
                st.Wall = UnityEngine.Object.Instantiate(wallPrefab, holder);
                st.Wall.name = wallPrefab.name; // the game looks at these names nowhere, but other mods might
                var sr = st.Wall.GetComponent<SpriteRenderer>();
                if (sr != null && sr.sprite != null)
                {
                    // The game forces this renderer's colour to white, so the colour has to be in the pixels.
                    var tinted = TintSprite(sr.sprite, color);
                    if (tinted != null) sr.sprite = tinted;
                }
            }
            var groundPrefab = Resources.Load<GameObject>(human ? "Special/blockbloodhuman" : "Special/blockblood");
            if (groundPrefab != null)
            {
                st.Ground = UnityEngine.Object.Instantiate(groundPrefab, holder);
                st.Ground.name = groundPrefab.name;
                var sr = st.Ground.GetComponent<SpriteRenderer>();
                if (sr != null) sr.color = new Color(color.r, color.g, color.b, sr.color.a); // the game keeps rgb, randomises alpha
            }
            StainTemplates[key] = st;
            return st;
        }

        // ------------------------------------------------------------------ texture work

        private static Color[] ReadPixels(Texture src, out int w, out int h)
        {
            w = src.width; h = src.height;
            if (src is Texture2D t2 && t2.isReadable)
            {
                try { return t2.GetPixels(); } catch { /* compressed or otherwise unreadable: fall through */ }
            }
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tmp.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var px = tmp.GetPixels();
            UnityEngine.Object.Destroy(tmp);
            return px;
        }

        /// <summary>Brightness of each pixel, stretched so the brightest pixel is 1. Alpha kept.</summary>
        private static Color[] ToIntensity(Color[] px)
        {
            float max = 0.0001f;
            for (int i = 0; i < px.Length; i++)
            {
                float v = Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b));
                if (px[i].a > 0.01f && v > max) max = v;
            }
            var res = new Color[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                float v = Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b)) / max;
                res[i] = new Color(v, v, v, px[i].a);
            }
            return res;
        }

        private static Texture2D TintedLimbTexture(Texture original, Color color)
        {
            Color32 key = color; key.a = 255;
            if (TintedLimbTex.TryGetValue(key, out var cached) && cached != null) return cached;
            try
            {
                if (limbBloodGray == null)
                {
                    limbBloodGray = ToIntensity(ReadPixels(original, out limbBloodW, out limbBloodH));
                    limbBloodFilter = original.filterMode;
                }
                var px = new Color[limbBloodGray.Length];
                for (int i = 0; i < px.Length; i++)
                {
                    var g = limbBloodGray[i];
                    px[i] = new Color(g.r * color.r, g.r * color.g, g.r * color.b, g.a);
                }
                var tex = new Texture2D(limbBloodW, limbBloodH, TextureFormat.RGBA32, false);
                tex.SetPixels(px);
                tex.Apply(false, false);
                tex.filterMode = limbBloodFilter;
                tex.wrapMode = original.wrapMode;
                tex.name = "MSR_limbBlood_" + ColorUtility.ToHtmlStringRGB(color);
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                TintedLimbTex[key] = tex;
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Verbose("Could not tint the limb blood texture: " + e.Message);
                return null;
            }
        }

        private static Sprite TintSprite(Sprite sprite, Color color)
        {
            try
            {
                var all = ToIntensity(ReadPixels(sprite.texture, out int tw, out int th));
                var r = sprite.textureRect;
                int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height), x0 = Mathf.RoundToInt(r.x), y0 = Mathf.RoundToInt(r.y);
                var px = new Color[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var g = all[(y + y0) * tw + x + x0];
                        px[y * w + x] = new Color(g.r * color.r, g.r * color.g, g.r * color.b, g.a);
                    }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels(px);
                tex.Apply(false, false);
                tex.filterMode = sprite.texture.filterMode;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                var s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(sprite.pivot.x / r.width, sprite.pivot.y / r.height), sprite.pixelsPerUnit, 0, SpriteMeshType.FullRect);
                s.name = sprite.name;
                s.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return s;
            }
            catch (Exception e)
            {
                Plugin.Verbose("Could not tint stain sprite " + sprite.name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Drops bookkeeping for materials and particle systems that no longer exist.</summary>
        private static void Prune()
        {
            if (MatOriginals.Count < 256 && ParticleOriginals.Count < 256) return;
            var deadMats = new List<Material>();
            foreach (var m in MatOriginals.Keys) if (m == null) deadMats.Add(m);
            foreach (var m in deadMats) MatOriginals.Remove(m);
            var deadPs = new List<ParticleSystem>();
            foreach (var p in ParticleOriginals.Keys) if (p == null) deadPs.Add(p);
            foreach (var p in deadPs) ParticleOriginals.Remove(p);
        }
    }

    /// <summary>Drip emitters fetch their stain prefabs in Start; re-apply the owner's colour right after.</summary>
    [HarmonyPatch(typeof(BleedParticle), "Start")]
    internal static class Patch_BleedParticle_Start
    {
        private static void Postfix(BleedParticle __instance)
        {
            try
            {
                var body = __instance.GetComponentInParent<Body>();
                var color = Blood.ColorOf(body);
                if (color.HasValue) Blood.ApplyParticle(__instance, color);
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }
}
