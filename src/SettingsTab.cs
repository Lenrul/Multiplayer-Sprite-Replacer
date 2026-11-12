using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Msr
{
    /// <summary>
    /// The "Skins" tab of the game's settings window.
    /// Left: a scrollable grid of every skin folder (picture, name, "applied" checkbox, hotkey badge).
    /// Right: download-by-URL box, then the key mappings and options.
    /// </summary>
    internal static class SettingsTab
    {
        private const string TabName = "MSR_SkinsTab", PageName = "MSR_SkinsPage";
        private const int UiLayer = 5;
        private static readonly Color Yellow = new Color(1f, 0.92f, 0.1f);
        private static readonly Color RowGray = new Color(0.368f, 0.368f, 0.368f);
        private static readonly Color Dim = new Color(0.62f, 0.62f, 0.62f);

        private sealed class Tile
        {
            public SkinInfo Info;
            public Image Frame, Check;
            public TextMeshProUGUI Badge;
        }

        private static SettingsMenu menu;
        private static GameObject page, vanillaScroll;
        private static Image tabImage;
        private static RectTransform tabRect;
        private static long layoutSignature;
        private static Sprite block, blockDark;
        private static TMP_FontAsset font;
        private static Material fontMat;
        private static GameObject scrollbarTemplate;
        private static RectTransform gridContent, keyContent;
        private static TextMeshProUGUI status;
        private static TMP_InputField urlInput;
        private static readonly List<Tile> Tiles = new List<Tile>();
        private static readonly List<Action> RowRefreshers = new List<Action>();
        private static bool subscribed;

        public static bool PageOpen => page != null && page.activeSelf;

        // ------------------------------------------------------------------ tab button

        public static void Inject(SettingsMenu m)
        {
            if (m == null) return;
            // Other mods add their tabs in the same Start postfix; lay ours out after all of them.
            Plugin.Runner.Run(InjectNextFrame(m));
        }

        private static IEnumerator InjectNextFrame(SettingsMenu m)
        {
            yield return null;
            if (m == null || m.buttons == null || m.buttons.Count == 0) yield break;
            try { CreateTab(m); }
            catch (Exception e) { Plugin.Log.LogError("Could not add the Skins tab: " + e); }
        }

        private static void CreateTab(SettingsMenu m)
        {
            Button template = null;
            foreach (var b in m.buttons) if (b != null) { template = b; break; }
            if (template == null) return;
            Transform parent = template.transform.parent;
            if (parent.Find(TabName) != null) return;

            menu = m;
            block = m.buttonClosed;
            blockDark = m.buttonOpen;
            var label = template.GetComponentInChildren<TextMeshProUGUI>(true);
            font = label != null ? label.font : null;
            fontMat = label != null ? label.fontSharedMaterial : null;
            vanillaScroll = m.content != null && m.content.parent != null && m.content.parent.parent != null ? m.content.parent.parent.gameObject : null;
            var sb = vanillaScroll != null ? vanillaScroll.GetComponentInChildren<Scrollbar>(true) : null;
            scrollbarTemplate = sb != null ? sb.gameObject : null;
            page = null;

            var go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.name = TabName;
            foreach (var loc in go.GetComponentsInChildren<UILocalizer>(true)) UnityEngine.Object.DestroyImmediate(loc);
            var tip = go.GetComponent<UITooltip>();
            if (tip != null) UnityEngine.Object.DestroyImmediate(tip);
            var text = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                text.text = "Skins";
                var trt = text.rectTransform;
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(6, 4); trt.offsetMax = new Vector2(-6, -4);
            }
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent(); // also drops the cloned "SelectTab(0)" listener
            button.onClick.AddListener(ShowPage);
            tabImage = go.GetComponent<Image>();
            if (tabImage != null) tabImage.sprite = block;

            tabRect = (RectTransform)go.transform;
            LayoutTabs(m, tabRect);
            KeepBackButtonLeftmost(parent);
            go.AddComponent<TabKeeper>();

            if (!subscribed)
            {
                subscribed = true;
                SkinManager.LocalChanged += RefreshTiles;
            }
        }

        /// <summary>Spreads the existing tabs plus ours evenly over the width the tabs already occupy.</summary>
        private static void LayoutTabs(SettingsMenu m, RectTransform mine)
        {
            var tabs = new List<RectTransform>();
            foreach (var b in m.buttons)
                if (b != null && b.gameObject.activeSelf && b.transform.parent == mine.parent) tabs.Add((RectTransform)b.transform);
            if (tabs.Count == 0) return;
            tabs.Sort((a, b) => a.anchoredPosition.x.CompareTo(b.anchoredPosition.x));

            float left = float.MaxValue, right = float.MinValue;
            foreach (var t in tabs)
            {
                left = Mathf.Min(left, t.anchoredPosition.x - t.sizeDelta.x * 0.5f);
                right = Mathf.Max(right, t.anchoredPosition.x + t.sizeDelta.x * 0.5f);
            }
            float gap = tabs.Count > 1 ? Mathf.Clamp(tabs[1].anchoredPosition.x - tabs[0].anchoredPosition.x - tabs[0].sizeDelta.x, 0f, 12f) : 3f;
            float y = tabs[0].anchoredPosition.y, h = tabs[0].sizeDelta.y;

            tabs.Add(mine);
            mine.anchorMin = tabs[0].anchorMin; mine.anchorMax = tabs[0].anchorMax; mine.pivot = tabs[0].pivot;
            int n = tabs.Count;
            float w = (right - left - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                tabs[i].sizeDelta = new Vector2(w, h);
                tabs[i].anchoredPosition = new Vector2(left + w * 0.5f + i * (w + gap), y);
                // Vanilla labels are a fixed 160 wide; keep them inside narrower tabs.
                var lbl = tabs[i].GetComponentInChildren<TextMeshProUGUI>(true);
                if (lbl != null && !lbl.enableAutoSizing && lbl.preferredWidth > w - 6f)
                {
                    // e.g. "Multiplayer" once eight tabs share the strip
                    lbl.fontSizeMax = lbl.fontSize; lbl.fontSizeMin = 9f; lbl.enableAutoSizing = true;
                }
                if (lbl != null && lbl.rectTransform.sizeDelta.x > w - 8f)
                    lbl.rectTransform.sizeDelta = new Vector2(w - 8f, lbl.rectTransform.sizeDelta.y);
            }
            mine.SetAsLastSibling();
            layoutSignature = Signature(m, mine);
        }

        /// <summary>
        /// QoL Unknown finds the Back button as "the button with the smallest x in the window" and
        /// hangs its preset buttons (1 2 3) off it. With seven or more tabs the first tab's centre
        /// ends up left of the Back button's centre and the presets jump into the tab row. Measuring
        /// the Back button from its left edge instead of its centre keeps it the leftmost, without
        /// moving it on screen.
        /// </summary>
        private static void KeepBackButtonLeftmost(Transform panel)
        {
            RectTransform back = null;
            foreach (Transform child in panel)
            {
                var btn = child.GetComponent<Button>();
                var rt = child as RectTransform;
                if (btn == null || rt == null || rt.anchoredPosition.y >= 0f) continue;   // bottom row only
                bool closes = false;
                for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++)
                    if (btn.onClick.GetPersistentMethodName(i) == "Close") closes = true;
                if (closes) { back = rt; break; }
            }
            if (back == null || back.pivot.x <= 0.001f) return;
            float shift = back.sizeDelta.x * back.pivot.x;
            back.pivot = new Vector2(0f, back.pivot.y);
            back.anchoredPosition = new Vector2(back.anchoredPosition.x - shift, back.anchoredPosition.y);
        }

        /// <summary>Fingerprint of the tab strip as we left it: which tabs exist and where they sit.</summary>
        private static long Signature(SettingsMenu m, RectTransform mine)
        {
            long h = 17;
            foreach (var b in m.buttons)
            {
                if (b == null || !b.gameObject.activeSelf || b.transform.parent != mine.parent) continue;
                var rt = (RectTransform)b.transform;
                h = h * 31 + b.GetInstanceID();
                h = h * 31 + Mathf.RoundToInt(rt.anchoredPosition.x * 10f);
                h = h * 31 + Mathf.RoundToInt(rt.sizeDelta.x * 10f);
            }
            h = h * 31 + Mathf.RoundToInt(mine.anchoredPosition.x * 10f);
            return h;
        }

        /// <summary>
        /// Other mods rebuild the tab strip whenever a tab is selected (QoL Unknown destroys and
        /// re-creates its "Mods" tab and spreads the tabs it knows about over the full width, which
        /// buries ours). Whenever the strip no longer looks the way we laid it out, lay it out again.
        /// </summary>
        internal static void KeepLayout()
        {
            if (menu == null || tabRect == null || menu.buttons == null) return;
            if (Signature(menu, tabRect) == layoutSignature) return;
            try { LayoutTabs(menu, tabRect); }
            catch (Exception e) { Plugin.Log.LogError(e); layoutSignature = Signature(menu, tabRect); }
        }

        // ------------------------------------------------------------------ page switching

        private static void ShowPage()
        {
            if (menu == null) return;
            try
            {
                if (page == null) BuildPage();
                else Rebuild();
                if (vanillaScroll != null) vanillaScroll.SetActive(false);
                page.SetActive(true);
                foreach (var b in menu.buttons)
                {
                    if (b == null) continue;
                    var img = b.GetComponent<Image>();
                    if (img != null) img.sprite = menu.buttonClosed;
                }
                if (tabImage != null) tabImage.sprite = menu.buttonOpen;
                PlayerCamera.PlayUISound("miniClick");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not open the Skins tab: " + e);
            }
        }

        /// <summary>A vanilla (or another mod's) tab was selected: give the window back.</summary>
        public static void HidePage()
        {
            KeyCapture.Cancel();
            if (page != null) page.SetActive(false);
            if (vanillaScroll != null) vanillaScroll.SetActive(true);
            if (tabImage != null && menu != null) tabImage.sprite = menu.buttonClosed;
        }

        public static void ResetDefaults()
        {
            Cfg.ResetToDefaults();
            Applier.Dirty = true;
            Rebuild();
            PlayerCamera.PlayUISound("miniClick");
        }

        // ------------------------------------------------------------------ building

        private static void BuildPage()
        {
            Transform panel = vanillaScroll != null ? vanillaScroll.transform.parent : menu.transform;
            page = NewObject(PageName, panel);
            var prt = (RectTransform)page.transform;
            if (vanillaScroll != null)
            {
                var src = (RectTransform)vanillaScroll.transform;
                prt.anchorMin = src.anchorMin; prt.anchorMax = src.anchorMax; prt.pivot = src.pivot;
                prt.anchoredPosition = src.anchoredPosition; prt.sizeDelta = src.sizeDelta;
                prt.SetSiblingIndex(src.GetSiblingIndex() + 1);
                var srcImg = vanillaScroll.GetComponent<Image>();
                var bg = page.AddComponent<Image>();
                if (srcImg != null) { bg.sprite = srcImg.sprite; bg.type = srcImg.type; bg.color = srcImg.color; }
                else bg.color = new Color(0.1f, 0.1f, 0.1f);
            }
            else
            {
                prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(1075f, 687f);
                page.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f);
            }
            page.AddComponent<KeyCapture>();

            float W = prt.rect.width > 10 ? prt.rect.width : prt.sizeDelta.x;
            float H = prt.rect.height > 10 ? prt.rect.height : prt.sizeDelta.y;
            const float pad = 8f, bar = 20f;

            // ---- left: skin grid
            float gridW = Mathf.Floor(W * 0.6f);
            gridContent = MakeScroll(page.transform, pad, pad, gridW - bar - 4f, H - pad * 2, bar);

            // ---- right: download box
            float rx = pad + gridW + 10f, rw = W - rx - pad;
            float y = pad;
            var title = Label(page.transform, "Download Remote Skin:", 20, TextAlignmentOptions.Left, Color.white);
            Place(title.rectTransform, rx + 2, y, rw, 28); y += 32;

            urlInput = MakeInput(page.transform, rx, y, rw, 44, "https://skin.cat-bot.de/d/...");
            urlInput.text = Cfg.LastUrl.Value ?? "";
            urlInput.onEndEdit.AddListener(v =>
            {
                // "skin load-remote https://..." as copied from the skin site -> just the link
                string clean = Downloader.CleanUrl(v);
                if (clean != v) urlInput.SetTextWithoutNotify(clean);
                Cfg.LastUrl.Value = clean;
                Cfg.Save();
            });
            urlInput.onSubmit.AddListener(v => StartDownload());
            y += 50;

            float bw = (rw - 16f) / 3f;
            MakeButton(page.transform, "Download", rx, y, bw, 36, 16, StartDownload);
            MakeButton(page.transform, "Open Folder", rx + bw + 8, y, bw, 36, 16, () =>
            {
                try { System.IO.Directory.CreateDirectory(SkinLibrary.Root); PreRunScript.OpenInFileBrowser(SkinLibrary.Root); }
                catch (Exception e) { Plugin.Log.LogWarning("Could not open the skins folder: " + e.Message); }
            });
            MakeButton(page.transform, "Rescan", rx + (bw + 8) * 2, y, bw, 36, 16, () =>
            {
                SkinLibrary.Rescan();
                Rebuild();
                SetStatus(SkinLibrary.Skins.Count + " skin folder(s) found.");
            });
            y += 42;

            status = Label(page.transform, "", 14, TextAlignmentOptions.TopLeft, Dim);
            status.enableWordWrapping = true;
            status.overflowMode = TextOverflowModes.Ellipsis;
            Place(status.rectTransform, rx + 2, y, rw - 4, 38); y += 42;
            SetStatus("Skins live in BepInEx/plugins/CustomSprites/<name>/");

            // ---- right: key mappings and options
            keyContent = MakeScroll(page.transform, rx, y, rw - bar - 4f, H - y - pad, bar);

            Rebuild();
        }

        /// <summary>Refills both lists from the current library, pins and config.</summary>
        private static void Rebuild()
        {
            if (page == null || gridContent == null || keyContent == null) return;
            KeyCapture.Cancel();
            BuildGrid();
            BuildKeys();
        }

        private static void BuildGrid()
        {
            Clear(gridContent);
            Tiles.Clear();
            var skins = SkinLibrary.Skins;
            const int cols = 3;
            const float gap = 6f;
            float w = gridContent.rect.width > 10 ? gridContent.rect.width : ((RectTransform)gridContent.parent).rect.width;
            float tw = Mathf.Floor((w - gap * (cols - 1)) / cols), th = Mathf.Round(tw * 0.96f);

            if (skins.Count == 0)
            {
                var none = Label(gridContent, "No skins yet.\n\nPut a folder with Body/, Head/, WoundView/ or Minigame/ PNGs into\nBepInEx/plugins/CustomSprites/\nor paste a .zip link on the right.", 16, TextAlignmentOptions.Top, Dim);
                none.enableWordWrapping = true;
                Place(none.rectTransform, 10, 30, w - 20, 200);
                gridContent.sizeDelta = new Vector2(gridContent.sizeDelta.x, 240);
                return;
            }

            for (int i = 0; i < skins.Count; i++)
            {
                int col = i % cols, row = i / cols;
                Tiles.Add(MakeTile(skins[i], col * (tw + gap), row * (th + gap), tw, th));
            }
            int rows = (skins.Count + cols - 1) / cols;
            gridContent.sizeDelta = new Vector2(gridContent.sizeDelta.x, rows * (th + gap) - gap);
            RefreshTiles();
        }

        private static Tile MakeTile(SkinInfo info, float x, float y, float w, float h)
        {
            var tile = new Tile { Info = info };
            var go = NewObject("Skin_" + info.Folder, gridContent);
            Place((RectTransform)go.transform, x, y, w, h);
            tile.Frame = go.AddComponent<Image>();
            tile.Frame.sprite = block; tile.Frame.type = Image.Type.Sliced;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = tile.Frame;
            btn.onClick.AddListener(() =>
            {
                SkinManager.Toggle(info);
                PlayerCamera.PlayUISound("miniClick");
            });

            // picture
            const float inset = 7f, footer = 50f;
            float pw = w - inset * 2, ph = h - inset - footer;
            var back = NewObject("PreviewBack", go.transform);
            Place((RectTransform)back.transform, inset, inset, pw, ph);
            var backImg = back.AddComponent<Image>();
            backImg.color = new Color(0.13f, 0.13f, 0.14f);
            backImg.raycastTarget = false;

            var tex = SkinLibrary.PreviewOf(info);
            if (tex != null)
            {
                var pic = NewObject("Preview", back.transform);
                var raw = pic.AddComponent<RawImage>();
                raw.texture = tex;
                raw.raycastTarget = false;
                float scale = Mathf.Min((pw - 8f) / tex.width, (ph - 8f) / tex.height);
                if (scale >= 1f) scale = Mathf.Floor(scale); // whole-pixel scaling keeps pixel art crisp
                var rt = (RectTransform)pic.transform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(tex.width * scale, tex.height * scale);
            }

            // name
            var name = Label(go.transform, info.DisplayName, 16, TextAlignmentOptions.Left, Color.white);
            name.enableWordWrapping = true;
            name.enableAutoSizing = true; name.fontSizeMin = 9; name.fontSizeMax = 16;
            name.overflowMode = TextOverflowModes.Ellipsis;
            Place(name.rectTransform, inset + 3, h - footer + 2, w - inset * 2 - 44, footer - 8);

            // "applied" checkbox
            var box = NewObject("Applied", go.transform);
            Place((RectTransform)box.transform, w - inset - 34, h - footer + 8, 32, 32);
            var boxImg = box.AddComponent<Image>();
            boxImg.sprite = block; boxImg.type = Image.Type.Sliced; boxImg.raycastTarget = false;
            var check = NewObject("Check", box.transform);
            var crt = (RectTransform)check.transform;
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            crt.sizeDelta = new Vector2(18, 18);
            tile.Check = check.AddComponent<Image>();
            tile.Check.raycastTarget = false;

            // hotkey badge (click, then press a number to pin)
            var badge = NewObject("Hotkey", go.transform);
            Place((RectTransform)badge.transform, w - inset - 36, inset, 36, 30);
            var badgeImg = badge.AddComponent<Image>();
            badgeImg.color = new Color(0f, 0f, 0f, 0.9f);
            var badgeBtn = badge.AddComponent<Button>();
            badgeBtn.targetGraphic = badgeImg;
            tile.Badge = Label(badge.transform, "", 20, TextAlignmentOptions.Center, Yellow);
            Stretch(tile.Badge.rectTransform, 0, 0, 0, 0);
            badgeBtn.onClick.AddListener(() =>
            {
                PlayerCamera.PlayUISound("miniClick");
                tile.Badge.text = "?";
                SetStatus("Press 1-9 or 0 to pin '" + info.DisplayName + "' to that hotkey. Backspace unpins, Esc cancels.");
                KeyCapture.Begin(KeyCapture.Mode.Pin, key =>
                {
                    int slot = KeyCapture.SlotFromKey(key);
                    if (slot >= 0) State.I.SetPin(info.Folder, slot);
                    SetStatus(slot > 0 ? "'" + info.DisplayName + "' pinned to hotkey " + slot + " (" + KeyName(Cfg.SlotKeys[slot - 1].Value) + ")."
                              : slot == 0 ? "'" + info.DisplayName + "' unpinned." : "");
                    RefreshTiles();
                    RefreshRows();
                }, () => { RefreshTiles(); SetStatus(""); });
            });
            return tile;
        }

        private static void RefreshTiles()
        {
            if (page == null) return;
            foreach (var t in Tiles)
            {
                if (t.Frame == null) continue;
                bool active = string.Equals(SkinManager.LocalFolder, t.Info.Folder, StringComparison.OrdinalIgnoreCase);
                t.Check.enabled = active;
                t.Frame.color = active ? Yellow : Color.white;
                int pin = State.I.PinOf(t.Info.Folder);
                t.Badge.text = pin > 0 ? (pin % 10).ToString() : "-";
                t.Badge.color = pin > 0 ? Yellow : Dim;
            }
        }

        private static void RefreshRows()
        {
            foreach (var r in RowRefreshers) r();
        }

        private static void BuildKeys()
        {
            Clear(keyContent);
            RowRefreshers.Clear();
            float w = keyContent.rect.width > 10 ? keyContent.rect.width : ((RectTransform)keyContent.parent).rect.width;
            float y = 0f;

            for (int i = 0; i < Cfg.SlotCount; i++)
            {
                int slot = i + 1;
                var entry = Cfg.SlotKeys[i];
                TextMeshProUGUI lbl = null, sub = null;
                KeyRow(w, ref y, "Skin" + slot, entry, out lbl, out sub);
                var l = lbl; var s = sub;
                Action refresh = () =>
                {
                    if (l == null) return;
                    var pinned = SkinLibrary.Get(State.I.SkinAtPin(slot));
                    l.color = pinned != null ? Yellow : Color.white;   // yellow = a skin is pinned to this hotkey
                    s.text = pinned != null ? pinned.DisplayName : "";
                };
                refresh();
                RowRefreshers.Add(refresh);
            }
            KeyRow(w, ref y, "Next skin", Cfg.NextKey, out _, out _);
            KeyRow(w, ref y, "Previous skin", Cfg.PrevKey, out _, out _);
            KeyRow(w, ref y, "Vanilla skin", Cfg.VanillaKey, out _, out _);

            y += 6f;
            ToggleRow(w, ref y, "Custom blood colour", Cfg.Blood);
            ToggleRow(w, ref y, "Health panel sprites", Cfg.WoundView);
            ToggleRow(w, ref y, "Minigame hands & art", Cfg.Minigame);
            ToggleRow(w, ref y, "Fit skins to any character", Cfg.CrossCharacter);
            ToggleRow(w, ref y, "Show other players' skins", Cfg.ShowOthers);
            ToggleRow(w, ref y, "Reload changed files", Cfg.AutoReload);

            keyContent.sizeDelta = new Vector2(keyContent.sizeDelta.x, y);
        }

        private const float RowH = 54f, RowGap = 3f;

        private static GameObject RowBase(float w, ref float y, string name)
        {
            var row = NewObject("Row_" + name, keyContent);
            Place((RectTransform)row.transform, 0, y, w, RowH);
            var img = row.AddComponent<Image>();
            img.sprite = block; img.type = Image.Type.Sliced; img.color = RowGray;
            y += RowH + RowGap;
            return row;
        }

        private static void KeyRow(float w, ref float y, string text, BepInEx.Configuration.ConfigEntry<KeyCode> entry, out TextMeshProUGUI label, out TextMeshProUGUI sub)
        {
            var row = RowBase(w, ref y, text);
            float bw = Mathf.Min(170f, w * 0.48f);
            label = Label(row.transform, text, 20, TextAlignmentOptions.Left, Color.white);
            Place(label.rectTransform, 12, 4, w - bw - 24, 28);
            sub = Label(row.transform, "", 12, TextAlignmentOptions.Left, Dim);
            sub.overflowMode = TextOverflowModes.Ellipsis;
            sub.enableWordWrapping = false;
            Place(sub.rectTransform, 12, 31, w - bw - 24, 18);

            TextMeshProUGUI keyText = null;
            var btn = MakeButton(row.transform, KeyName(entry.Value), w - bw - 8, 7, bw, RowH - 14, 18, null);
            keyText = btn.GetComponentInChildren<TextMeshProUGUI>();
            btn.onClick.AddListener(() =>
            {
                PlayerCamera.PlayUISound("miniClick");
                keyText.text = "...";
                KeyCapture.Begin(KeyCapture.Mode.Bind, key =>
                {
                    entry.Value = key;
                    Cfg.Save();
                    keyText.text = KeyName(key);
                }, () => keyText.text = KeyName(entry.Value));
            });
        }

        private static void ToggleRow(float w, ref float y, string text, BepInEx.Configuration.ConfigEntry<bool> entry)
        {
            var row = RowBase(w, ref y, text);
            var label = Label(row.transform, text, 18, TextAlignmentOptions.Left, Color.white);
            label.enableAutoSizing = true; label.fontSizeMin = 11; label.fontSizeMax = 18;
            Place(label.rectTransform, 12, 8, w - 70, RowH - 16);

            var box = NewObject("Toggle", row.transform);
            Place((RectTransform)box.transform, w - 44, (RowH - 32) * 0.5f, 32, 32);
            var boxImg = box.AddComponent<Image>();
            boxImg.sprite = block; boxImg.type = Image.Type.Sliced;
            var check = NewObject("Check", box.transform);
            var crt = (RectTransform)check.transform;
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            crt.sizeDelta = new Vector2(18, 18);
            var checkImg = check.AddComponent<Image>();
            checkImg.raycastTarget = false;
            checkImg.enabled = entry.Value;

            var btn = row.AddComponent<Button>();
            btn.targetGraphic = boxImg;
            btn.onClick.AddListener(() =>
            {
                entry.Value = !entry.Value;
                checkImg.enabled = entry.Value;
                Cfg.Save();
                Applier.Dirty = true;
                PlayerCamera.PlayUISound("miniClick");
            });
        }

        private static void StartDownload()
        {
            if (urlInput == null) return;
            string clean = Downloader.CleanUrl(urlInput.text);
            if (clean != urlInput.text) urlInput.SetTextWithoutNotify(clean);
            Cfg.LastUrl.Value = clean;
            Cfg.Save();
            Downloader.Start(urlInput.text, (msg, done) =>
            {
                SetStatus(msg);
                if (done && page != null) Rebuild();
            });
        }

        private static void SetStatus(string text)
        {
            if (status != null) status.text = text ?? "";
        }

        internal static string KeyName(KeyCode key)
        {
            if (key == KeyCode.None)
            {
                try { return Locale.GetOther("keybindnotset"); } catch { return "Not set"; }
            }
            return key.ToString();
        }

        // ------------------------------------------------------------------ small UI kit

        private static GameObject NewObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = UiLayer;
            go.transform.SetParent(parent, false);
            return go;
        }

        /// <summary>Top-left anchored box: x to the right, y downwards.</summary>
        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static void Stretch(RectTransform rt, float l, float t, float r, float b)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
        }

        private static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        private static TextMeshProUGUI Label(Transform parent, string text, float size, TextAlignmentOptions align, Color color)
        {
            var go = NewObject("Text", parent);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            if (fontMat != null) t.fontSharedMaterial = fontMat;
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            t.richText = false;
            t.enableWordWrapping = false;
            return t;
        }

        private static Button MakeButton(Transform parent, string text, float x, float y, float w, float h, float fontSize, Action onClick)
        {
            var go = NewObject("Button_" + text, parent);
            Place((RectTransform)go.transform, x, y, w, h);
            var img = go.AddComponent<Image>();
            img.sprite = block; img.type = Image.Type.Sliced;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.698f, 0.698f, 0.698f);
            colors.pressedColor = new Color(0.425f, 0.425f, 0.425f);
            btn.colors = colors;
            var lbl = Label(go.transform, text, fontSize, TextAlignmentOptions.Center, Color.white);
            lbl.enableAutoSizing = true; lbl.fontSizeMin = 8; lbl.fontSizeMax = fontSize;
            Stretch(lbl.rectTransform, 6, 3, 6, 3);
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
        }

        /// <summary>A vertical scroll area with the game's own scrollbar art. Returns the content rect to fill.</summary>
        private static RectTransform MakeScroll(Transform parent, float x, float y, float w, float h, float barWidth)
        {
            var root = NewObject("Scroll", parent);
            Place((RectTransform)root.transform, x, y, w, h);
            var hit = root.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0); // invisible, but lets the mouse wheel work over gaps
            var scroll = root.AddComponent<ScrollRect>();

            var viewport = NewObject("Viewport", root.transform);
            Stretch((RectTransform)viewport.transform, 0, 0, 0, 0);
            viewport.AddComponent<RectMask2D>();

            var content = NewObject("Content", viewport.transform);
            var crt = (RectTransform)content.transform;
            crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0f, 1f);
            crt.anchoredPosition = Vector2.zero; crt.sizeDelta = Vector2.zero;

            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = crt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 50f;

            if (scrollbarTemplate != null)
            {
                var bar = UnityEngine.Object.Instantiate(scrollbarTemplate, parent, false);
                bar.name = "Scrollbar";
                Place((RectTransform)bar.transform, x + w + 4f, y, barWidth, h);
                scroll.verticalScrollbar = bar.GetComponent<Scrollbar>();
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }
            return crt;
        }

        private static TMP_InputField MakeInput(Transform parent, float x, float y, float w, float h, string placeholderText)
        {
            var go = NewObject("UrlInput", parent);
            go.SetActive(false); // TMP_InputField wires itself up in OnEnable; give it its parts first
            Place((RectTransform)go.transform, x, y, w, h);
            var img = go.AddComponent<Image>();
            img.sprite = block; img.type = Image.Type.Sliced;

            var area = NewObject("Text Area", go.transform);
            Stretch((RectTransform)area.transform, 10, 6, 10, 6);
            area.AddComponent<RectMask2D>();

            var placeholder = Label(area.transform, placeholderText, 16, TextAlignmentOptions.Left, new Color(0.5f, 0.5f, 0.5f));
            Stretch(placeholder.rectTransform, 0, 0, 0, 0);
            var text = Label(area.transform, "", 16, TextAlignmentOptions.Left, Color.white);
            Stretch(text.rectTransform, 0, 0, 0, 0);

            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = img;
            input.textViewport = (RectTransform)area.transform;
            input.textComponent = text;
            input.placeholder = placeholder;
            if (font != null) input.fontAsset = font;
            input.pointSize = 16;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.characterLimit = 2048;
            input.richText = false;
            input.caretColor = Color.white;
            input.customCaretColor = true;
            input.caretWidth = 2;
            input.selectionColor = new Color(1f, 0.92f, 0.1f, 0.35f);
            go.SetActive(true);
            return input;
        }
    }

    /// <summary>Sits on the Skins tab button and keeps it in the tab strip.</summary>
    internal sealed class TabKeeper : MonoBehaviour
    {
        private void LateUpdate() => SettingsTab.KeepLayout();
    }

    /// <summary>Waits for one key press while a keybind button or a hotkey badge is armed.</summary>
    internal sealed class KeyCapture : MonoBehaviour
    {
        public enum Mode { Bind, Pin }

        private static Action<KeyCode> onKey;
        private static Action onCancel;
        private static Mode mode;
        private static int armedFrame;
        private static readonly KeyCode[] AllKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        public static bool Active => onKey != null;

        public static void Begin(Mode m, Action<KeyCode> key, Action cancel)
        {
            Cancel();
            mode = m; onKey = key; onCancel = cancel;
            armedFrame = Time.frameCount;
        }

        public static void Cancel()
        {
            var c = onCancel;
            onKey = null; onCancel = null;
            try { c?.Invoke(); } catch (Exception e) { Plugin.Log.LogError(e); }
        }

        /// <summary>Pin mode: 1..9 -> 1..9, 0 -> 10, Backspace/Delete -> 0 (unpin), anything else -> -1.</summary>
        public static int SlotFromKey(KeyCode key)
        {
            if (key >= KeyCode.Alpha1 && key <= KeyCode.Alpha9) return key - KeyCode.Alpha0;
            if (key >= KeyCode.Keypad1 && key <= KeyCode.Keypad9) return key - KeyCode.Keypad0;
            if (key == KeyCode.Alpha0 || key == KeyCode.Keypad0) return 10;
            if (key == KeyCode.Backspace || key == KeyCode.Delete) return 0;
            return -1;
        }

        private void Update()
        {
            if (onKey == null || Time.frameCount <= armedFrame || !Input.anyKeyDown) return;
            foreach (var key in AllKeys)
            {
                if (!Input.GetKeyDown(key)) continue;
                var handler = onKey;
                bool mouse = key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;
                if (mode == Mode.Bind)
                {
                    if (key == KeyCode.Mouse0 || key == KeyCode.Mouse1) { Cancel(); return; }   // clicked elsewhere
                    onKey = null; onCancel = null;
                    handler(key == KeyCode.Escape ? KeyCode.None : key);                           // Esc clears the binding
                }
                else
                {
                    if (mouse || key == KeyCode.Escape || SlotFromKey(key) < 0) { Cancel(); return; }
                    onKey = null; onCancel = null;
                    handler(key);
                }
                return;
            }
        }

        private void OnDisable() => Cancel();
    }

    // ---------------------------------------------------------------------- patches

    [HarmonyPatch(typeof(SettingsMenu), "Start")]
    internal static class Patch_SettingsMenu_Start
    {
        private static void Postfix(SettingsMenu __instance)
        {
            try { SettingsTab.Inject(__instance); } catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    [HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SelectTab), new[] { typeof(Setting.SettingCategory) })]
    internal static class Patch_SettingsMenu_SelectTab
    {
        private static void Prefix()
        {
            try { SettingsTab.HidePage(); } catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    /// <summary>"Reset to default" while the Skins tab is open resets this mod's keys and options only.</summary>
    [HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.ResetToDefault))]
    internal static class Patch_SettingsMenu_ResetToDefault
    {
        private static bool Prefix()
        {
            if (!SettingsTab.PageOpen) return true;
            try { SettingsTab.ResetDefaults(); } catch (Exception e) { Plugin.Log.LogError(e); }
            return false;
        }
    }
}
