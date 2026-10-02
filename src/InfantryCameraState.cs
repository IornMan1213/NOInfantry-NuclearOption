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

            Vector3 eye = Pilot.transform.position + Vector3.up * Plugin.EyeHeight.Value;
            cam.cameraPivot.position = eye;
            cam.cameraPivot.rotation = Quaternion.identity;
            cam.transform.SetPositionAndRotation(eye, Quaternion.Euler(Pitch, Yaw, 0f));
        }

        public override void FixedUpdateState(CameraStateManager cam)
        {
        }
    }
}
