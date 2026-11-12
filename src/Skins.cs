using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Msr
{
    /// <summary>Which part of the game a sprite of a skin replaces. Decided by the sub-folder it sits in.</summary>
    internal enum Cat : byte
    {
        Body = 0,      // Body/, Head/, anything else: limbs, tail, face, eyes, bandage wraps, broken bone
        Wound = 1,     // WoundView/: health panel diagram and wound overlays
        Minigame = 2,  // Minigame/: hands / cursors and minigame art (never sent to other players)
    }

    /// <summary>One PNG of a skin. Sprites are created on demand so they match the sprite they replace.</summary>
    internal sealed class SkinImage
    {
        public string Name;
        public byte[] Png;
        public Texture2D Tex;
        private readonly Dictionary<long, Sprite> sprites = new Dictionary<long, Sprite>();

        /// <summary>A sprite of this image with the same pixels-per-unit and relative pivot as <paramref name="original"/>.</summary>
        public Sprite For(Sprite original)
        {
            if (Tex == null) return null;
            float ppu = 8f;
            Vector2 pivot = new Vector2(0.5f, 0.5f);
            string name = Name;
            if (original != null)
            {
                ppu = original.pixelsPerUnit;
                var r = original.rect;
                if (r.width > 0 && r.height > 0) pivot = new Vector2(original.pivot.x / r.width, original.pivot.y / r.height);
                name = original.name;
            }
            long key = ((long)Mathf.RoundToInt(ppu * 100f) << 40) ^ ((long)Mathf.RoundToInt(pivot.x * 1000f) << 20) ^ (long)Mathf.RoundToInt(pivot.y * 1000f);
            if (sprites.TryGetValue(key, out var s) && s != null) return s;
            s = Sprite.Create(Tex, new Rect(0, 0, Tex.width, Tex.height), pivot, ppu, 0, SpriteMeshType.FullRect);
            s.name = name; // keep the vanilla name: the game and other mods look sprites up by name
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            sprites[key] = s;
            SkinSet.Ours.Add(s.GetInstanceID());
            if (original != null && !SkinSet.IsOurs(original)) SkinSet.OriginalOf[s.GetInstanceID()] = original;
            return s;
        }

        public void Destroy()
        {
            foreach (var s in sprites.Values)
            {
                if (s == null) continue;
                SkinSet.Ours.Remove(s.GetInstanceID());
                SkinSet.OriginalOf.Remove(s.GetInstanceID());
                UnityEngine.Object.Destroy(s);
            }
            sprites.Clear();
            if (Tex != null) UnityEngine.Object.Destroy(Tex);
            Tex = null;
        }
    }

    /// <summary>A loaded skin: either the local one read from disk or one received from another player.</summary>
    internal sealed class SkinSet
    {
        /// <summary>Instance ids of every sprite this mod created, to tell "ours" from vanilla.</summary>
        public static readonly HashSet<int> Ours = new HashSet<int>();
        /// <summary>Our sprite (instance id) -> the vanilla sprite it stands in for.</summary>
        public static readonly Dictionary<int, Sprite> OriginalOf = new Dictionary<int, Sprite>();

        /// <summary>The vanilla sprite behind one of ours; the sprite itself when it already is vanilla.</summary>
        public static Sprite Vanilla(Sprite s)
        {
            if (s == null) return null;
            return OriginalOf.TryGetValue(s.GetInstanceID(), out var o) && o != null ? o : s;
        }
        private static int nextSerial = 1;

        public const int MaxImageSide = 2048;

        public readonly int Serial = nextSerial++;
        public string Name = "";
        public Color? Blood;
        public readonly Dictionary<string, SkinImage>[] Images =
        {
            new Dictionary<string, SkinImage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SkinImage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SkinImage>(StringComparer.OrdinalIgnoreCase),
        };

        public static bool IsOurs(Sprite s) => s != null && Ours.Contains(s.GetInstanceID());

        public int Count(Cat c) => Images[(int)c].Count;

        public SkinImage Find(Cat cat, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Images[(int)cat].TryGetValue(name, out var img) ? img : null;
        }

        /// <summary>Replacement for <paramref name="original"/>, trying its own name and then the given fallbacks.</summary>
        public Sprite Replace(Cat cat, Sprite original, params string[] fallbackNames)
        {
            if (original == null) return null;
            var img = Find(cat, original.name);
            if (img == null && fallbackNames != null)
            {
                for (int i = 0; i < fallbackNames.Length && img == null; i++) img = Find(cat, fallbackNames[i]);
            }
            return img?.For(original);
        }

        public bool Add(Cat cat, string name, byte[] png)
        {
            if (!LooksLikePng(png)) return false;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png, false) || tex.width > MaxImageSide || tex.height > MaxImageSide)
            {
                UnityEngine.Object.Destroy(tex);
                return false;
            }
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "MSR_" + name;
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (Images[(int)cat].TryGetValue(name, out var old)) old.Destroy();
            Images[(int)cat][name] = new SkinImage { Name = name, Png = png, Tex = tex };
            return true;
        }

        public void Destroy()
        {
            foreach (var d in Images)
            {
                foreach (var img in d.Values) img.Destroy();
                d.Clear();
            }
        }

        public static bool LooksLikePng(byte[] d)
        {
            return d != null && d.Length > 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
        }

        // ------------------------------------------------------------------ network package

        private const byte PackageVersion = 1;
        public const int MaxPackageBytes = 6 * 1024 * 1024;       // compressed, as sent
        private const int MaxUnpackedBytes = 24 * 1024 * 1024;
        private const int MaxImages = 600;

        /// <summary>Everything other players need to draw this skin (body + wound view + blood). Null when there is nothing to send.</summary>
        public byte[] ToPackage()
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                using (var w = new BinaryWriter(z))
                {
                    w.Write(PackageVersion);
                    w.Write(Name ?? "");
                    w.Write(Blood.HasValue);
                    Color32 b = Blood ?? Color.white;
                    w.Write(b.r); w.Write(b.g); w.Write(b.b);
                    var list = new List<KeyValuePair<Cat, SkinImage>>();
                    foreach (var img in Images[(int)Cat.Body].Values) list.Add(new KeyValuePair<Cat, SkinImage>(Cat.Body, img));
                    foreach (var img in Images[(int)Cat.Wound].Values) list.Add(new KeyValuePair<Cat, SkinImage>(Cat.Wound, img));
                    w.Write((ushort)list.Count);
                    foreach (var kv in list)
                    {
                        w.Write((byte)kv.Key);
                        w.Write(kv.Value.Name);
                        w.Write(kv.Value.Png.Length);
                        w.Write(kv.Value.Png);
                    }
                }
                var bytes = ms.ToArray();
                if (bytes.Length > MaxPackageBytes)
                {
                    Plugin.Log.LogWarning("Skin '" + Name + "' is too large to share (" + bytes.Length / 1024 + " KB); other players will not see it.");
                    return null;
                }
                return bytes;
            }
        }

        /// <summary>Builds a skin from data sent by another player. Everything in it is untrusted.</summary>
        public static SkinSet FromPackage(byte[] package)
        {
            if (package == null || package.Length == 0 || package.Length > MaxPackageBytes) return null;
            byte[] raw;
            using (var src = new MemoryStream(package))
            using (var z = new DeflateStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                var buf = new byte[64 * 1024];
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    if (dst.Length + n > MaxUnpackedBytes) return null;
                    dst.Write(buf, 0, n);
                }
                raw = dst.ToArray();
            }

            var set = new SkinSet();
            try
            {
                using (var r = new BinaryReader(new MemoryStream(raw)))
                {
                    if (r.ReadByte() != PackageVersion) { set.Destroy(); return null; }
                    set.Name = Clip(r.ReadString(), 64);
                    bool hasBlood = r.ReadBoolean();
                    byte cr = r.ReadByte(), cg = r.ReadByte(), cb = r.ReadByte();
                    if (hasBlood) set.Blood = new Color32(cr, cg, cb, 255);
                    int count = r.ReadUInt16();
                    if (count > MaxImages) { set.Destroy(); return null; }
                    for (int i = 0; i < count; i++)
                    {
                        byte cat = r.ReadByte();
                        string name = r.ReadString();
                        int len = r.ReadInt32();
                        if (len < 0 || len > raw.Length) { set.Destroy(); return null; }
                        byte[] png = r.ReadBytes(len);
                        if (png.Length != len) { set.Destroy(); return null; }
                        if (cat > (byte)Cat.Wound || string.IsNullOrEmpty(name) || name.Length > 96) continue;
                        set.Add((Cat)cat, name, png);
                    }
                }
            }
            catch (Exception)
            {
                set.Destroy();
                return null;
            }
            return set;
        }

        private static string Clip(string s, int max) => s == null ? "" : (s.Length > max ? s.Substring(0, max) : s);
    }

    /// <summary>A skin folder on disk, as listed in the Skins tab.</summary>
    internal sealed class SkinInfo
    {
        public string Folder;      // folder name inside CustomSprites, also the stable id
        public string Path;
        public string DisplayName;
        public string Author;
        public string PreviewFile; // from the skin's metadata, relative to the folder
        public Texture2D Preview;  // lazily built
        public bool PreviewTried;
    }

    /// <summary>Finds skin folders under BepInEx/plugins/CustomSprites and loads them.</summary>
    internal static class SkinLibrary
    {
        public static readonly List<SkinInfo> Skins = new List<SkinInfo>();
        private static readonly string[] PreviewNames = { "preview", "icon", "thumbnail" };

        public static string Root => System.IO.Path.Combine(Paths.PluginPath, "CustomSprites");

        public static SkinInfo Get(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            return Skins.FirstOrDefault(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase));
        }

        public static void Rescan()
        {
            var old = Skins.ToDictionary(s => s.Folder, s => s, StringComparer.OrdinalIgnoreCase);
            Skins.Clear();
            try
            {
                Directory.CreateDirectory(Root);
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    string folder = System.IO.Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folder) || folder.StartsWith(".") || folder.StartsWith("_")) continue;
                    if (!Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories).Any()) continue;

                    var info = new SkinInfo { Folder = folder, Path = dir, DisplayName = folder };
                    ReadMeta(info, out _);
                    if (old.TryGetValue(folder, out var prev) && prev.Preview != null)
                    {
                        UnityEngine.Object.Destroy(prev.Preview); // files may have changed; rebuilt on demand
                    }
                    Skins.Add(info);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not list skins: " + e.Message);
            }
            Skins.Sort((a, b) => NaturalCompare(a.Folder, b.Folder));

            // First run: pin the first skins to hotkeys 1..N so the number keys work out of the box.
            if (!State.I.PinsInitialised && Skins.Count > 0)
            {
                for (int i = 0; i < Skins.Count && i < Cfg.SlotCount; i++) State.I.Pins[Skins[i].Folder] = i + 1;
                State.I.PinsInitialised = true;
                State.Save();
            }
        }

        private static void ReadMeta(SkinInfo info, out Color? blood)
        {
            blood = null;
            // skin.json is this mod's own file; meta.json is what skin.cat-bot.de puts in its archives.
            string meta = System.IO.Path.Combine(info.Path, "skin.json");
            if (!File.Exists(meta)) meta = System.IO.Path.Combine(info.Path, "meta.json");
            if (!File.Exists(meta)) return;
            try
            {
                var o = JObject.Parse(File.ReadAllText(meta));
                string name = (string)o["displayName"];
                if (string.IsNullOrWhiteSpace(name)) name = (string)o["name"];
                if (!string.IsNullOrWhiteSpace(name)) info.DisplayName = name.Trim();
                info.Author = (string)o["author"];
                string preview = o["preview"] != null && o["preview"].Type == JTokenType.String ? (string)o["preview"] : null;
                if (!string.IsNullOrWhiteSpace(preview) && preview.IndexOf("..", StringComparison.Ordinal) < 0 && !System.IO.Path.IsPathRooted(preview))
                    info.PreviewFile = preview;
                string hex = (string)o["bloodColor"] ?? (string)o["blood"];
                if (!string.IsNullOrWhiteSpace(hex))
                {
                    if (!hex.StartsWith("#")) hex = "#" + hex;
                    if (ColorUtility.TryParseHtmlString(hex, out var c)) blood = new Color(c.r, c.g, c.b, 1f);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("skin.json of '" + info.Folder + "' is not valid: " + e.Message);
            }
        }

        private static Cat CategoryOf(string skinRoot, string file)
        {
            string rel = file.Substring(skinRoot.Length).TrimStart('\\', '/');
            int cut = rel.IndexOfAny(new[] { '\\', '/' });
            if (cut <= 0) return Cat.Body;
            string top = FolderKey(rel.Substring(0, cut));
            if (top == "woundview" || top == "wound" || top == "healthpanel") return Cat.Wound;
            if (top == "minigame" || top == "minigames" || top == "hands") return Cat.Minigame;
            return Cat.Body;
        }

        /// <summary>"WoundView.legacy" -> "woundview": the skin site tags folders with a suffix.</summary>
        internal static string FolderKey(string folderName)
        {
            string s = (folderName ?? "").ToLowerInvariant();
            int dot = s.IndexOf('.');
            return dot > 0 ? s.Substring(0, dot) : s;
        }

        public static SkinSet Load(SkinInfo info)
        {
            var set = new SkinSet { Name = info.DisplayName };
            ReadMeta(info, out var blood);
            byte[] bloodPng = null;
            foreach (var file in Directory.GetFiles(info.Path, "*.png", SearchOption.AllDirectories))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                bool atRoot = string.Equals(System.IO.Path.GetDirectoryName(file).TrimEnd('\\', '/'), info.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                if (atRoot && PreviewNames.Contains(name.ToLowerInvariant())) continue;
                if (info.PreviewFile != null && string.Equals(System.IO.Path.GetFullPath(file), System.IO.Path.GetFullPath(System.IO.Path.Combine(info.Path, info.PreviewFile)), StringComparison.OrdinalIgnoreCase)) continue;
                byte[] png;
                try { png = File.ReadAllBytes(file); }
                catch (Exception e) { Plugin.Log.LogWarning("Could not read " + file + ": " + e.Message); continue; }

                if (string.Equals(name, "Blood", StringComparison.OrdinalIgnoreCase))
                {
                    bloodPng = png; // a colour swatch, not a sprite
                    continue;
                }
                if (!set.Add(CategoryOf(info.Path, file), name, png))
                    Plugin.Log.LogWarning("Skipped " + file + " (not a PNG, or larger than " + SkinSet.MaxImageSide + " px).");
            }
            // Older skin packs keep everything the game loads by path in Minigame/. Bandage wraps and
            // wound marks from there are drawn on the body and the health panel, which other players
            // see too, so file them where they are used (Minigame/ is never sent over the network).
            foreach (var n in LimbOverlayNames) Mirror(set, n, Cat.Body);
            foreach (var n in PanelOverlayNames) Mirror(set, n, Cat.Wound);
            if (blood == null && bloodPng != null) blood = DominantColor(bloodPng);
            set.Blood = blood;
            Plugin.Verbose("Loaded skin '" + info.Folder + "': " + set.Count(Cat.Body) + " body, " + set.Count(Cat.Wound) +
                           " wound view, " + set.Count(Cat.Minigame) + " minigame sprites, blood " + (blood.HasValue ? "#" + ColorUtility.ToHtmlStringRGB(blood.Value) : "vanilla"));
            return set;
        }

        private static readonly string[] LimbOverlayNames = { "bandageWrap", "alginateWrap", "ashSprite", "musharmwrap", "brokenBone", "footglass" };
        private static readonly string[] PanelOverlayNames = { "injectionWound", "suicideWound", "selfHarmWound", "splintIcon", "tourniquetIcon", "footglasshealthpanel" };

        private static void Mirror(SkinSet set, string name, Cat to)
        {
            var src = set.Find(Cat.Minigame, name);
            if (src != null && set.Find(to, name) == null) set.Add(to, name, src.Png);
        }

        /// <summary>Changes whenever a file of the skin is added, removed or rewritten.</summary>
        public static long Stamp(SkinInfo info)
        {
            long h = 17;
            try
            {
                foreach (var f in Directory.GetFiles(info.Path, "*.*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    h = h * 31 + fi.LastWriteTimeUtc.Ticks;
                    h = h * 31 + fi.Length;
                    h = h * 31 + f.GetHashCode();
                }
            }
            catch { return 0; }
            return h;
        }

        /// <summary>Most common colour of a swatch image (the old MSR "Blood.png" convention).</summary>
        public static Color? DominantColor(byte[] png)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(png, false)) return null;
                var px = tex.GetPixels32();
                var buckets = new Dictionary<int, long[]>();
                foreach (var c in px)
                {
                    if (c.a < 64) continue;
                    int key = ((c.r >> 4) << 8) | ((c.g >> 4) << 4) | (c.b >> 4);
                    if (!buckets.TryGetValue(key, out var acc)) buckets[key] = acc = new long[4];
                    acc[0] += c.r; acc[1] += c.g; acc[2] += c.b; acc[3]++;
                }
                long[] best = null;
                foreach (var acc in buckets.Values) if (best == null || acc[3] > best[3]) best = acc;
                if (best == null) return null;
                return new Color(best[0] / (float)best[3] / 255f, best[1] / (float)best[3] / 255f, best[2] / (float)best[3] / 255f, 1f);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }

        // ------------------------------------------------------------------ previews

        public static Texture2D PreviewOf(SkinInfo info)
        {
            if (info.Preview != null || info.PreviewTried) return info.Preview;
            info.PreviewTried = true;
            try
            {
                var candidates = new List<string>();
                if (info.PreviewFile != null) candidates.Add(info.PreviewFile);
                foreach (var n in PreviewNames) candidates.Add(n + ".png");
                foreach (var n in candidates)
                {
                    string p = System.IO.Path.Combine(info.Path, n);
                    if (!File.Exists(p)) continue;
                    var t = LoadTex(File.ReadAllBytes(p));
                    if (t != null) { info.Preview = t; return t; }
                }
                info.Preview = Preview.Compose(info.Path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not build a preview for '" + info.Folder + "': " + e.Message);
            }
            return info.Preview;
        }

        internal static Texture2D LoadTex(byte[] png)
        {
            if (!SkinSet.LooksLikePng(png)) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!t.LoadImage(png, false)) { UnityEngine.Object.Destroy(t); return null; }
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        /// <summary>st2 before st10, "Skin 9" before "Skin 10".</summary>
        private static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    long x = 0, y = 0;
                    while (i < a.Length && char.IsDigit(a[i])) x = x * 10 + (a[i++] - '0');
                    while (j < b.Length && char.IsDigit(b[j])) y = y * 10 + (b[j++] - '0');
                    if (x != y) return x < y ? -1 : 1;
                }
                else
                {
                    int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    /// <summary>Builds a paper-doll picture of a skin from its body part PNGs, in the character's rest pose.</summary>
    internal static class Preview
    {
        private struct Part
        {
            public string[] Names; public float X, Y, Rot, Shade;
            public Part(float x, float y, float rot, float shade, params string[] names) { X = x; Y = y; Rot = rot; Shade = shade; Names = names; }
        }

        // Positions are the limb positions of the character prefab, in pixels (1 unit = 8 px), drawn back to front.
        private static readonly Part[] Layout =
        {
            new Part( 2,   7,    0, 0.72f, "experimentUpArm", "milkyuparm", "duneUpArmAlt"),
            new Part( 2,  -3,    0, 0.72f, "experimentDownArm", "mildownarm", "duneDownArmAlt"),
            new Part( 2, -10,    0, 0.72f, "experimentHandB", "milkyhandb", "duneHandAlt"),
            new Part( 4, -19.4f, 30, 0.72f, "experimentFoot", "milkyfoot", "duneFoot"),
            new Part( 3, -12,    0, 0.72f, "experimentCrus", "milkycrus", "duneCrus"),
            new Part( 2, -4.5f,  0, 0.72f, "experimentThigh", "milkythigh", "duneThighAlt"),
            new Part( 0, -0.8f, 23.6f, 1f, "experimentTail", "milkytail", "duneTail"),
            new Part( 0,   2,    0, 0.9f, "experimentDownTorso", "milkydowntorso", "duneDownTorso"),
            new Part( 0,  10,    0, 0.9f, "experimentUpTorso", "milkytorso", "duneUpTorso"),
            new Part( 0,  20,    0, 0.9f, "experimentHeadBack", "experimentHead", "milkyheadback", "duneHeadBack"),
            new Part( 0,  20,    0, 1f,   "experimentEyeOpen", "milkyeyeopen", "duneEyes"),
            new Part( 1.3f, -19.4f, 30, 0.9f, "experimentFoot", "milkyfoot", "duneFoot"),
            new Part( 0, -12,    0, 0.9f, "experimentCrus", "milkycrus", "duneCrus"),
            new Part(-1, -4.5f,  0, 0.9f, "experimentThigh", "milkythigh", "duneThigh"),
            new Part(-1,   7,    0, 1f,   "experimentUpArm", "milkyuparm", "duneUpArm"),
            new Part(-1,  -3,    0, 1f,   "experimentDownArm", "mildownarm", "duneDownArm"),
            new Part(-1, -10,    0, 1f,   "experimentHandF", "milkyhandf", "duneHand"),
        };

        public static Texture2D Compose(string skinPath)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string firstAny = null;
            foreach (var f in Directory.GetFiles(skinPath, "*.png", SearchOption.AllDirectories))
            {
                string rel = f.Substring(skinPath.Length).ToLowerInvariant();
                if (firstAny == null) firstAny = f;
                if (rel.Contains("woundview") || rel.Contains("minigame")) continue;
                // pictures lying directly in the skin folder are previews, not body parts
                if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(f)).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(skinPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                files[Path.GetFileNameWithoutExtension(f)] = f;
            }

            const int W = 64, H = 64, OX = 32, OY = 30;
            var canvas = new Color32[W * H];
            int drawn = 0;
            foreach (var part in Layout)
            {
                string file = null;
                foreach (var n in part.Names) if (files.TryGetValue(n, out file)) break; else file = null;
                if (file == null) continue;
                var tex = SkinLibrary.LoadTex(File.ReadAllBytes(file));
                if (tex == null) continue;
                Blit(canvas, W, H, tex, OX + part.X, OY + part.Y, part.Rot, part.Shade);
                UnityEngine.Object.Destroy(tex);
                drawn++;
            }

            if (drawn == 0)
            {
                // Not a body skin (e.g. minigame-only): show one of its images instead.
                return firstAny != null ? SkinLibrary.LoadTex(File.ReadAllBytes(firstAny)) : null;
            }

            // Crop to content so small characters still fill the tile.
            int minX = W, minY = H, maxX = -1, maxY = -1;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (canvas[y * W + x].a > 0)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return null;
            minX = Mathf.Max(0, minX - 2); minY = Mathf.Max(0, minY - 2);
            maxX = Mathf.Min(W - 1, maxX + 2); maxY = Mathf.Min(H - 1, maxY + 2);
            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            var cropped = new Color32[cw * ch];
            for (int y = 0; y < ch; y++) Array.Copy(canvas, (y + minY) * W + minX, cropped, y * cw, cw);

            var result = new Texture2D(cw, ch, TextureFormat.RGBA32, false);
            result.SetPixels32(cropped);
            result.Apply(false, false);
            result.filterMode = FilterMode.Point;
            result.wrapMode = TextureWrapMode.Clamp;
            return result;
        }

        /// <summary>Draws <paramref name="tex"/> centred on (cx, cy), rotated counter-clockwise, alpha blended.</summary>
        private static void Blit(Color32[] canvas, int w, int h, Texture2D tex, float cx, float cy, float rotDeg, float shade)
        {
            var src = tex.GetPixels32();
            int tw = tex.width, th = tex.height;
            float rad = rotDeg * Mathf.Deg2Rad, cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            float half = Mathf.Sqrt(tw * tw + th * th) * 0.5f + 1f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - half)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt(cx + half));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - half)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt(cy + half));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    // inverse rotation into texture space
                    float sx = dx * cos + dy * sin + tw * 0.5f;
                    float sy = -dx * sin + dy * cos + th * 0.5f;
                    int ix = Mathf.FloorToInt(sx), iy = Mathf.FloorToInt(sy);
                    if (ix < 0 || iy < 0 || ix >= tw || iy >= th) continue;
                    Color32 s = src[iy * tw + ix];
                    if (s.a == 0) continue;
                    Color32 d = canvas[y * w + x];
                    float a = s.a / 255f;
                    canvas[y * w + x] = new Color32(
                        (byte)(s.r * shade * a + d.r * (1 - a)),
                        (byte)(s.g * shade * a + d.g * (1 - a)),
                        (byte)(s.b * shade * a + d.b * (1 - a)),
                        (byte)Mathf.Max(s.a, d.a));
                }
            }
        }
    }

    /// <summary>Who wears what: the local skin, and the skins of other players in a multiplayer session.</summary>
    internal static class SkinManager
    {
        public static SkinSet Local { get; private set; }
        public static string LocalFolder { get; private set; } = "";
        private static long localStamp;
        private static readonly Dictionary<int, SkinSet> Remote = new Dictionary<int, SkinSet>();

        /// <summary>Raised when the active local skin changes, for the settings tab.</summary>
        public static event Action LocalChanged;

        public static void RestoreLastSkin()
        {
            var info = SkinLibrary.Get(State.I.ActiveSkin);
            if (info != null) SetLocal(info, false);
        }

        public static void SetLocal(SkinInfo info, bool announce)
        {
            SkinSet loaded = null;
            if (info != null)
            {
                try { loaded = SkinLibrary.Load(info); }
                catch (Exception e) { Plugin.Log.LogError("Could not load skin '" + info.Folder + "': " + e); return; }
            }

            var previous = Local;
            Local = loaded;
            LocalFolder = info != null ? info.Folder : "";
            localStamp = info != null ? SkinLibrary.Stamp(info) : 0;
            if (State.I.ActiveSkin != LocalFolder)
            {
                State.I.ActiveSkin = LocalFolder;
                State.Save();
            }

            Applier.Dirty = true;
            Applier.Tick();                 // swap every reference off the old sprites first...
            WoundSkin.Flush();
            previous?.Destroy();            // ...then free them
            Mp.LocalSkinChanged();
            try { LocalChanged?.Invoke(); } catch (Exception e) { Plugin.Log.LogError(e); }

            if (announce)
            {
                Plugin.Log.LogInfo(info != null ? "Skin applied: " + info.DisplayName : "Skin removed (vanilla).");
                Feedback(info != null ? "Skin: " + info.DisplayName : "Skin: vanilla");
            }
        }

        public static void Toggle(SkinInfo info)
        {
            if (info == null) return;
            bool active = string.Equals(LocalFolder, info.Folder, StringComparison.OrdinalIgnoreCase);
            SetLocal(active ? null : info, true);
        }

        /// <summary>Hotkey N: apply the skin pinned to it.</summary>
        public static void SelectSlot(int slot)
        {
            var info = SkinLibrary.Get(State.I.SkinAtPin(slot));
            if (info == null)
            {
                Plugin.Verbose("Hotkey " + slot + " has no skin pinned.");
                return;
            }
            if (string.Equals(LocalFolder, info.Folder, StringComparison.OrdinalIgnoreCase)) return;
            SetLocal(info, true);
        }

        public static void Cycle(int dir)
        {
            var list = SkinLibrary.Skins;
            if (list.Count == 0) return;
            int idx = list.FindIndex(s => string.Equals(s.Folder, LocalFolder, StringComparison.OrdinalIgnoreCase));
            idx = idx < 0 ? (dir > 0 ? 0 : list.Count - 1) : ((idx + dir) % list.Count + list.Count) % list.Count;
            SetLocal(list[idx], true);
        }

        public static void ReloadIfChangedOnDisk()
        {
            if (Local == null) return;
            var info = SkinLibrary.Get(LocalFolder);
            if (info == null || !Directory.Exists(info.Path)) return;
            long stamp = SkinLibrary.Stamp(info);
            if (stamp == localStamp || stamp == 0) return;
            Plugin.Log.LogInfo("Files of skin '" + info.Folder + "' changed, reloading.");
            info.PreviewTried = false;
            if (info.Preview != null) { UnityEngine.Object.Destroy(info.Preview); info.Preview = null; }
            SetLocal(info, false);
        }

        // ---- other players

        public static void SetRemote(int playerId, SkinSet set)
        {
            Remote.TryGetValue(playerId, out var old);
            if (set == null) Remote.Remove(playerId); else Remote[playerId] = set;
            Applier.Dirty = true;
            Applier.Tick();
            WoundSkin.Flush();
            old?.Destroy();
        }

        public static void ClearRemote()
        {
            if (Remote.Count == 0) return;
            var old = Remote.Values.ToList();
            Remote.Clear();
            Applier.Dirty = true;
            Applier.Tick();
            WoundSkin.Flush();
            foreach (var s in old) s.Destroy();
        }

        /// <summary>The skin a body should be drawn with, or null for vanilla.</summary>
        public static SkinSet Resolve(Body body)
        {
            if (body == null) return null;
            if (Mp.TryGetOwner(body, out bool isLocal, out int id))
            {
                if (isLocal) return Local;
                if (!Cfg.ShowOthers.Value) return null;
                return Remote.TryGetValue(id, out var s) ? s : null;
            }
            if (Mp.SessionRunning) return null; // a body that belongs to nobody (NPC, corpse)
            return PlayerCamera.main != null && PlayerCamera.main.body == body ? Local : null;
        }

        private static void Feedback(string text)
        {
            try
            {
                if (PlayerCamera.main != null && WorldGeneration.world != null) PlayerCamera.PlayUISound("miniClick");
            }
            catch { /* no audio in this scene */ }
        }
    }
}
