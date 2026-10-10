using System.Collections.Generic;
using System.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace NOInfantry
{
    // AI riflemen built on the game's own PilotDismounted unit (networked, visible to every player).
    // Everything runs on the server/host: they replace PilotDismounted.LocalFixedUpdate (via the same patch as the
    // player's soldier), move with SoldierMotor, and shoot through the same server-side rifle damage as players.
    internal enum AiOrder : byte { Follow, Hold, Move, Free }

    internal class AiSoldier
    {
        internal AiOrder Order;               // fireteams: what their leader told them (patrols ignore it)
        // Stored as global positions: the game shifts its floating origin, which would move a stored local Vector3.
        private GlobalPosition orderPos, patrolGoal;
        internal Vector3 OrderPos { get => orderPos.ToLocalPosition(); set => orderPos = value.ToGlobalPosition(); } // Hold/Move: formation centre
        internal float OrderYaw;              // Hold/Move: formation facing
        internal Airbase Objective;           // Free: airbase to take
        internal Vector3 FreeOffset;          // Free: spread around the objective
        internal PilotDismounted Pilot;
        internal PilotDismounted Leader;      // friendly fireteam: follow this soldier
        internal Vector3 FormationOffset;     // in the leader's yaw frame
        internal Vector3 PatrolGoal { get => patrolGoal.ToLocalPosition(); set => patrolGoal = value.ToGlobalPosition(); } // patrols: where to advance
        internal bool IsPatrol;
        internal Unit Target;
        internal float Yaw;
        internal Vector3 Wish;
        internal float NextThink;
        internal float NextShot;
        internal int BurstLeft;
        internal float StuckTime;
        internal int BlockedShots;   // friendly in the line of fire
        internal Vector3 LastPos;
        internal PilotDismounted ReviveTarget; // going to (or treating) a downed friend
        internal GroundVehicle Riding;         // aboard a transport (Transport.cs)
        internal int Seat;
        internal float ReviveDoneAt;
    }

    internal static class AiSquads
    {
        internal static readonly Dictionary<PilotDismounted, AiSoldier> All = new Dictionary<PilotDismounted, AiSoldier>();
        internal static int Spawned, ShotsFired, HitsLanded, OrdersReceived;

        private static int nameCounter;
        private static AudioSource shotSource;

        internal static bool IsAi(PilotDismounted p) => p != null && All.ContainsKey(p);

        internal static int AliveFollowing(PilotDismounted leader) =>
            leader == null ? 0 : All.Values.Count(a => (object)a.Leader == leader && a.Pilot != null && !a.Pilot.disabled); // reference compare: a destroyed leader "== null" would match patrols

        // ---------------- spawning (server only) ----------------

        internal static void SpawnFireteam(PilotDismounted leader, int count)
        {
            if (!Combat.IsServer || leader == null || leader.NetworkHQ == null) return;
            int existing = AliveFollowing(leader);
            Quaternion yaw = Quaternion.Euler(0f, leader.transform.eulerAngles.y, 0f);
            for (int i = 0; i < count - existing; i++)
            {
                // Wedge behind the leader: alternating left/right, each row 4 m further back.
                int row = i / 2 + 1;
                float side = (i % 2 == 0 ? -1f : 1f) * (2.5f + row);
                Vector3 offset = new Vector3(side, 0f, -4f * row);
                Vector3 pos = GroundPoint(leader.transform.position + yaw * offset + yaw * Vector3.back * 6f);
                AiSoldier ai = Spawn(leader.NetworkHQ, pos, leader.transform.eulerAngles.y, "Rifleman");
                if (ai == null) continue;
                ai.Leader = leader;
                ai.FormationOffset = offset;
            }
            AiSoldier first = All.Values.FirstOrDefault(a => (object)a.Leader == leader);
            Plugin.Log.LogInfo($"Fireteam for {leader.unitName}: {AliveFollowing(leader)} riflemen; leader global {leader.transform.position.ToGlobalPosition()} rb {leader.rb.position.ToGlobalPosition()}, first rifleman global {first?.Pilot.transform.position.ToGlobalPosition()}");
        }

        internal static List<AiSoldier> SpawnPatrol(FactionHQ hq, Vector3 center, Vector3 advanceTo, int count)
        {
            var squad = new List<AiSoldier>();
            if (!Combat.IsServer || hq == null) return squad;
            float yaw = Quaternion.LookRotation(Flat(advanceTo - center)).eulerAngles.y;
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = GroundPoint(center + new Vector3((i % 3 - 1) * 4f, 0f, (i / 3) * -4f));
                AiSoldier ai = Spawn(hq, pos, yaw, "Patrol");
                if (ai == null) continue;
                ai.IsPatrol = true;
                ai.PatrolGoal = advanceTo;
                ai.FreeOffset = new Vector3(Random.Range(-25f, 25f), 0f, Random.Range(-25f, 25f));
                squad.Add(ai);
            }
            Plugin.Log.LogInfo($"Patrol of {count} spawned {Vector3.Distance(center, advanceTo):F0} m out");
            return squad;
        }

        // Server: an order from a player to every AI rifleman following them.
        internal static int OrderSquad(PilotDismounted leader, AiOrder order, Vector3 pos, bool disband)
        {
            if (!Combat.IsServer || leader == null) return 0;
            int n = 0;
            float yaw = leader.transform.eulerAngles.y;
            foreach (AiSoldier ai in All.Values)
            {
                if ((object)ai.Leader != leader || ai.Pilot == null || ai.Pilot.disabled) continue;
                n++;
                ai.Order = order;
                ai.OrderYaw = yaw;
                ai.OrderPos = order == AiOrder.Hold ? leader.transform.position : pos;
                ai.Objective = null;
                ai.FreeOffset = new Vector3(Random.Range(-25f, 25f), 0f, Random.Range(-25f, 25f));
                if (disband)
                {
                    ai.Leader = null;
                    ai.Order = AiOrder.Free;
                }
                ai.NextThink = Time.time; // react now
            }
            OrdersReceived++;
            Plugin.Log.LogInfo($"Squad order from {leader.unitName}: {(disband ? "disband" : order.ToString())} ({n} riflemen)");
            return n;
        }

        private static AiSoldier Spawn(FactionHQ hq, Vector3 pos, float yaw, string role)
        {
            Spawner spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null) return null;
            string name = $"NOI {role} {++nameCounter}";
            PilotDismounted p = spawner.SpawnPilot(GameAssets.i.pilotDismounted, (pos + Vector3.up * SoldierMotor.HoverHeight).ToGlobalPosition(),
                Quaternion.Euler(0f, yaw, 0f), hq, name);
            GroundWar.PlaceBody(p.gameObject, p.transform.position, p.transform.rotation); // see PlaceBody: else it starts at the local origin
            var ai = new AiSoldier { Pilot = p, Yaw = yaw, LastPos = p.transform.position, NextThink = Time.time + Random.value * 0.4f };
            All[p] = ai; // registered before its first physics step, so the game's landed-pilot logic never runs on it
            Spawned++;
            return ai;
        }

        // ---------------- per-step behaviour ----------------

        // Called from the LocalFixedUpdate prefix (server) instead of the game's landed-pilot logic.
        internal static void Step(AiSoldier ai)
        {
            PilotDismounted p = ai.Pilot;
            if (ai.Riding != null)
            {
                // Aboard: inside the truck, collider off and hidden. The collider is forced off every step: the game's
                // pilot Setup re-enables it after spawn, and riders' colliders inside the hull blew the truck up.
                GroundVehicle t = ai.Riding;
                if (t == null || t.disabled) return; // Transport.Tick dismounts the survivors
                p.SetCollidable(false);
                p.rb.isKinematic = true;
                Transport.SetVisible(p, false);
                Vector3 seat = Transport.Seat(t, ai.Seat);
                p.transform.position = seat;
                p.rb.position = seat;
                p.rb.MoveRotation(Quaternion.Euler(0f, t.transform.eulerAngles.y + (ai.Seat % 2 == 0 ? -90f : 90f), 0f));
                return;
            }
            if (Medical.IsDowned(p))
            {
                ai.Target = null;
                ai.ReviveTarget = null;
                SoldierMotor.Step(p, ai.Yaw, Vector3.zero, jump: false, downed: true);
                return;
            }
            if (Time.time >= ai.NextThink) Think(ai);
            SoldierMotor.Step(p, ai.Yaw, ai.Wish, jump: false);
            if (ai.Target != null && Time.time >= ai.NextShot && ai.ReviveDoneAt == 0f) Shoot(ai);
        }

        private static void Think(AiSoldier ai)
        {
            ai.NextThink = Time.time + 0.4f;
            PilotDismounted p = ai.Pilot;
            Vector3 pos = p.transform.position;

            // Stuck detection: if we meant to move but didn't, sidestep for a moment.
            bool wantedMove = ai.Wish.sqrMagnitude > 0.5f;
            ai.StuckTime = wantedMove && Vector3.Distance(Flat(pos), Flat(ai.LastPos)) < 0.3f ? ai.StuckTime + 0.4f : 0f;
            ai.LastPos = pos;

            Unit previous = ai.Target;
            ai.Target = FindTarget(p, Plugin.AiSoldierRange.Value);
            if (ai.Target != null && ai.Target != previous)
            {
                // Reaction time before the first shot at a newly spotted target.
                ai.NextShot = Mathf.Max(ai.NextShot, Time.time + Random.Range(0.6f, 1.4f));
                ai.BurstLeft = 0;
            }

            if (ThinkRevive(ai, pos)) return;

            // Leader killed or gone: the riflemen carry on by themselves.
            if ((object)ai.Leader != null && (ai.Leader == null || ai.Leader.disabled))
            {
                ai.Leader = null;
                if (ai.Order == AiOrder.Follow) ai.Order = AiOrder.Free;
            }

            Vector3 goal;
            float range = float.MaxValue;
            bool hasLeader = ai.Leader != null && !ai.Leader.disabled;
            bool formation = false; // keep tight formation spacing (stop at 2.5 m)
            if (hasLeader && ai.Order == AiOrder.Follow)
            {
                Quaternion leaderYaw = Quaternion.Euler(0f, ai.Leader.transform.eulerAngles.y, 0f);
                goal = ai.Leader.transform.position + leaderYaw * ai.FormationOffset;
                formation = true;
            }
            else if (ai.Order == AiOrder.Hold || ai.Order == AiOrder.Move)
            {
                goal = ai.OrderPos + Quaternion.Euler(0f, ai.OrderYaw, 0f) * ai.FormationOffset;
                formation = true;
            }
            else if (ai.Order == AiOrder.Free && !ai.IsPatrol)
            {
                // Free roam: hunt soldiers we know about nearby, otherwise take the next airbase.
                PilotDismounted prey = NearestEnemySoldier(p, 600f);
                if (prey != null)
                {
                    goal = prey.transform.position;
                    range = ai.Target != null ? 80f : 25f;
                }
                else
                {
                    // Move on once the objective is fully ours (or gone).
                    bool secured = ai.Objective != null && ai.Objective.CurrentHQ == p.NetworkHQ
                                   && (ai.Objective.capture == null || ai.Objective.capture.controlBalance >= 0.99f);
                    if (ai.Objective == null || ai.Objective.disabled || secured) ai.Objective = GroundWar.NextObjective(p.NetworkHQ, pos);
                    goal = ai.Objective != null ? ai.Objective.center.position + ai.FreeOffset : pos;
                    range = ai.Objective != null ? 8f : 0f;
                }
            }
            else if (ai.IsPatrol)
            {
                // Advance on the nearest enemy soldier we know of, else the original objective.
                PilotDismounted prey = NearestEnemySoldier(p, 3000f);
                if (prey != null) ai.PatrolGoal = prey.transform.position;
                goal = ai.PatrolGoal;
                range = ai.Target != null ? 90f : 25f; // hold at fighting distance once engaged
            }
            else
            {
                goal = pos; // leaderless fireteam: hold position
            }

            Vector3 toGoal = Flat(goal - pos);
            float dist = toGoal.magnitude;
            float stopAt = formation ? 2.5f : range;
            bool engaging = ai.Target != null;
            float speed = 0f;
            if (dist > stopAt)
            {
                // Riflemen keep up with their leader even while shooting; patrols stop to fire once in range.
                bool farBehind = formation && dist > 15f || !formation && !engaging && dist > 200f;
                speed = farBehind ? Plugin.SprintSpeed.Value : Plugin.WalkSpeed.Value * (engaging ? 0.6f : 1f);
                if (!formation && !ai.IsPatrol && engaging && dist < range * 1.6f) speed = 0f; // free roamers stop to fight
                if (ai.IsPatrol && engaging) speed = dist > range * 1.6f ? Plugin.WalkSpeed.Value : 0f;
            }
            Vector3 moveDir = dist > 0.01f ? toGoal / dist : Vector3.zero;
            if (ai.StuckTime > 1.2f) moveDir = Quaternion.Euler(0f, 70f, 0f) * moveDir; // sidestep obstacles
            if (ai.Target != null && ai.BlockedShots >= 3)
            {
                // A friend keeps blocking our shot: step sideways to open a firing lane.
                Vector3 toTarget = Flat(ai.Target.transform.position - pos).normalized;
                moveDir = Vector3.Cross(Vector3.up, toTarget) * (ai.Pilot.GetInstanceID() % 2 == 0 ? 1f : -1f);
                speed = Plugin.WalkSpeed.Value;
            }
            ai.Wish = moveDir * speed;

            if (engaging)
            {
                ai.Yaw = Quaternion.LookRotation(Flat(ai.Target.transform.position - pos)).eulerAngles.y;
            }
            else if (speed > 0.1f)
            {
                ai.Yaw = Quaternion.LookRotation(moveDir).eulerAngles.y;
            }
            else if (formation && ai.Order != AiOrder.Follow)
            {
                ai.Yaw = ai.OrderYaw;
            }
            else if (hasLeader)
            {
                ai.Yaw = ai.Leader.transform.eulerAngles.y;
            }
        }

        // Go to a downed friend nobody else is helping (the player first), kneel for ReviveTime, revive. Returns true
        // while busy with it (movement already set).
        private static bool ThinkRevive(AiSoldier ai, Vector3 pos)
        {
            PilotDismounted p = ai.Pilot;
            if (!Plugin.ReviveEnabled.Value) return false;
            PilotDismounted t = ai.ReviveTarget;
            if (t == null || t.disabled || !Medical.IsDowned(t))
            {
                ai.ReviveTarget = null;
                ai.ReviveDoneAt = 0f;
                // Not while trading fire at close range; a downed friend is otherwise the priority.
                if (ai.Target != null && Vector3.Distance(ai.Target.transform.position, pos) < 120f) return false;
                t = Medical.DownedNear(p, 80f)
                    .Where(d => !All.Values.Any(o => o != ai && (object)o.ReviveTarget == d))
                    .OrderBy(d => NetSync.IsControlled(d) ? 0 : 1).ThenBy(d => Vector3.Distance(d.transform.position, pos))
                    .FirstOrDefault();
                if (t == null) return false;
                ai.ReviveTarget = t;
                Plugin.Log.LogInfo($"{p.unitName} going to revive {t.unitName}");
            }
            Vector3 to = Flat(t.transform.position - pos);
            if (to.magnitude > 1.8f)
            {
                ai.ReviveDoneAt = 0f;
                ai.Wish = to.normalized * (to.magnitude > 8f ? Plugin.SprintSpeed.Value : Plugin.WalkSpeed.Value);
                ai.Yaw = Quaternion.LookRotation(to).eulerAngles.y;
                return true;
            }
            ai.Wish = Vector3.zero;
            if (ai.ReviveDoneAt == 0f) ai.ReviveDoneAt = Time.time + Plugin.ReviveTime.Value;
            else if (Time.time >= ai.ReviveDoneAt)
            {
                Medical.Revive(p, t, medic: false);
                ai.ReviveTarget = null;
                ai.ReviveDoneAt = 0f;
            }
            return true;
        }

        private static void Shoot(AiSoldier ai)
        {
            PilotDismounted p = ai.Pilot;
            Unit t = ai.Target;
            if (t == null || t.disabled) { ai.Target = null; return; }
            Vector3 eye = p.transform.position + Vector3.up * Plugin.EyeHeight.Value;
            Vector3 aimPoint = t.transform.position + (t is PilotDismounted ? Vector3.up * 0.3f : Vector3.up * 1f);
            float dist = Vector3.Distance(eye, aimPoint);
            float spread = Plugin.AiSpreadDegrees.Value * (1f + dist / 300f);
            if (t.rb != null) spread *= 1f + Mathf.Clamp(t.rb.velocity.magnitude / 6f, 0f, 1.5f); // moving targets are harder
            if (t == InfantryController.Controlled)
            {
                InfantryController.Stance st = InfantryController.Instance.CurrentStance;
                spread *= st == InfantryController.Stance.Prone ? 1.8f : st == InfantryController.Stance.Crouch ? 1.3f : 1f;
            }
            // Triangular distribution: most rounds near the aim point, a few at the edge of the cone.
            float Jitter() => (Random.value - Random.value) * spread;
            Vector3 dir = Quaternion.Euler(Jitter(), Jitter(), 0f) * (aimPoint - eye).normalized;

            // Check fire: never shoot through a friendly. Test the clean aim line (no spread) first.
            if (FriendlyInLine(p, eye, aimPoint))
            {
                ai.BlockedShots++;
                ai.NextShot = Time.time + 0.4f;
                return;
            }
            ai.BlockedShots = 0;

            Vector3 end = eye + dir * Plugin.Range.Value;
            if (Combat.Raycast(eye, dir, p, out RaycastHit hit))
            {
                end = hit.point;
                Unit hitUnit = hit.collider.GetComponentInParent<IDamageable>()?.GetUnit();
                if (hitUnit != null && hitUnit.NetworkHQ == p.NetworkHQ)
                {
                    // Spread would hit a friend: skip this round instead.
                    ai.NextShot = Time.time + 0.15f;
                    return;
                }
                if (Combat.ApplyRifleDamage(p, hit, dir) != null) HitsLanded++;
                if (!GameManager.IsHeadless) Combat.PlayImpact(hit, dir);
            }
            ShotsFired++;
            Vector3 muzzle = eye + dir * 0.8f + Vector3.down * 0.15f;
            if (!GameManager.IsHeadless)
            {
                Tracer.Spawn(muzzle, end);
                PlayShot(muzzle);
            }
            NetSync.BroadcastLocalShot(muzzle, end);

            // Bursts of 3-5, then a pause.
            if (ai.BurstLeft <= 0) ai.BurstLeft = Random.Range(3, 6);
            ai.BurstLeft--;
            ai.NextShot = Time.time + (ai.BurstLeft > 0 ? 0.13f : Random.Range(0.7f, 1.5f));
        }

        private static void PlayShot(Vector3 at)
        {
            AudioClip[] clips = BorrowedAssets.RifleShots;
            if (clips == null || clips.Length == 0) return;
            if (shotSource == null)
            {
                var go = new GameObject("NOInfantryAiShot");
                Object.DontDestroyOnLoad(go);
                shotSource = go.AddComponent<AudioSource>();
                shotSource.spatialBlend = 1f;
                shotSource.minDistance = 8f;
                shotSource.maxDistance = 600f;
                shotSource.rolloffMode = AudioRolloffMode.Logarithmic;
                if (SoundManager.i != null) shotSource.outputAudioMixerGroup = SoundManager.i.EffectsMixer;
            }
            shotSource.transform.position = at;
            shotSource.pitch = Random.Range(1.1f, 1.3f);
            shotSource.PlayOneShot(clips[Random.Range(0, clips.Length)], Plugin.RifleVolume.Value * 0.8f);
        }

        // ---------------- targeting ----------------

        private static Unit FindTarget(PilotDismounted self, float range)
        {
            Vector3 eye = self.transform.position + Vector3.up * Plugin.EyeHeight.Value;
            Unit best = null;
            float bestScore = float.MaxValue;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled || u == self || u.NetworkHQ == null || u.NetworkHQ == self.NetworkHQ) continue;
                bool soldier = u is PilotDismounted pd && NetSync.IsProtectedOrAi(pd) && !NetSync.InVehicle(pd) && !Medical.IsDowned(pd);
                bool softVehicle = u is GroundVehicle && u.definition != null && u.definition.armorTier <= 0;
                if (!soldier && !softVehicle) continue;
                float d = Vector3.Distance(eye, u.transform.position);
                if (d > (soldier ? range : range * 0.6f)) continue;
                float score = d * (soldier ? 1f : 2f); // prefer soldiers
                if (score >= bestScore) continue;
                if (!LineOfSight(eye, u, self)) continue;
                bestScore = score;
                best = u;
            }
            return best;
        }

        private static PilotDismounted NearestEnemySoldier(PilotDismounted self, float range)
        {
            PilotDismounted best = null;
            float bestD = range;
            foreach (PilotDismounted p in NetSync.AllControlled().Concat(All.Keys))
            {
                if (p == null || p.disabled || p.NetworkHQ == null || p.NetworkHQ == self.NetworkHQ) continue;
                float d = Vector3.Distance(p.transform.position, self.transform.position);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        // True if a unit of our own side is between us and the aim point (within a 0.6 m wide lane).
        private static bool FriendlyInLine(PilotDismounted self, Vector3 eye, Vector3 aimPoint)
        {
            Vector3 to = aimPoint - eye;
            float len = to.magnitude;
            foreach (RaycastHit h in Physics.SphereCastAll(eye, 0.3f, to / len, len, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(self.transform)) continue;
                Unit u = h.collider.GetComponentInParent<IDamageable>()?.GetUnit();
                if (u != null && u != self && u.NetworkHQ == self.NetworkHQ && !u.disabled) return true;
            }
            return false;
        }

        private static bool LineOfSight(Vector3 eye, Unit target, PilotDismounted self)
        {
            Vector3 to = target.transform.position + Vector3.up * 0.3f - eye;
            foreach (RaycastHit h in Physics.RaycastAll(eye, to.normalized, to.magnitude, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore))
            {
                Transform t = h.collider.transform;
                if (t.IsChildOf(self.transform) || t.IsChildOf(target.transform)) continue;
                return false;
            }
            return true;
        }

        // ---------------- housekeeping ----------------

        // Forget dead or despawned soldiers (the game shows the body and despawns it after 60 s).
        internal static void Tick()
        {
            if (All.Count == 0) return;
            List<PilotDismounted> gone = null;
            foreach (KeyValuePair<PilotDismounted, AiSoldier> kv in All)
            {
                if (kv.Key == null || kv.Key.disabled) (gone ??= new List<PilotDismounted>()).Add(kv.Key);
            }
            if (gone != null)
            {
                foreach (PilotDismounted p in gone)
                {
                    if (p != null) Plugin.Log.LogInfo($"AI soldier down: {p.UniqueName}");
                    All.Remove(p);
                }
            }
        }

        // Server: remove AI soldiers (test harness / mission reset).
        internal static int Despawn(System.Func<AiSoldier, bool> which)
        {
            var gone = All.Values.Where(which).ToList();
            foreach (AiSoldier ai in gone)
            {
                All.Remove(ai.Pilot);
                if (ai.Pilot == null) continue;
                ai.Pilot.NetworkunitState = Unit.UnitState.Returned;
                ai.Pilot.Networkdisabled = true;
                Object.Destroy(ai.Pilot.gameObject);
            }
            return gone.Count;
        }

        internal static Vector3 GroundPoint(Vector3 p)
        {
            // Terrain and buildings only: vehicles, aircraft (even the selection menu's preview) and stray colliders are not ground.
            if (Physics.Raycast(p + Vector3.up * 300f, Vector3.down, out RaycastHit hit, 800f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
            {
                return hit.point;
            }
            return p;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
