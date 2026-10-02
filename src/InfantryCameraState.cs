using UnityEngine;

namespace NOInfantry
{
    // First-person camera pinned to the pilot's head. Look angles live here; the controller reads them.
    internal class InfantryCameraState : CameraBaseState
    {
        public float Yaw;
        public float Pitch;
        public PilotDismounted Pilot;

        public override void EnterState(CameraStateManager cam)
        {
            cam.cameraPivot.SetParent(Datum.origin, true);
            cam.transform.SetParent(cam.cameraPivot, true);
            CameraStateManager.cameraMode = CameraMode.free;
            FlightHud.EnableCanvas(false);
            cam.mainCamera.nearClipPlane = 0.05f;
        }

        public override void LeaveState(CameraStateManager cam)
        {
            cam.mainCamera.nearClipPlane = 0.3f;
        }

        public override void UpdateState(CameraStateManager cam)
        {
            if (Pilot == null) return;

            float targetFov = InfantryController.Instance.Aiming ? Plugin.AdsFov.Value : Plugin.Fov.Value;
            cam.mainCamera.fieldOfView = Mathf.Lerp(cam.mainCamera.fieldOfView, targetFov, 12f * Time.unscaledDeltaTime);

            Unit driven = VehicleControl.Driven;
            Quaternion look = Quaternion.Euler(Pitch, Yaw, 0f);
            if (driven != null)
            {
                UpdateVehicleCamera(cam, driven, look);
                return;
            }

            Vector3 eye = Pilot.transform.position + Vector3.up * Plugin.EyeHeight.Value;
            cam.cameraPivot.position = eye;
            cam.cameraPivot.rotation = Quaternion.identity;
            cam.transform.SetPositionAndRotation(eye, look);
        }

        // Third-person orbit around the vehicle; the aim point is whatever is under the screen centre.
        private static void UpdateVehicleCamera(CameraStateManager cam, Unit gv, Quaternion look)
        {
            Vector3 pivot = gv.transform.position + Vector3.up * (gv.definition.height * 0.8f + 1.5f);
            float dist = Mathf.Min(gv.maxRadius * 2f, 250f) + Plugin.VehicleCamDistance.Value;
            Vector3 back = look * Vector3.back;
            if (Physics.Raycast(pivot, back, out RaycastHit block, dist, PhysicsLayers.StaticsMask))
            {
                dist = Mathf.Max(block.distance - 0.5f, 1f);
            }
            Vector3 pos = pivot + back * dist;
            cam.cameraPivot.position = pivot;
            cam.cameraPivot.rotation = Quaternion.identity;
            cam.transform.SetPositionAndRotation(pos, look);

            Vector3 fwd = look * Vector3.forward;
            VehicleControl.AimValid = true;
            VehicleControl.AimPoint = pos + fwd * 5000f;
            VehicleControl.AimUnit = null;
            float best = float.MaxValue;
            foreach (RaycastHit h in Physics.RaycastAll(pos, fwd, 5000f, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore))
            {
                // Ignore everything up to just past the vehicle (camera sits behind it), the vehicle itself
                // (some colliders aren't parented under its root) and our soldier riding inside it.
                if (h.distance >= best || h.distance < dist + gv.maxRadius * 1.5f) continue;
                if (h.collider.transform.IsChildOf(gv.transform) || h.collider.attachedRigidbody == gv.rb) continue;
                if (InfantryController.Controlled != null && h.collider.transform.IsChildOf(InfantryController.Controlled.transform)) continue;
                Unit hitUnit = h.collider.GetComponentInParent<IDamageable>()?.GetUnit();
                if (hitUnit == gv) continue;
                best = h.distance;
                VehicleControl.AimPoint = h.point;
                VehicleControl.AimUnit = hitUnit != null && hitUnit.NetworkHQ != gv.NetworkHQ && !hitUnit.disabled ? hitUnit : null;
            }
        }

        public override void FixedUpdateState(CameraStateManager cam)
        {
        }
    }
}
