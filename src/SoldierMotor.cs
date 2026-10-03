using UnityEngine;

namespace NOInfantry
{
    // Ground movement for a PilotDismounted body, shared by the player's soldier and AI soldiers.
    // Hover spring constants match the game's own landed-pilot logic (PilotDismounted.LocalFixedUpdate).
    internal static class SoldierMotor
    {
        internal const float HoverHeight = 1.3f;
        internal const float DownedHover = 0.45f, DownedPitch = 75f;

        // wishVelocity: desired horizontal velocity in world space (m/s). Returns true when on the ground.
        internal static bool Step(PilotDismounted pilot, float yaw, Vector3 wishVelocity, bool jump, bool downed = false)
        {
            float hover = downed ? DownedHover : HoverHeight;
            Rigidbody rb = pilot.rb;
            Vector3 pos = pilot.transform.position;
            bool inWater = pos.GlobalY() < 0f;

            rb.drag = 0f;
            rb.angularVelocity = Vector3.zero;
            rb.MoveRotation(Quaternion.Euler(downed ? DownedPitch : 0f, yaw, 0f)); // downed: slumped forward, low

            Vector3 groundVel = Vector3.zero;
            bool grounded = Physics.Linecast(pos, pos - Vector3.up * (HoverHeight + 0.4f), out RaycastHit hit, ~(int)PhysicsLayers.ExclusionZonesMask)
                            && !hit.collider.transform.IsChildOf(pilot.transform);
            if (grounded && hit.collider.attachedRigidbody != null)
            {
                groundVel = hit.collider.attachedRigidbody.GetPointVelocity(hit.point);
            }

            Vector3 wishVel = new Vector3(wishVelocity.x, 0f, wishVelocity.z) + groundVel;
            Vector3 vel = rb.velocity;
            Vector3 horiz = new Vector3(vel.x - wishVel.x, 0f, vel.z - wishVel.z);

            if (inWater)
            {
                rb.AddForce(Vector3.up * rb.mass * 25f * Mathf.Clamp01(Datum.LocalSeaY - pos.y));
                rb.AddForce(-horiz * rb.mass * 2f - Vector3.up * vel.y * rb.mass);
            }
            else if (grounded)
            {
                // Spring to hover height plus strong horizontal control.
                float relVy = vel.y - groundVel.y;
                rb.AddForce(Vector3.up * (hover - hit.distance - relVy * 0.2f) * rb.mass * 25f - Vector3.up * relVy * rb.mass * 2f);
                rb.AddForce(-horiz * rb.mass * 12f);
                if (jump)
                {
                    rb.velocity = new Vector3(vel.x, groundVel.y + Plugin.JumpSpeed.Value, vel.z);
                }
                if (pilot.animationState != PilotDismounted.PilotState.landing)
                {
                    pilot.SetPilotState(PilotDismounted.PilotState.landing);
                }
            }
            else
            {
                rb.AddForce(-horiz * rb.mass * 1.5f); // a little air control
            }
            return grounded;
        }
    }
}
