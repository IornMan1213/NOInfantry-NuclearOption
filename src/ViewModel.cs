using UnityEngine;
using UnityEngine.Rendering;

namespace NOInfantry
{
    // First-person rifle built from primitives (no extracted assets). Parented to the main camera.
    // Local axes: +z forward along the bore, bore at y = 0, iron sights at y = SightHeight.
    internal class ViewModel : MonoBehaviour
    {
        private const float SightHeight = 0.055f;

        private static readonly Vector3 HipPos = new Vector3(0.14f, -0.15f, 0.30f);
        private static readonly Vector3 AdsPos = new Vector3(0f, -SightHeight, 0.20f);
        private static readonly Vector3 SprintPos = new Vector3(0.10f, -0.20f, 0.24f);
        private static readonly Vector3 SprintRot = new Vector3(15f, -35f, 10f);

        internal Transform Muzzle { get; private set; }

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
            Material metal = MakeMat(lit, new Color(0.12f, 0.12f, 0.13f), 0.6f);
            Material polymer = MakeMat(lit, new Color(0.22f, 0.2f, 0.16f), 0.2f);

            // name, shape, local position, scale, material
            Part("Receiver", PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(0.045f, 0.06f, 0.24f), metal);
            Part("Handguard", PrimitiveType.Cube, new Vector3(0f, -0.004f, 0.2f), new Vector3(0.05f, 0.055f, 0.18f), polymer);
            Part("Barrel", PrimitiveType.Cylinder, new Vector3(0f, 0.004f, 0.36f), new Vector3(0.016f, 0.09f, 0.016f), metal, new Vector3(90f, 0f, 0f));
            Part("FlashHider", PrimitiveType.Cylinder, new Vector3(0f, 0.004f, 0.46f), new Vector3(0.024f, 0.025f, 0.024f), metal, new Vector3(90f, 0f, 0f));
            Part("Magazine", PrimitiveType.Cube, new Vector3(0f, -0.075f, 0.04f), new Vector3(0.03f, 0.11f, 0.06f), metal, new Vector3(-12f, 0f, 0f));
            Part("Grip", PrimitiveType.Cube, new Vector3(0f, -0.07f, -0.08f), new Vector3(0.03f, 0.09f, 0.04f), polymer, new Vector3(18f, 0f, 0f));
            Part("Stock", PrimitiveType.Cube, new Vector3(0f, -0.015f, -0.22f), new Vector3(0.04f, 0.07f, 0.16f), polymer);
            Part("RearSight", PrimitiveType.Cube, new Vector3(0f, 0.04f, -0.08f), new Vector3(0.03f, 0.02f, 0.015f), metal);
            Part("RearNotchL", PrimitiveType.Cube, new Vector3(-0.009f, SightHeight, -0.08f), new Vector3(0.008f, 0.012f, 0.012f), metal);
            Part("RearNotchR", PrimitiveType.Cube, new Vector3(0.009f, SightHeight, -0.08f), new Vector3(0.008f, 0.012f, 0.012f), metal);
            Part("FrontPostBase", PrimitiveType.Cube, new Vector3(0f, 0.035f, 0.28f), new Vector3(0.02f, 0.03f, 0.015f), metal);
            Part("FrontPost", PrimitiveType.Cube, new Vector3(0f, SightHeight, 0.28f), new Vector3(0.004f, 0.016f, 0.004f), metal);

            Muzzle = new GameObject("Muzzle").transform;
            Muzzle.SetParent(model, false);
            Muzzle.localPosition = new Vector3(0f, 0.004f, 0.49f);

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

            transform.localPosition = HipPos;
        }

        private static Material MakeMat(Shader shader, Color color, float smoothness)
        {
            if (shader == null) return null;
            var m = new Material(shader) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
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
            float k = aiming ? 0.5f : 1f;
            recoilPos += new Vector3(Random.Range(-0.003f, 0.003f), 0.004f * k, -0.035f * k);
            recoilRot += new Vector3(-Random.Range(2f, 3.5f) * k, Random.Range(-1f, 1f) * k, Random.Range(-1.5f, 1.5f));
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

            sway = Vector2.Lerp(sway, -lookDelta * 0.004f * (1f - adsBlend * 0.8f), dt * 8f);
            sway = Vector2.ClampMagnitude(sway, 0.03f);

            bobPhase += dt * moveSpeed * 1.8f;
            float bobAmount = Mathf.Clamp01(moveSpeed / 7f) * (1f - adsBlend * 0.85f);
            Vector3 bob = new Vector3(Mathf.Sin(bobPhase) * 0.012f, -Mathf.Abs(Mathf.Cos(bobPhase)) * 0.01f, 0f) * bobAmount;

            recoilPos = Vector3.Lerp(recoilPos, Vector3.zero, dt * 14f);
            recoilRot = Vector3.Lerp(recoilRot, Vector3.zero, dt * 12f);

            Vector3 pos = Vector3.Lerp(HipPos, AdsPos, adsBlend);
            pos = Vector3.Lerp(pos, SprintPos, sprintBlend);
            pos += new Vector3(sway.x, sway.y, 0f) + bob + recoilPos + new Vector3(0f, -0.08f, -0.05f) * reloadBlend;
            transform.localPosition = pos;

            Vector3 rot = SprintRot * sprintBlend + recoilRot + new Vector3(35f, 0f, -25f) * reloadBlend
                          + new Vector3(sway.y * 200f, -sway.x * 200f, sway.x * 300f);
            transform.localRotation = Quaternion.Euler(rot);

            bool flashing = Time.time < flashUntil;
            flash.SetActive(flashing);
            flashLight.enabled = flashing;
        }
    }
}
