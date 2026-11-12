using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json;
using UnityEngine;

namespace Msr
{
    /// <summary>BepInEx config: key mappings and feature toggles.</summary>
    internal static class Cfg
    {
        public const int SlotCount = 10;

        public static ConfigFile File;
        public static ConfigEntry<KeyCode>[] SlotKeys;
        public static ConfigEntry<KeyCode> NextKey, PrevKey, VanillaKey;
        public static ConfigEntry<bool> Blood, WoundView, Minigame, CrossCharacter, ShowOthers, AutoReload, Verbose;
        public static ConfigEntry<string> LastUrl;

        public static void Bind(ConfigFile cfg)
        {
            File = cfg;
            SlotKeys = new ConfigEntry<KeyCode>[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                SlotKeys[i] = cfg.Bind("Keybinds", "Skin" + (i + 1), DefaultSlotKey(i),
                    "Key that applies the skin pinned to hotkey " + (i + 1) + ".");
            }
            NextKey = cfg.Bind("Keybinds", "NextSkin", KeyCode.KeypadPlus, "Apply the next skin in the list.");
            PrevKey = cfg.Bind("Keybinds", "PreviousSkin", KeyCode.KeypadMinus, "Apply the previous skin in the list.");
            VanillaKey = cfg.Bind("Keybinds", "VanillaSkin", KeyCode.KeypadDivide, "Remove the custom skin.");

            Blood = cfg.Bind("Features", "BloodColor", true, "Recolour blood (fur stains, drips, splatters) using the skin's blood colour.");
            WoundView = cfg.Bind("Features", "WoundViewSprites", true, "Replace the health panel body diagram with the skin's WoundView sprites.");
            Minigame = cfg.Bind("Features", "MinigameSprites", true, "Replace minigame hands / cursors and minigame art with the skin's Minigame sprites.");
            CrossCharacter = cfg.Bind("Features", "ApplyToAnyCharacter", true,
                "Skins are drawn with Experiment sprite names. When on, they are also mapped onto Milky and Dune bodies by body part.");
            ShowOthers = cfg.Bind("Multiplayer", "ShowOtherPlayersSkins", true, "Display skins received from other players.");
            AutoReload = cfg.Bind("General", "AutoReloadChangedFiles", true, "Reload the active skin when its files change on disk.");
            Verbose = cfg.Bind("General", "VerboseLogging", false, "Extra log output.");
            LastUrl = cfg.Bind("General", "LastDownloadUrl", "", "Last URL typed into the download box.");
        }

        public static KeyCode DefaultSlotKey(int index)
        {
            // Skin1..Skin9 -> Keypad1..Keypad9, Skin10 -> Keypad0
            return index < 9 ? KeyCode.Keypad1 + index : KeyCode.Keypad0;
        }

        public static void ResetToDefaults()
        {
            for (int i = 0; i < SlotCount; i++) SlotKeys[i].Value = (KeyCode)SlotKeys[i].DefaultValue;
            NextKey.Value = (KeyCode)NextKey.DefaultValue;
            PrevKey.Value = (KeyCode)PrevKey.DefaultValue;
            VanillaKey.Value = (KeyCode)VanillaKey.DefaultValue;
            foreach (var b in new[] { Blood, WoundView, Minigame, CrossCharacter, ShowOthers, AutoReload })
                b.Value = (bool)b.DefaultValue;
            Save();
        }

        public static void Save()
        {
            try { File.Save(); } catch (Exception e) { Plugin.Log.LogWarning("Could not save config: " + e.Message); }
        }

        /// <summary>Folded into the "what is applied" stamp so toggling a feature re-applies everything.</summary>
        public static int FlagsStamp()
        {
            return (Blood.Value ? 1 : 0) | (WoundView.Value ? 2 : 0) | (Minigame.Value ? 4 : 0) |
                   (CrossCharacter.Value ? 8 : 0) | (ShowOthers.Value ? 16 : 0);
        }
    }

    /// <summary>Small JSON file for things that are not key mappings: active skin and hotkey pins.</summary>
    internal sealed class State
    {
        public string ActiveSkin = "";
        public Dictionary<string, int> Pins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool PinsInitialised;

        public static State I = new State();

        private static string FilePath => Path.Combine(Paths.ConfigPath, "MultiplayerSpriteReplacer.state.json");

        public static void Load()
        {
            try
            {
                if (System.IO.File.Exists(FilePath))
                {
                    var loaded = JsonConvert.DeserializeObject<State>(System.IO.File.ReadAllText(FilePath));
                    if (loaded != null) I = loaded;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not read state file, starting fresh: " + e.Message);
                I = new State();
            }
            I.Pins = new Dictionary<string, int>(I.Pins ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
            I.Urls = new Dictionary<string, string>(I.Urls ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            if (I.ActiveSkin == null) I.ActiveSkin = "";
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                System.IO.File.WriteAllText(FilePath, JsonConvert.SerializeObject(I, Formatting.Indented));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not save state file: " + e.Message);
            }
        }

        /// <summary>Hotkey number (1..SlotCount) the skin is pinned to, or 0.</summary>
        public int PinOf(string folder)
        {
            return Pins.TryGetValue(folder, out var n) ? n : 0;
        }

        public string SkinAtPin(int slot)
        {
            foreach (var kv in Pins) if (kv.Value == slot) return kv.Key;
            return null;
        }

        /// <summary>Pins a skin to a hotkey; a skin that already held it loses it. slot 0 unpins.</summary>
        public void SetPin(string folder, int slot)
        {
            if (slot > 0)
            {
                var holder = SkinAtPin(slot);
                if (holder != null) Pins.Remove(holder);
            }
            if (slot <= 0) Pins.Remove(folder);
            else Pins[folder] = slot;
            Save();
        }
    }
}
