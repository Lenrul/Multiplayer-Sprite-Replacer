using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace Msr
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(Mp.TogetherGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "lenrui.casualtiesunknown.multiplayerspritereplacer";
        public const string Name = "Multiplayer Sprite Replacer";
        public const string Version = "2.0.0";

        internal static ManualLogSource Log;
        internal static Runner Runner;

        private void Awake()
        {
            Log = Logger;
            Cfg.Bind(Config);
            State.Load();

            // The game destroys the BepInEx manager object, so everything that needs
            // Update() or coroutines lives on an object of our own.
            var go = new GameObject("MSR_Runner");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            Runner = go.AddComponent<Runner>();

            var harmony = new Harmony(Guid);
            Type[] types;
            try { types = typeof(Plugin).Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var type in types)
            {
                if (type == null) continue;
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Log.LogWarning("Patch " + type.Name + " was not applied: " + e.Message); }
            }

            SkinLibrary.Rescan();
            SkinManager.RestoreLastSkin();
            Mp.Init();

            SceneManager.sceneLoaded += (s, m) => Applier.OnSceneLoaded();
            Log.LogInfo(Name + " " + Version + " loaded. Skins folder: " + SkinLibrary.Root +
                        (Mp.Present ? " (Together detected, skins will sync)" : " (singleplayer)"));
        }

        internal static void Verbose(string msg)
        {
            if (Cfg.Verbose != null && Cfg.Verbose.Value) Log.LogInfo(msg);
        }
    }

    /// <summary>Persistent driver: hotkeys, periodic re-application, main-thread callbacks.</summary>
    internal class Runner : MonoBehaviour
    {
        private static readonly Queue<Action> MainThread = new Queue<Action>();
        private float nextTick, nextReloadCheck;

        internal static void Post(Action a)
        {
            lock (MainThread) MainThread.Enqueue(a);
        }

        internal Coroutine Run(IEnumerator routine) => StartCoroutine(routine);

        private void Update()
        {
            while (true)
            {
                Action a;
                lock (MainThread)
                {
                    if (MainThread.Count == 0) break;
                    a = MainThread.Dequeue();
                }
                try { a(); } catch (Exception e) { Plugin.Log.LogError(e); }
            }

            try
            {
                Mp.Tick();
                HandleHotkeys();

                float now = Time.realtimeSinceStartup;
                if (now >= nextTick)
                {
                    nextTick = now + 0.25f;
                    Applier.Tick();
                }
                if (Cfg.AutoReload.Value && now >= nextReloadCheck)
                {
                    nextReloadCheck = now + 2f;
                    SkinManager.ReloadIfChangedOnDisk();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }

        // Runs after every Awake/Start of the frame, so freshly spawned bodies never render a vanilla frame.
        private void LateUpdate()
        {
            if (!Applier.Dirty) return;
            try { Applier.Tick(); } catch (Exception e) { Plugin.Log.LogError(e); }
        }

        /// <summary>True while the player is typing somewhere or rebinding a key.</summary>
        internal static bool InputBlocked()
        {
            if (KeyCapture.Active) return true;
            if (GUIUtility.keyboardControl != 0) return true;
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject != null)
            {
                var sel = es.currentSelectedGameObject;
                var tmp = sel.GetComponent<TMPro.TMP_InputField>();
                if (tmp != null && tmp.isFocused) return true;
                var legacy = sel.GetComponent<UnityEngine.UI.InputField>();
                if (legacy != null && legacy.isFocused) return true;
            }
            return false;
        }

        private static void HandleHotkeys()
        {
            if (!Input.anyKeyDown || InputBlocked()) return;

            for (int i = 0; i < Cfg.SlotCount; i++)
            {
                var key = Cfg.SlotKeys[i].Value;
                if (key != KeyCode.None && Input.GetKeyDown(key))
                {
                    SkinManager.SelectSlot(i + 1);
                    return;
                }
            }
            if (Pressed(Cfg.NextKey)) SkinManager.Cycle(1);
            else if (Pressed(Cfg.PrevKey)) SkinManager.Cycle(-1);
            else if (Pressed(Cfg.VanillaKey)) SkinManager.SetLocal(null, true);
        }

        private static bool Pressed(BepInEx.Configuration.ConfigEntry<KeyCode> e)
        {
            return e.Value != KeyCode.None && Input.GetKeyDown(e.Value);
        }
    }
}
