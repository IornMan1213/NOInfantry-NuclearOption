using System;
using System.Collections.Generic;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace NOInfantry
{
    // ---- Messages. Mod code isn't processed by Mirage's weaver, so serializers are registered by hand. ----
    public struct NoiHello { public int Version; }
    public struct NoiHelloAck { public int Version; }
    public struct NoiSoldierState { public uint Pilot; public GlobalPosition Pos; public float Yaw; public Vector3 Vel; }
    public struct NoiShot { public uint Pilot; public GlobalPosition Origin; public Vector3 Dir; }
    public struct NoiGrenade { public uint Pilot; public GlobalPosition Origin; public Vector3 Vel; }
    public struct NoiLaunch { public uint Pilot; public uint Target; public GlobalPosition Pos; public Vector3 Dir; }
    public struct NoiVehicle { public uint Pilot; public uint Vehicle; public bool Enter; }
    public struct NoiVehicleInput { public uint Pilot; public float Throttle; public float Steering; public GlobalPosition Aim; public uint AimUnit; public bool Fire; }
    public struct NoiReturn { public uint Pilot; }
    public struct NoiFx { public byte Kind; public GlobalPosition A; public Vector3 B; }
    public struct NoiTurretAim { public uint Vehicle; public GlobalPosition Aim; public bool Active; }

    // Multiplayer for NO Infantry. Every player needs the mod; the server must have it for clients to use it.
    //
    // Handshake: a client sends one NoiHello (an unmodded server charges 60 of its 200 error tokens for an
    // unknown message, harmless once); a modded server answers NoiHelloAck and from then on treats that player
    // as modded. The server only ever sends mod messages to players that said hello.
    //
    // Authority: clients simulate their own soldier (owner-predicted movement) and stream its state; everything
    // that affects other units (rifle damage, grenades, missiles, driving vehicles/ships, returning to base)
    // runs on the server, which validates that the sender owns the soldier involved.
    internal static class NetSync
    {
        internal const int ProtocolVersion = 1;

        internal static bool ServerHasMod => Combat.IsServer || serverAcked;

        private static bool serverAcked;
        private static bool helloSent;
        private static MessageHandler serverHandler;
        private static MessageHandler clientHandler;
        private static readonly HashSet<INetworkPlayer> moddedPlayers = new HashSet<INetworkPlayer>();

        // ---- server-side state for soldiers controlled by remote players ----
        internal class RemoteSoldier
        {
            internal PilotDismounted Pilot;
            internal INetworkPlayer Owner;
            internal Vector3 Pos;
            internal float Yaw;
            internal Vector3 Vel;
            internal float LastShot;
            internal float LastGrenade;
            internal float LastLaunch;
            internal VehicleDriver Driver;
            internal bool FireHeld;
            internal Unit SpotTarget;
            internal float SpotUntil;
            internal float NextSpot;
        }

        internal static readonly Dictionary<PilotDismounted, RemoteSoldier> Remote = new Dictionary<PilotDismounted, RemoteSoldier>();

        // ---- client-side visual drivers for vehicles other players are driving ----
        private static readonly Dictionary<Unit, (VehicleDriver driver, float lastSeen)> visualDrivers = new Dictionary<Unit, (VehicleDriver, float)>();

        private static float nextStateSend;
        private static float nextInputSend;
        private static float nextAimBroadcast;

        internal static bool IsControlled(PilotDismounted p) =>
            p != null && (p == InfantryController.Controlled || Remote.ContainsKey(p));

        internal static bool InVehicle(PilotDismounted p)
        {
            if (p == null) return false;
            if (p == InfantryController.Controlled) return VehicleControl.Driven != null;
            return Remote.TryGetValue(p, out RemoteSoldier rs) && rs.Driver != null && rs.Driver.Driven != null;
        }

        internal static IEnumerable<PilotDismounted> AllControlled()
        {
            if (InfantryController.Controlled != null) yield return InfantryController.Controlled;
            foreach (PilotDismounted p in Remote.Keys) if (p != null) yield return p;
        }

        // ================= registration =================

        internal static void RegisterSerializers()
        {
            Writer<NoiHello>.Write = (w, m) => w.Write(m.Version);
            Reader<NoiHello>.Read = r => new NoiHello { Version = r.Read<int>() };
            Writer<NoiHelloAck>.Write = (w, m) => w.Write(m.Version);
            Reader<NoiHelloAck>.Read = r => new NoiHelloAck { Version = r.Read<int>() };

            Writer<NoiSoldierState>.Write = (w, m) => { w.WriteUInt32(m.Pilot); w.Write(m.Pos); w.Write(m.Yaw); w.Write(m.Vel); };
            Reader<NoiSoldierState>.Read = r => new NoiSoldierState { Pilot = r.ReadUInt32(), Pos = r.Read<GlobalPosition>(), Yaw = r.Read<float>(), Vel = r.Read<Vector3>() };

            Writer<NoiShot>.Write = (w, m) => { w.WriteUInt32(m.Pilot); w.Write(m.Origin); w.Write(m.Dir); };
            Reader<NoiShot>.Read = r => new NoiShot { Pilot = r.ReadUInt32(), Origin = r.Read<GlobalPosition>(), Dir = r.Read<Vector3>() };

            Writer<NoiGrenade>.Write = (w, m) => { w.WriteUInt32(m.Pilot); w.Write(m.Origin); w.Write(m.Vel); };
            Reader<NoiGrenade>.Read = r => new NoiGrenade { Pilot = r.ReadUInt32(), Origin = r.Read<GlobalPosition>(), Vel = r.Read<Vector3>() };

            Writer<NoiLaunch>.Write = (w, m) => { w.WriteUInt32(m.Pilot); w.WriteUInt32(m.Target); w.Write(m.Pos); w.Write(m.Dir); };
            Reader<NoiLaunch>.Read = r => new NoiLaunch { Pilot = r.ReadUInt32(), Target = r.ReadUInt32(), Pos = r.Read<GlobalPosition>(), Dir = r.Read<Vector3>() };

            Writer<NoiVehicle>.Write = (w, m) => { w.WriteUInt32(m.Pilot); w.WriteUInt32(m.Vehicle); w.WriteBoolean(m.Enter); };
            Reader<NoiVehicle>.Read = r => new NoiVehicle { Pilot = r.ReadUInt32(), Vehicle = r.ReadUInt32(), Enter = r.ReadBoolean() };

            Writer<NoiVehicleInput>.Write = (w, m) =>
            {
                w.WriteUInt32(m.Pilot); w.Write(m.Throttle); w.Write(m.Steering); w.Write(m.Aim); w.WriteUInt32(m.AimUnit); w.WriteBoolean(m.Fire);
            };
            Reader<NoiVehicleInput>.Read = r => new NoiVehicleInput
            {
                Pilot = r.ReadUInt32(), Throttle = r.Read<float>(), Steering = r.Read<float>(), Aim = r.Read<GlobalPosition>(),
                AimUnit = r.ReadUInt32(), Fire = r.ReadBoolean(),
            };

            Writer<NoiReturn>.Write = (w, m) => w.WriteUInt32(m.Pilot);
            Reader<NoiReturn>.Read = r => new NoiReturn { Pilot = r.ReadUInt32() };

            Writer<NoiFx>.Write = (w, m) => { w.WriteByte(m.Kind); w.Write(m.A); w.Write(m.B); };
            Reader<NoiFx>.Read = r => new NoiFx { Kind = r.ReadByte(), A = r.Read<GlobalPosition>(), B = r.Read<Vector3>() };

            Writer<NoiTurretAim>.Write = (w, m) => { w.WriteUInt32(m.Vehicle); w.Write(m.Aim); w.WriteBoolean(m.Active); };
            Reader<NoiTurretAim>.Read = r => new NoiTurretAim { Vehicle = r.ReadUInt32(), Aim = r.Read<GlobalPosition>(), Active = r.ReadBoolean() };

            Register<NoiHello>(); Register<NoiHelloAck>(); Register<NoiSoldierState>(); Register<NoiShot>();
            Register<NoiGrenade>(); Register<NoiLaunch>(); Register<NoiVehicle>(); Register<NoiVehicleInput>();
            Register<NoiReturn>(); Register<NoiFx>(); Register<NoiTurretAim>();
        }

        private static void Register<T>()
        {
            int id = MessagePacker.GetId<T>();
            if (MessagePacker.MessageTypes.TryGetValue(id, out Type existing) && existing != typeof(T))
            {
                Plugin.Log.LogError($"Network message id clash: {typeof(T).Name} vs {existing.FullName} ({id}); multiplayer features may misbehave.");
            }
            MessagePacker.RegisterMessage<T>();
        }

        // Called every frame: (re)attach handlers whenever the game creates a new server/client session.
        internal static void Poll()
        {
            NetworkManagerNuclearOption nm = NetworkManagerNuclearOption.i;
            if (nm == null) return;

            NetworkServer server = nm.Server;
            if (server != null && server.Active && server.MessageHandler != null && server.MessageHandler != serverHandler)
            {
                serverHandler = server.MessageHandler;
                moddedPlayers.Clear();
                Remote.Clear();
                serverHandler.RegisterHandler<NoiHello>(OnHello, false);
                serverHandler.RegisterHandler<NoiSoldierState>(OnSoldierState, false);
                serverHandler.RegisterHandler<NoiShot>(OnShot, false);
                serverHandler.RegisterHandler<NoiGrenade>(OnGrenade, false);
                serverHandler.RegisterHandler<NoiLaunch>(OnLaunch, false);
                serverHandler.RegisterHandler<NoiVehicle>(OnVehicle, false);
                serverHandler.RegisterHandler<NoiVehicleInput>(OnVehicleInput, false);
                serverHandler.RegisterHandler<NoiReturn>(OnReturn, false);
                Plugin.Log.LogInfo("Multiplayer: server handlers registered");
            }
            if (server == null || !server.Active) serverHandler = null;

            NetworkClient client = nm.Client;
            if (client != null && client.Active && client.MessageHandler != null && client.MessageHandler != clientHandler)
            {
                clientHandler = client.MessageHandler;
                serverAcked = false;
                helloSent = false;
                visualDrivers.Clear();
                clientHandler.RegisterHandler<NoiHelloAck>(OnHelloAck, false);
                clientHandler.RegisterHandler<NoiFx>(OnFx, false);
                clientHandler.RegisterHandler<NoiTurretAim>(OnTurretAim, false);
                clientHandler.RegisterHandler<NoiVehicle>(OnVehicleReject, false);
                Plugin.Log.LogInfo("Multiplayer: client handlers registered");
            }
            if (client == null || !client.Active) clientHandler = null;

            // Pure clients introduce themselves once the connection is authenticated.
            if (Combat.IsPureClient && !helloSent && client.IsConnected && client.Player != null && client.Player.IsAuthenticated)
            {
                helloSent = true;
                client.Send(new NoiHello { Version = ProtocolVersion });
                Plugin.Log.LogInfo("Multiplayer: hello sent to server");
            }

            if (Combat.IsServer) ServerTick();
            else ExpireVisualDrivers();
        }

        // ================= client -> server sends =================

        private static uint NetIdOf(Component c) => c != null && c.TryGetComponent(out NetworkIdentity id) ? id.NetId : 0u;

        private static void SendToServer<T>(T msg, Channel channel = Channel.Reliable)
        {
            NetworkClient client = NetworkManagerNuclearOption.i?.Client;
            if (client != null && client.IsConnected && serverAcked) client.Send(msg, channel);
        }

        internal static void SendState(PilotDismounted p, float yaw)
        {
            if (!Combat.IsPureClient || Time.time < nextStateSend) return;
            nextStateSend = Time.time + 0.05f;
            SendToServer(new NoiSoldierState { Pilot = NetIdOf(p), Pos = p.rb.position.ToGlobalPosition(), Yaw = yaw, Vel = p.rb.velocity }, Channel.Unreliable);
        }

        internal static void SendShot(PilotDismounted p, Vector3 origin, Vector3 dir) =>
            SendToServer(new NoiShot { Pilot = NetIdOf(p), Origin = origin.ToGlobalPosition(), Dir = dir });

        internal static void SendGrenade(PilotDismounted p, Vector3 origin, Vector3 vel) =>
            SendToServer(new NoiGrenade { Pilot = NetIdOf(p), Origin = origin.ToGlobalPosition(), Vel = vel });

        internal static void SendLaunch(PilotDismounted p, Unit target, Vector3 pos, Vector3 dir) =>
            SendToServer(new NoiLaunch { Pilot = NetIdOf(p), Target = NetIdOf(target), Pos = pos.ToGlobalPosition(), Dir = dir });

        internal static void SendVehicle(PilotDismounted p, Unit vehicle, bool enter) =>
            SendToServer(new NoiVehicle { Pilot = NetIdOf(p), Vehicle = NetIdOf(vehicle), Enter = enter });

        internal static void SendVehicleInput(PilotDismounted p, VehicleDriver d, bool fire)
        {
            if (!Combat.IsPureClient || Time.time < nextInputSend) return;
            nextInputSend = Time.time + 0.05f;
            SendToServer(new NoiVehicleInput
            {
                Pilot = NetIdOf(p), Throttle = d.Throttle, Steering = d.Steering, Aim = d.AimPoint.ToGlobalPosition(),
                AimUnit = NetIdOf(d.AimUnit), Fire = fire,
            }, Channel.Unreliable);
        }

        internal static void SendReturn(PilotDismounted p) => SendToServer(new NoiReturn { Pilot = NetIdOf(p) });

        // ================= server handlers =================

        private static void OnHello(INetworkPlayer player, NoiHello msg)
        {
            moddedPlayers.Add(player);
            player.Send(new NoiHelloAck { Version = ProtocolVersion });
            Plugin.Log.LogInfo($"Multiplayer: {player} has NO Infantry (protocol {msg.Version})");
        }

        // The soldier must exist, be alive, and belong to the sender.
        private static bool TryGetOwnedPilot(INetworkPlayer sender, uint netId, out PilotDismounted pilot)
        {
            pilot = null;
            NetworkServer server = NetworkManagerNuclearOption.i.Server;
            if (!moddedPlayers.Contains(sender) || !server.World.TryGetIdentity(netId, out NetworkIdentity identity)) return false;
            pilot = identity.GetComponent<PilotDismounted>();
            if (pilot == null || pilot.disabled || pilot.Networkplayer == null || pilot.Networkplayer.Owner != sender) return false;
            return true;
        }

        private static RemoteSoldier GetOrAddRemote(PilotDismounted pilot, INetworkPlayer owner)
        {
            if (!Remote.TryGetValue(pilot, out RemoteSoldier rs))
            {
                rs = new RemoteSoldier { Pilot = pilot, Owner = owner, Pos = pilot.rb.position, Yaw = pilot.transform.eulerAngles.y };
                Remote[pilot] = rs;
                if (pilot.NetworkisKinematic) pilot.NetworkisKinematic = false;
                pilot.SetPilotState(PilotDismounted.PilotState.landing);
                Plugin.Log.LogInfo($"Multiplayer: {owner} now controls {pilot.unitName} as infantry");
            }
            return rs;
        }

        private static void OnSoldierState(INetworkPlayer sender, NoiSoldierState msg)
        {
            if (!TryGetOwnedPilot(sender, msg.Pilot, out PilotDismounted pilot)) return;
            RemoteSoldier rs = GetOrAddRemote(pilot, sender);
            Vector3 pos = msg.Pos.ToLocalPosition();
            if (!float.IsNaN(pos.x) && !float.IsNaN(msg.Vel.x))
            {
                rs.Pos = pos;
                rs.Vel = Vector3.ClampMagnitude(msg.Vel, 200f);
                rs.Yaw = msg.Yaw;
            }
        }

        // Replaces PilotDismounted.LocalFixedUpdate on the server for a remotely controlled soldier.
        internal static void ServerStep(RemoteSoldier rs)
        {
            PilotDismounted p = rs.Pilot;
            Rigidbody rb = p.rb;
            if (rs.Driver != null && rs.Driver.Driven != null)
            {
                rs.Driver.ApplyInputs();
                if (rs.FireHeld) rs.Driver.FireHeld();
            }
            // Owner-predicted: follow the owner's reported state (snap if far, else glide with velocity).
            Vector3 err = rs.Pos - rb.position;
            if (err.sqrMagnitude > 25f * 25f)
            {
                rb.position = rs.Pos;
                p.transform.position = rs.Pos;
                rb.velocity = rs.Vel;
            }
            else
            {
                rb.velocity = rs.Vel + err * 10f;
            }
            rb.angularVelocity = Vector3.zero;
            rb.MoveRotation(Quaternion.Euler(0f, rs.Yaw, 0f));
            if (p.animationState != PilotDismounted.PilotState.landing) p.SetPilotState(PilotDismounted.PilotState.landing);
        }

        private static bool OriginPlausible(PilotDismounted pilot, Vector3 origin) =>
            Vector3.Distance(origin, pilot.transform.position) < 6f || InVehicle(pilot);

        private static void OnShot(INetworkPlayer sender, NoiShot msg)
        {
            if (!TryGetOwnedPilot(sender, msg.Pilot, out PilotDismounted pilot)) return;
            RemoteSoldier rs = GetOrAddRemote(pilot, sender);
            if (Time.time < rs.LastShot + Plugin.FireInterval.Value * 0.7f) return; // fire-rate check
            rs.LastShot = Time.time;
            Vector3 origin = msg.Origin.ToLocalPosition();
            Vector3 dir = msg.Dir.normalized;
            if (!OriginPlausible(pilot, origin) || dir == Vector3.zero) return;

            Vector3 end = origin + dir * Plugin.Range.Value;
            if (Combat.Raycast(origin, dir, pilot, out RaycastHit hit))
            {
                end = hit.point;
                Combat.ApplyRifleDamage(pilot, hit, dir);
                if (!GameManager.IsHeadless) Combat.PlayImpact(hit, dir);
            }
            if (!GameManager.IsHeadless) Tracer.Spawn(origin + dir * 1.2f, end);
            Broadcast(new NoiFx { Kind = 1, A = (origin + dir * 1.2f).ToGlobalPosition(), B = end - origin }, except: sender);
        }

        private static void OnGrenade(INetworkPlayer sender, NoiGrenade msg)
        {
            if (!TryGetOwnedPilot(sender, msg.Pilot, out PilotDismounted pilot)) return;
            RemoteSoldier rs = GetOrAddRemote(pilot, sender);
            if (Time.time < rs.LastGrenade + 0.8f) return;
            rs.LastGrenade = Time.time;
            Vector3 origin = msg.Origin.ToLocalPosition();
            if (!OriginPlausible(pilot, origin)) return;
            Vector3 vel = Vector3.ClampMagnitude(msg.Vel, Plugin.GrenadeThrowSpeed.Value + 60f);
            Grenade.Throw(pilot, origin, vel, authoritative: true);
            Broadcast(new NoiFx { Kind = 2, A = msg.Origin, B = vel }, except: sender);
        }

        private static void OnLaunch(INetworkPlayer sender, NoiLaunch msg)
        {
            if (!TryGetOwnedPilot(sender, msg.Pilot, out PilotDismounted pilot)) return;
            RemoteSoldier rs = GetOrAddRemote(pilot, sender);
            if (Time.time < rs.LastLaunch + Plugin.LauncherReload.Value * 0.7f) return;
            if (!NetworkManagerNuclearOption.i.Server.World.TryGetIdentity(msg.Target, out NetworkIdentity tid)) return;
            Unit target = tid.GetComponent<Unit>();
            if (target == null || target.disabled) return;
            Vector3 pos = msg.Pos.ToLocalPosition();
            if (!OriginPlausible(pilot, pos)) return;
            rs.LastLaunch = Time.time;
            Launcher.SpawnFor(pilot, target, pos, msg.Dir.normalized);
            rs.SpotTarget = target;
            rs.SpotUntil = Time.time + 30f;
        }

        private static void OnVehicle(INetworkPlayer sender, NoiVehicle msg)
        {
            if (!TryGetOwnedPilot(sender, msg.Pilot, out PilotDismounted pilot)) return;
            RemoteSoldier rs = GetOrAddRemote(pilot, sender);
            if (!msg.Enter)
            {
                ServerLeaveVehicle(rs);
                return;
            }
            Unit unit = null;
            if (NetworkManagerNuclearOption.i.Server.World.TryGetIdentity(msg.Vehicle, out NetworkIdentity vid)) unit = vid.GetComponent<Unit>();
            bool ok = unit != null && VehicleControl.IsDrivable(unit) && !unit.disabled && unit.NetworkHQ == pilot.NetworkHQ
                      && !VehicleControl.IsTaken(unit)
                      && Vector3.Distance(unit.transform.position, pilot.transform.position) - unit.maxRadius < Plugin.EnterDistance.Value + 4f;
            if (!ok)
            {
                sender.Send(new NoiVehicle { Pilot = msg.Pilot, Vehicle = msg.Vehicle, Enter = false }); // rejected
                Plugin.Log.LogInfo($"Multiplayer: refused {sender} entering {unit?.unitName}");
                return;
            }
            ServerLeaveVehicle(rs);
            rs.Driver = new VehicleDriver();
            rs.Driver.Enter(unit, pilot.transform.position, authority: true);
            pilot.SetCollidable(false);
        }

        private static void ServerLeaveVehicle(RemoteSoldier rs)
        {
            if (rs.Driver == null) return;
            Unit unit = rs.Driver.Driven;
            rs.Driver.Exit();
            rs.Driver = null;
            rs.FireHeld = false;
            if (rs.Pilot != null) rs.Pilot.SetCollidable(true);
            if (unit != null) Broadcast(new NoiTurretAim { Vehicle = NetIdOf(unit), Active = false }, except: null);
        }

        private static void OnVehicleInput(INetworkPlayer sender, NoiVehicleInput msg)
        {
            if (!TryGetOwnedPilot(sender, msg.Pilot, out PilotDismounted pilot)) return;
            if (!Remote.TryGetValue(pilot, out RemoteSoldier rs) || rs.Driver == null) return;
            rs.Driver.Throttle = Mathf.Clamp(msg.Throttle, -1f, 1f);
            rs.Driver.Steering = Mathf.Clamp(msg.Steering, -1f, 1f);
            rs.Driver.AimPoint = msg.Aim.ToLocalPosition();
            rs.Driver.AimValid = true;
            rs.Driver.AimUnit = msg.AimUnit != 0 && NetworkManagerNuclearOption.i.Server.World.TryGetIdentity(msg.AimUnit, out NetworkIdentity aid)
                ? aid.GetComponent<Unit>() : null;
            rs.FireHeld = msg.Fire;
        }

        private static void OnReturn(INetworkPlayer sender, NoiReturn msg)
        {
            if (!TryGetOwnedPilot(sender, msg.Pilot, out PilotDismounted pilot)) return;
            if (Remote.TryGetValue(pilot, out RemoteSoldier rs)) ServerLeaveVehicle(rs);
            Remote.Remove(pilot);
            AirbaseServices.ReturnPilot(pilot);
        }

        private static void ServerTick()
        {
            // Forget soldiers that died, were destroyed, or whose owner left.
            List<PilotDismounted> dead = null;
            foreach (KeyValuePair<PilotDismounted, RemoteSoldier> kv in Remote)
            {
                RemoteSoldier rs = kv.Value;
                if (kv.Key == null || kv.Key.disabled || rs.Owner == null || !rs.Owner.IsConnected)
                {
                    (dead ??= new List<PilotDismounted>()).Add(kv.Key);
                    continue;
                }
                // Spotting for launched optical missiles (soldier must still see the target).
                if (rs.SpotTarget != null && Time.time < rs.SpotUntil && Time.time >= rs.NextSpot && kv.Key.NetworkHQ != null)
                {
                    rs.NextSpot = Time.time + 0.5f;
                    if (!rs.SpotTarget.disabled) kv.Key.NetworkHQ.RpcUpdateTrackingInfo(rs.SpotTarget.persistentID);
                }
            }
            if (dead != null)
            {
                foreach (PilotDismounted p in dead)
                {
                    if (Remote.TryGetValue(p, out RemoteSoldier rs)) ServerLeaveVehicle(rs);
                    Remote.Remove(p);
                }
            }

            // Tell modded clients where each driven turret points so barrels match on every screen.
            if (Time.time < nextAimBroadcast || moddedPlayers.Count == 0) return;
            nextAimBroadcast = Time.time + 0.2f;
            if (VehicleControl.Local.Driven != null && VehicleControl.Local.AimValid)
            {
                Broadcast(new NoiTurretAim { Vehicle = NetIdOf(VehicleControl.Local.Driven), Aim = VehicleControl.Local.AimPoint.ToGlobalPosition(), Active = true }, except: null);
            }
            foreach (RemoteSoldier rs in Remote.Values)
            {
                if (rs.Driver?.Driven == null || !rs.Driver.AimValid) continue;
                Broadcast(new NoiTurretAim { Vehicle = NetIdOf(rs.Driver.Driven), Aim = rs.Driver.AimPoint.ToGlobalPosition(), Active = true }, except: rs.Owner);
            }
        }

        // Host's own actions: show them to modded clients too.
        internal static void BroadcastLocalShot(Vector3 from, Vector3 to)
        {
            if (Combat.IsServer && moddedPlayers.Count > 0) Broadcast(new NoiFx { Kind = 1, A = from.ToGlobalPosition(), B = to - from }, except: null);
        }

        internal static void BroadcastLocalGrenade(Vector3 origin, Vector3 vel)
        {
            if (Combat.IsServer && moddedPlayers.Count > 0) Broadcast(new NoiFx { Kind = 2, A = origin.ToGlobalPosition(), B = vel }, except: null);
        }

        private static void Broadcast<T>(T msg, INetworkPlayer except)
        {
            foreach (INetworkPlayer p in moddedPlayers)
            {
                if (p == except || !p.IsConnected) continue;
                // Don't message the host's own local connection; the host already sees everything.
                if (p.IsHost) continue;
                p.Send(msg);
            }
        }

        // ================= client handlers =================

        private static void OnHelloAck(INetworkPlayer _, NoiHelloAck msg)
        {
            serverAcked = true;
            Plugin.Log.LogInfo($"Multiplayer: server runs NO Infantry (protocol {msg.Version}); infantry enabled");
        }

        private static void OnFx(INetworkPlayer _, NoiFx msg)
        {
            Vector3 a = msg.A.ToLocalPosition();
            switch (msg.Kind)
            {
                case 1:
                    Tracer.Spawn(a, a + msg.B);
                    if (Physics.Raycast(a, msg.B.normalized, out RaycastHit hit, msg.B.magnitude + 1f, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore))
                    {
                        Combat.PlayImpact(hit, msg.B.normalized);
                    }
                    break;
                case 2:
                    Grenade.Throw(null, a, msg.B, authoritative: false);
                    break;
            }
        }

        private static void OnTurretAim(INetworkPlayer _, NoiTurretAim msg)
        {
            NetworkClient client = NetworkManagerNuclearOption.i.Client;
            if (!client.World.TryGetIdentity(msg.Vehicle, out NetworkIdentity id)) return;
            Unit unit = id.GetComponent<Unit>();
            if (unit == null || unit == VehicleControl.Local.Driven) return;
            if (!msg.Active)
            {
                if (visualDrivers.TryGetValue(unit, out var e)) { e.driver.Exit(); visualDrivers.Remove(unit); }
                return;
            }
            if (!visualDrivers.TryGetValue(unit, out var entry))
            {
                var d = new VehicleDriver();
                d.Enter(unit, unit.transform.position, authority: false);
                entry = (d, Time.time);
            }
            entry.driver.AimPoint = msg.Aim.ToLocalPosition();
            entry.driver.AimValid = true;
            entry.driver.ApplyInputs(); // keeps the (remote) turrets awake so our aim prefix runs
            visualDrivers[unit] = (entry.driver, Time.time);
        }

        private static void ExpireVisualDrivers()
        {
            if (visualDrivers.Count == 0) return;
            List<Unit> stale = null;
            foreach (var kv in visualDrivers)
            {
                if (kv.Key == null || Time.time - kv.Value.lastSeen > 2f) (stale ??= new List<Unit>()).Add(kv.Key);
                else kv.Value.driver.ApplyInputs();
            }
            if (stale == null) return;
            foreach (Unit u in stale)
            {
                visualDrivers[u].driver.Exit();
                visualDrivers.Remove(u);
            }
        }

        private static void OnVehicleReject(INetworkPlayer _, NoiVehicle msg)
        {
            Plugin.Log.LogWarning("Multiplayer: the server refused the vehicle (taken, too far, or not friendly)");
            InfantryController.Instance?.ServerRefusedVehicle();
        }
    }
}
