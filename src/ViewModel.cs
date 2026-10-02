using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace NOInfantry
{
    // First-person rifle, parented to the main camera. Uses an OBJ model from <plugin>/models when available
    // (CC0 Quaternius "Ultimate Gun Pack"), else a primitive placeholder.
    // Frame: +z forward along the bore, bore at y = 0. The rifle's local origin is the eye point when aiming,
    // so the aim pose is simply (0, -sightHeight, 0) and the front sight tip lands exactly on screen centre.
    internal class ViewModel : MonoBehaviour
    {
        private static readonly Vector3 HipOffset = new Vector3(0.13f, -0.13f, 0.10f);   // added to the aim pose
        private static readonly Vector3 SprintOffset = new Vector3(0.08f, -0.18f, 0.04f);
        private static readonly Vector3 SprintRot = new Vector3(15f, -35f, 10f);

        internal Transform Muzzle { get; private set; }

        private float sightHeight = 0.055f;
        private Vector3 AimPos => new Vector3(0f, -sightHeight, 0f);

        private Transform model;
        private GameObject flash;
        private Light flashLight;
        private float flashUntil;

        private float adsBlend;
        private float sprintBlend;
        private float reloadBlend;
        private float bobPhase;
        private Vector2 sway;
        private Vector3 recoilPos;
        private Vector3 recoilRot;

        internal static ViewModel Create(Camera cam, Shader fallbackShader)
        {
            var root = new GameObject("NOInfantryViewModel");
            root.transform.SetParent(cam.transform, false);
            var vm = root.AddComponent<ViewModel>();
            vm.Build(fallbackShader);
            return vm;
        }

        private void Build(Shader fallbackShader)
        {
            model = new GameObject("Rifle").transform;
            model.SetParent(transform, false);

            Shader lit = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? fallbackShader;

            float muzzleZ;
            if (!TryBuildFromModel(lit, out muzzleZ))
            {
                muzzleZ = BuildPrimitive(lit);
            }

            Muzzle = new GameObject("Muzzle").transform;
            Muzzle.SetParent(model, false);
            Muzzle.localPosition = new Vector3(0f, 0f, muzzleZ);

            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? fallbackShader;
            Material flashMat = MakeMat(unlit, new Color(1f, 0.75f, 0.3f), 0f);
            flash = Part("MuzzleFlash", PrimitiveType.Sphere, Muzzle.localPosition + Vector3.forward * 0.03f, new Vector3(0.05f, 0.05f, 0.09f), flashMat).gameObject;
            flash.SetActive(false);
            flashLight = Muzzle.gameObject.AddComponent<Light>();
            flashLight.type = LightType.Point;
            flashLight.color = new Color(1f, 0.7f, 0.35f);
            flashLight.range = 6f;
            flashLight.intensity = 3f;
            flashLight.shadows = LightShadows.None;
            flashLight.enabled = false;

            transform.localPosition = AimPos + HipOffset;
        }

        private bool TryBuildFromModel(Shader lit, out float muzzleZ)
        {
            muzzleZ = 0f;
            string path = Path.Combine(Plugin.PluginDir, "models", Plugin.RifleModel.Value);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"Rifle model not found at {path}; using placeholder.");
                return false;
            }
            ObjModel m;
            try
            {
                m = ObjModel.Load(path, Plugin.RifleLength.Value);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"Failed to load rifle model {path}: {e.Message}");
                return false;
            }

            // The eye sits SightRadius behind the front sight tip (for the M4 that puts it ~8 cm behind the rear sight).
            float eyeZ = Mathf.Max(m.FrontSightZ - Plugin.SightRadius.Value, 0f);
            var go = new GameObject("Mesh");
            go.transform.SetParent(model, false);
            go.transform.localPosition = new Vector3(0f, 0f, -eyeZ);
            go.AddComponent<MeshFilter>().sharedMesh = m.Mesh;
            var r = go.AddComponent<MeshRenderer>();
            var mats = new Material[m.SubmeshColors.Count];
            for (int i = 0; i < mats.Length; i++) mats[i] = MakeMat(lit, m.SubmeshColors[i], 0.35f);
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;

            sightHeight = m.SightHeight;
            muzzleZ = m.MuzzleZ - eyeZ;
            Plugin.Log.LogInfo($"Rifle model {Plugin.RifleModel.Value}: sight height {sightHeight:F3} m, front sight {m.FrontSightZ - eyeZ:F2} m from eye, muzzle {muzzleZ:F2} m");
            return true;
        }

        private float BuildPrimitive(Shader lit)
        {
            const float sight = 0.055f;
            const float back = 0.20f; // eye relief to rear sight
            sightHeight = sight;
            Material metal = MakeMat(lit, new Color(0.12f, 0.12f, 0.13f), 0.6f);
            Material polymer = MakeMat(lit, new Color(0.22f, 0.2f, 0.16f), 0.2f);
            Vector3 o = new Vector3(0f, 0f, back);
            Part("Receiver", PrimitiveType.Cube, o + new Vector3(0f, 0f, 0f), new Vector3(0.045f, 0.06f, 0.24f), metal);
            Part("Handguard", PrimitiveType.Cube, o + new Vector3(0f, -0.004f, 0.2f), new Vector3(0.05f, 0.055f, 0.18f), polymer);
            Part("Barrel", PrimitiveType.Cylinder, o + new Vector3(0f, 0.004f, 0.36f), new Vector3(0.016f, 0.09f, 0.016f), metal, new Vector3(90f, 0f, 0f));
            Part("Magazine", PrimitiveType.Cube, o + new Vector3(0f, -0.075f, 0.04f), new Vector3(0.03f, 0.11f, 0.06f), metal, new Vector3(-12f, 0f, 0f));
            Part("Grip", PrimitiveType.Cube, o + new Vector3(0f, -0.07f, -0.08f), new Vector3(0.03f, 0.09f, 0.04f), polymer, new Vector3(18f, 0f, 0f));
            Part("RearNotchL", PrimitiveType.Cube, o + new Vector3(-0.007f, sight - 0.004f, -0.08f), new Vector3(0.005f, 0.008f, 0.006f), metal);
            Part("RearNotchR", PrimitiveType.Cube, o + new Vector3(0.007f, sight - 0.004f, -0.08f), new Vector3(0.005f, 0.008f, 0.006f), metal);
            Part("FrontPostBase", PrimitiveType.Cube, o + new Vector3(0f, 0.035f, 0.28f), new Vector3(0.02f, 0.03f, 0.015f), metal);
            Part("FrontPost", PrimitiveType.Cube, o + new Vector3(0f, sight - 0.006f, 0.28f), new Vector3(0.003f, 0.012f, 0.003f), metal);
            return back + 0.49f;
        }

        private static Material MakeMat(Shader shader, Color color, float smoothness)
        {
            if (shader == null) return null;
            var m = new Material(shader) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); // double-sided: robust to OBJ winding
            return m;
        }

        private Transform Part(string name, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>()); // never block our own raycasts
            go.name = name;
            go.transform.SetParent(model, false);
            go.transform.localPosition = pos;
            go.transform.localEulerAngles = euler;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        internal void OnFire(bool aiming)
        {
            if (aiming)
            {
                // Straight-back kick only, so the sight picture stays aligned.
                recoilPos += new Vector3(0f, 0f, -0.02f);
                recoilRot += new Vector3(-0.6f, 0f, 0f);
            }
            else
            {
                recoilPos += new Vector3(Random.Range(-0.003f, 0.003f), 0.004f, -0.035f);
                recoilRot += new Vector3(-Random.Range(2f, 3.5f), Random.Range(-1f, 1f), Random.Range(-1.5f, 1.5f));
            }
            flashUntil = Time.time + 0.035f;
            flash.transform.localEulerAngles = new Vector3(0f, 0f, Random.Range(0f, 360f));
        }

        // Called every frame by the controller after look input is applied.
        internal void Tick(bool aiming, bool sprinting, bool reloading, float moveSpeed, Vector2 lookDelta)
        {
            float dt = Time.deltaTime;
            adsBlend = Mathf.MoveTowards(adsBlend, aiming && !sprinting && !reloading ? 1f : 0f, dt * 6f);
            sprintBlend = Mathf.MoveTowards(sprintBlend, sprinting ? 1f : 0f, dt * 5f);
            reloadBlend = Mathf.MoveTowards(reloadBlend, reloading ? 1f : 0f, dt * 5f);
            float hip = 1f - adsBlend; // weight of everything that would misalign the sights

            sway = Vector2.Lerp(sway, -lookDelta * 0.004f, dt * 8f);
            sway = Vector2.ClampMagnitude(sway, 0.03f);

            bobPhase += dt * moveSpeed * 1.8f;
            float bobAmount = Mathf.Clamp01(moveSpeed / 7f);
            Vector3 bob = new Vector3(Mathf.Sin(bobPhase) * 0.012f, -Mathf.Abs(Mathf.Cos(bobPhase)) * 0.01f, 0f) * bobAmount;

            recoilPos = Vector3.Lerp(recoilPos, Vector3.zero, dt * 14f);
            recoilRot = Vector3.Lerp(recoilRot, Vector3.zero, dt * 12f);

            Vector3 pos = AimPos + HipOffset * hip;
            pos = Vector3.Lerp(pos, AimPos + SprintOffset, sprintBlend);
            pos += (new Vector3(sway.x, sway.y, 0f) + bob) * (0.1f + 0.9f * hip)
                   + recoilPos
                   + new Vector3(0f, -0.08f, -0.05f) * reloadBlend;
            transform.localPosition = pos;

            Vector3 rot = SprintRot * sprintBlend + recoilRot + new Vector3(35f, 0f, -25f) * reloadBlend
                          + new Vector3(sway.y * 200f, -sway.x * 200f, sway.x * 300f) * hip;
            transform.localRotation = Quaternion.Euler(rot);

            bool flashing = Time.time < flashUntil;
            flash.SetActive(flashing);
            flashLight.enabled = flashing;
        }
    }
}
