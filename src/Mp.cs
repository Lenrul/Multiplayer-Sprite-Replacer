using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using LiteNetLib;
using LiteNetLib.Utils;
using Together;
using UnityEngine;

namespace Msr
{
    /// <summary>
    /// Front door to multiplayer. Nothing in here mentions a Together type, so the mod loads and
    /// runs when Together ("Casualties: Together", plugin id CasualtiesMP) is not installed.
    /// Every call is forwarded to <see cref="MpTogether"/> only when the plugin is present.
    /// </summary>
    internal static class Mp
    {
        public const string TogetherGuid = "CasualtiesMP";

        public static bool Present { get; private set; }
        private static bool failed;

        /// <summary>True while a multiplayer session (hosting or joined) is running.</summary>
        public static bool SessionRunning => Present && !failed && Guarded(() => MpTogether.Running);

        public static void Init()
        {
            Present = Chainloader.PluginInfos.ContainsKey(TogetherGuid);
            if (!Present) return;
            try { InitImpl(); }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogError("Together is installed but its API does not match what this mod was built for. " +
                                    "Skins stay local. " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Tick()
        {
            if (!Present || failed) return;
            try { TickImpl(); } catch (Exception e) { Disable(e); }
        }

        public static void LocalSkinChanged()
        {
            if (!Present || failed) return;
            try { LocalSkinChangedImpl(); } catch (Exception e) { Disable(e); }
        }

        /// <summary>Which player a body belongs to. False for bodies that are nobody's (or outside a session).</summary>
        public static bool TryGetOwner(Body body, out bool isLocal, out int playerId)
        {
            isLocal = false; playerId = -1;
            if (!Present || failed) return false;
            try { return OwnerImpl(body, out isLocal, out playerId); }
            catch (Exception e) { Disable(e); return false; }
        }

        private static bool Guarded(Func<bool> f)
        {
            try { return f(); } catch (Exception e) { Disable(e); return false; }
        }

        private static void Disable(Exception e)
        {
            failed = true;
            Plugin.Log.LogError("Multiplayer bridge disabled after an error: " + e);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static void InitImpl() => MpTogether.Init();
        [MethodImpl(MethodImplOptions.NoInlining)] private static void TickImpl() => MpTogether.Tick();
        [MethodImpl(MethodImplOptions.NoInlining)] private static void LocalSkinChangedImpl() => MpTogether.LocalSkinChanged();
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool OwnerImpl(Body b, out bool l, out int id) => MpTogether.TryGetOwner(b, out l, out id);
    }

    /// <summary>
    /// Skin sync over Together's named custom messages.
    ///
    ///   client -> host   MSR2.Up     my skin package, in chunks (empty package = "I went vanilla")
    ///   host -> clients  MSR2.Down   a player's skin package, in chunks, tagged with the owner id
    ///   client -> host   MSR2.Req    "send me everybody's skin" (sent once after joining)
    ///
    /// The host keeps the last package of every player and relays. It never trusts an owner id
    /// from a client: uploads are filed under the connection they arrived on.
    /// </summary>
    internal static class MpTogether
    {
        private const string MsgUp = "MSR2.Up", MsgDown = "MSR2.Down", MsgReq = "MSR2.Req";
        // Steam's transport refuses messages over 512 KB and has a ~1 MB send buffer, so skins go
        // out as small chunks at a rate that stays far below it.
        private const int ChunkBytes = 16 * 1024;
        private const float ChunkInterval = 0.1f;

        private sealed class Incoming { public uint Xfer; public byte[] Data; public int Received; }
        private struct Job { public bool ToServer; public ushort Dest; public ushort Owner; public uint Xfer; public byte[] Data; public int Offset; }

        private static readonly Dictionary<int, byte[]> Packages = new Dictionary<int, byte[]>();    // host: last package per player
        private static readonly Dictionary<int, Incoming> Assembling = new Dictionary<int, Incoming>();
        private static readonly Queue<Job> Outbox = new Queue<Job>();
        private static bool wasRunning, pumping;
        private static uint nextXfer = 1;
        private static float sendLocalAt = -1f;
        private static byte[] localPackage;
        private static bool localPackageBuilt;

        public static bool Running => Net.IsRunning;

        public static void Init()
        {
            // Delegates are made from instance methods on purpose: for static methods the compiler
            // emits a cache class with fields of Together delegate types, and then merely listing
            // this assembly's types fails for everyone when Together is not installed.
            var h = new Handlers();
            Multiplayer.RegisterCustomServerReceiver(MsgUp, h.ServerUp);
            Multiplayer.RegisterCustomServerReceiver(MsgReq, h.ServerReq);
            Multiplayer.RegisterCustomClientReceiver(MsgDown, h.ClientDown);
            ScavPlayer.OnPlayerJoined += h.Joined;
            ScavPlayer.OnPlayerLeft += h.Left;
        }

        private sealed class Handlers
        {
            public void ServerUp(ScavPlayer p, NetDataReader r) => OnServerUp(p, r);
            public void ServerReq(ScavPlayer p, NetDataReader r) => OnServerReq(p, r);
            public void ClientDown(NetDataReader r) => OnClientDown(r);
            public void Joined(ScavPlayer p) => OnPlayerJoined(p);
            public void Left(ScavPlayer p) => OnPlayerLeft(p);
        }

        public static bool TryGetOwner(Body body, out bool isLocal, out int playerId)
        {
            isLocal = false; playerId = -1;
            if (!Net.IsRunning || body == null) return false;
            if (!ScavPlayer.BodyToPlayerDict.TryGetValue(body, out var plr) || plr == null) return false;
            isLocal = plr.IsLocal;
            playerId = (ushort)plr.playerId;
            return true;
        }

        public static void Tick()
        {
            bool running = Net.IsRunning;
            if (wasRunning && !running) ResetSession();
            wasRunning = running;
            if (!running) return;

            if (sendLocalAt >= 0f && Time.realtimeSinceStartup >= sendLocalAt)
            {
                sendLocalAt = -1f;
                SendLocal();
            }
        }

        private static void ResetSession()
        {
            Plugin.Verbose("Multiplayer session ended, dropping received skins.");
            Packages.Clear();
            Assembling.Clear();
            Outbox.Clear();
            sendLocalAt = -1f;
            SkinManager.ClearRemote();
        }

        public static void LocalSkinChanged()
        {
            localPackageBuilt = false;
            localPackage = null;
            if (Net.IsRunning && ScavPlayer.LocalPlayer != null)
                sendLocalAt = Time.realtimeSinceStartup + 0.25f; // collapses rapid hotkey presses into one upload
        }

        private static byte[] LocalPackage()
        {
            if (!localPackageBuilt)
            {
                localPackageBuilt = true;
                var skin = SkinManager.Local;
                localPackage = skin != null && (skin.Count(Cat.Body) > 0 || skin.Count(Cat.Wound) > 0 || skin.Blood.HasValue) ? skin.ToPackage() : null;
            }
            return localPackage;
        }

        // ------------------------------------------------------------------ events

        private static void OnPlayerJoined(ScavPlayer plr)
        {
            if (plr == null) return;
            Applier.Dirty = true;
            if (!plr.IsLocal) return;
            // We just entered a session: announce our skin, and as a client ask for everyone else's.
            sendLocalAt = Time.realtimeSinceStartup + 1.5f;
            if (Net.IsClient) Plugin.Runner.Run(RequestAllSoon());
        }

        private static IEnumerator RequestAllSoon()
        {
            yield return new WaitForSecondsRealtime(1.5f);
            if (!Net.IsRunning || !Net.IsClient) yield break;
            var w = Multiplayer.CreateNamedWriter(MsgReq);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
        }

        private static void OnPlayerLeft(ScavPlayer plr)
        {
            if (plr == null || plr.IsLocal) return;
            int id = (ushort)plr.playerId;
            Packages.Remove(id);
            Assembling.Remove(id);
            SkinManager.SetRemote(id, null);
        }

        // ------------------------------------------------------------------ sending

        private static void SendLocal()
        {
            var me = ScavPlayer.LocalPlayer;
            if (!Net.IsRunning || me == null) return;
            ushort myId = me.playerId;
            byte[] pkg = LocalPackage() ?? new byte[0];
            if (Net.IsServer)
            {
                if (pkg.Length > 0) Packages[myId] = pkg; else Packages.Remove(myId);
                Broadcast(myId, pkg, myId);
            }
            else
            {
                Outbox.Enqueue(new Job { ToServer = true, Xfer = nextXfer++, Data = pkg });
                Pump();
            }
            Plugin.Verbose("Sharing local skin (" + pkg.Length / 1024 + " KB).");
        }

        /// <summary>Host: send one player's package to every client except <paramref name="except"/> (and the host itself).</summary>
        private static void Broadcast(ushort owner, byte[] pkg, ushort except)
        {
            uint xfer = nextXfer++;
            foreach (var id in ServerMain.AllClientIdsExceptHost)
            {
                ushort dest = id;
                if (dest == except) continue;
                Outbox.Enqueue(new Job { Dest = dest, Owner = owner, Xfer = xfer, Data = pkg });
            }
            Pump();
        }

        private static void Pump()
        {
            if (pumping || Outbox.Count == 0) return;
            pumping = true;
            Plugin.Runner.Run(PumpRoutine());
        }

        private static IEnumerator PumpRoutine()
        {
            try
            {
                while (Outbox.Count > 0)
                {
                    if (!Net.IsRunning) { Outbox.Clear(); break; }
                    Job job = Outbox.Dequeue();
                    int len = Mathf.Min(ChunkBytes, job.Data.Length - job.Offset);
                    try
                    {
                        NetDataWriter w = Multiplayer.CreateNamedWriter(job.ToServer ? MsgUp : MsgDown);
                        if (!job.ToServer) w.Put(job.Owner);
                        w.Put(job.Xfer);
                        w.Put(job.Data.Length);
                        w.Put(job.Offset);
                        w.Put(len);
                        if (len > 0) w.Put(job.Data, job.Offset, len);
                        if (job.ToServer) Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
                        else Net.Server_SendTo(DeliveryMethod.ReliableOrdered, w, (knetid)job.Dest);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Sending a skin chunk failed: " + e.Message);
                        continue; // drop the rest of this transfer's current chunk; receiver discards incomplete data
                    }
                    job.Offset += len;
                    if (job.Offset < job.Data.Length) RequeueFront(job);
                    yield return new WaitForSecondsRealtime(ChunkInterval);
                }
            }
            finally
            {
                pumping = false;
            }
        }

        /// <summary>Keeps the chunks of one transfer together instead of interleaving transfers.</summary>
        private static void RequeueFront(Job job)
        {
            var rest = Outbox.ToArray();
            Outbox.Clear();
            Outbox.Enqueue(job);
            foreach (var j in rest) Outbox.Enqueue(j);
        }

        // ------------------------------------------------------------------ receiving

        /// <summary>Reads one chunk; returns the finished package once the last chunk arrived.</summary>
        private static bool ReadChunk(int key, NetDataReader r, out byte[] complete)
        {
            complete = null;
            uint xfer = r.GetUInt();
            int total = r.GetInt();
            int offset = r.GetInt();
            int len = r.GetInt();
            if (total < 0 || total > SkinSet.MaxPackageBytes || len < 0 || len > 64 * 1024 || offset < 0 || offset + len > total)
            {
                Assembling.Remove(key);
                return false;
            }
            if (total == 0)
            {
                Assembling.Remove(key);
                complete = new byte[0];
                return true;
            }
            if (!Assembling.TryGetValue(key, out var inc) || inc.Xfer != xfer || inc.Data.Length != total)
            {
                if (offset != 0) return false; // middle of a transfer we never saw the start of
                inc = new Incoming { Xfer = xfer, Data = new byte[total] };
                Assembling[key] = inc;
            }
            if (offset != inc.Received) { Assembling.Remove(key); return false; } // chunks are ordered; anything else is bogus
            if (len > 0) r.GetBytes(inc.Data, offset, len);
            inc.Received += len;
            if (inc.Received < total) return false;
            Assembling.Remove(key);
            complete = inc.Data;
            return true;
        }

        private static void OnServerUp(ScavPlayer sender, NetDataReader r)
        {
            if (sender == null || sender.IsLocal) return;
            ushort id = sender.playerId;
            if (!ReadChunk(id, r, out var pkg)) return;
            if (pkg.Length > 0) Packages[id] = pkg; else Packages.Remove(id);
            Install(id, pkg);
            Broadcast(id, pkg, id);
        }

        private static void OnServerReq(ScavPlayer sender, NetDataReader r)
        {
            if (sender == null || sender.IsLocal) return;
            ushort dest = sender.playerId;
            foreach (var kv in Packages)
            {
                if (kv.Key == dest) continue;
                Outbox.Enqueue(new Job { Dest = dest, Owner = (ushort)kv.Key, Xfer = nextXfer++, Data = kv.Value });
            }
            Pump();
        }

        private static void OnClientDown(NetDataReader r)
        {
            if (Net.IsServer) return;
            ushort owner = r.GetUShort();
            // Separate key space from the host-side table (a host never receives Down).
            if (!ReadChunk(owner, r, out var pkg)) return;
            var me = ScavPlayer.LocalPlayer;
            if (me != null && (ushort)me.playerId == owner) return;
            Install(owner, pkg);
        }

        private static void Install(int owner, byte[] pkg)
        {
            SkinSet set = null;
            if (pkg != null && pkg.Length > 0)
            {
                try { set = SkinSet.FromPackage(pkg); }
                catch (Exception e) { Plugin.Log.LogWarning("Skin from player " + owner + " could not be read: " + e.Message); }
                if (set == null) Plugin.Log.LogWarning("Ignored an invalid skin from player " + owner + ".");
            }
            SkinManager.SetRemote(owner, set);
            Plugin.Verbose(set != null ? "Received skin '" + set.Name + "' from player " + owner + "." : "Player " + owner + " now uses the vanilla skin.");
        }
    }
}
