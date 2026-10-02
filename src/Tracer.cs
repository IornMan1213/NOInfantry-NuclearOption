using UnityEngine;

namespace NOInfantry
{
    // Short-lived line tracer. Placeholder until we borrow the game's own tracer/impact effects.
    internal class Tracer : MonoBehaviour
    {
        private static Material material;
        private static bool materialSearched;

        private LineRenderer line;
        private float age;

        internal static void Spawn(Vector3 from, Vector3 to)
        {
            Material mat = GetMaterial();
            if (mat == null) return;

            var go = new GameObject("NOInfantryTracer");
            go.transform.SetParent(Datum.origin, true);
            var t = go.AddComponent<Tracer>();
            t.line = go.AddComponent<LineRenderer>();
            t.line.material = mat;
            t.line.useWorldSpace = true;
            t.line.positionCount = 2;
            t.line.SetPosition(0, from);
            t.line.SetPosition(1, to);
            t.line.startWidth = 0.03f;
            t.line.endWidth = 0.02f;
            t.line.startColor = new Color(1f, 0.85f, 0.4f, 0.9f);
            t.line.endColor = new Color(1f, 0.7f, 0.2f, 0.4f);
            t.line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static Material GetMaterial()
        {
            if (materialSearched) return material;
            materialSearched = true;
            foreach (string name in new[] { "Universal Render Pipeline/Particles/Unlit", "Universal Render Pipeline/Unlit", "Sprites/Default", "Unlit/Color" })
            {
                Shader shader = Shader.Find(name);
                if (shader == null) continue;
                material = new Material(shader);
                Plugin.Log.LogInfo($"Tracer shader: {name}");
                return material;
            }
            Plugin.Log.LogWarning("No unlit shader found; tracers disabled.");
            return null;
        }

        private void Update()
        {
            age += Time.deltaTime;
            if (age > 0.06f) Destroy(gameObject);
        }
    }
}
