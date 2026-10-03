using NuclearOption.Networking;
using UnityEngine;

namespace NOInfantry
{
    // Thrown frag grenade. Damage uses the game's own blast/fragmentation model (DamageEffects.BlastFrag).
    internal class Grenade : MonoBehaviour
    {
        private static Material material;
        internal static int Detonations;

        private PersistentID thrower;
        private bool authoritative;
        private float fuse;
        private Rigidbody rb;

        // authoritative: this copy applies damage (server/host). Remote players' grenades are shown to
        // other clients with authoritative = false (visual and explosion effect only).
        internal static void Throw(PilotDismounted pilot, Vector3 origin, Vector3 velocity, bool authoritative = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "NOInfantryGrenade";
            go.transform.SetParent(Datum.origin, true);
            go.transform.position = origin;
            go.transform.localScale = Vector3.one * 0.1f;

            var renderer = go.GetComponent<Renderer>();
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null) material = new Material(shader) { color = new Color(0.2f, 0.25f, 0.15f) };
            }
            if (material != null) renderer.sharedMaterial = material;

            var col = go.GetComponent<Collider>();
            if (pilot != null)
            {
                foreach (Collider c in pilot.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(col, c);
            }

            var g = go.AddComponent<Grenade>();
            g.thrower = pilot != null ? pilot.persistentID : PersistentID.None;
            g.authoritative = authoritative && Combat.IsServer;
            g.fuse = Plugin.GrenadeFuse.Value;
            g.rb = go.AddComponent<Rigidbody>();
            g.rb.mass = 0.4f;
            g.rb.drag = 0.05f;
            g.rb.angularDrag = 0.5f;
            g.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            g.rb.velocity = velocity;
            g.rb.angularVelocity = Random.insideUnitSphere * 10f;
        }

        private void Update()
        {
            fuse -= Time.deltaTime;
            if (fuse <= 0f) Detonate();
        }

        private void Detonate()
        {
            Vector3 pos = transform.position;
            float yield = Plugin.GrenadeYield.Value;
            bool onGround = Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 1.5f, PhysicsLayers.StaticsMask);
            Vector3 normal = onGround ? hit.normal : Vector3.up;

            // Lift slightly so the frag traces don't start inside the ground.
            Vector3 blastPos = pos + normal * 0.2f;
            if (authoritative)
            {
                DamageEffects.BlastFrag(yield, blastPos, thrower, PersistentID.None);
                Explosion.SimulateForce(blastPos, yield);
            }
            BorrowedAssets.PlayBlast(pos, normal, onGround);
            Detonations++;
            Plugin.Log.LogInfo($"Grenade detonated at {pos} (yield {yield})");
            Destroy(gameObject);
        }
    }
}
