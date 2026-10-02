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

            GroundVehicle gv = VehicleControl.Driven;
            Quaternion look = Quaternion.Euler(Pitch, Yaw, 0f);
            if (gv != null)
            {
                UpdateVehicleCamera(cam, gv, look);
                return;
            }

            Vector3 eye = Pilot.transform.position + Vector3.up * Plugin.EyeHeight.Value;
            cam.cameraPivot.position = eye;
            cam.cameraPivot.rotation = Quaternion.identity;
            cam.transform.SetPositionAndRotation(eye, look);
        }

        // Third-person orbit around the vehicle; the aim point is whatever is under the screen centre.
        private static void UpdateVehicleCamera(CameraStateManager cam, GroundVehicle gv, Quaternion look)
        {
            Vector3 pivot = gv.transform.position + Vector3.up * (gv.definition.height * 0.8f + 1.5f);
            float dist = gv.maxRadius * 2f + Plugin.VehicleCamDistance.Value;
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
            VehicleControl.AimPoint = pos + fwd * 3000f;
            foreach (RaycastHit h in Physics.RaycastAll(pos, fwd, 3000f, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(gv.transform)) continue;
                if ((h.point - pos).sqrMagnitude < (VehicleControl.AimPoint - pos).sqrMagnitude) VehicleControl.AimPoint = h.point;
            }
        }

        public override void FixedUpdateState(CameraStateManager cam)
        {
        }
    }
}
