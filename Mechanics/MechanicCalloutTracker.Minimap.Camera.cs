using System;
using UnityEngine;

namespace Stellar.RaidManager;

// Camera view cone on the local player's minimap marker (setting rm.mech.map.camCone, default OFF) — the soft white
// wedge the game's own minimap draws in the direction the CAMERA looks (separate from the facing arrow, which is the
// character's model yaw). The yaw lives on the local MinimapDot (CamYaw/HasCamYaw) so it rides the same per-frame
// fast path as the facing arrow; the painter draws the wedge (MechanicMinimapPainter.DrawCameraCone).
//
// Source: UnityEngine.Camera.main = the game's world "Main Camera" (Nameplate-HUD.md — no dedicated HUD camera; its
// cullingMask is odd during loading but the transform is fine). yaw = atan2(fwd.x, fwd.z), the same convention as
// the model yaw (0° = +Z, 90° = +X). Option OFF ⇒ no Camera.main read at all.
internal sealed partial class MechanicCalloutTracker
{
    public bool ShowCameraCone { get; set; }

    private const float CamConeTurnDeg = 2f;   // repaint threshold, like the facing arrow's 2° (no repaint per tiny turn)

    // Camera.main can hand back a destroyed camera across a scene transition (Unity fake-null: `== null` may miss it
    // and the transform read throws) — so the whole read is guarded and a failure just means "no cone this frame".
    // Near-vertical forward (looking straight down/up) has no meaningful yaw → no cone.
    private static bool TryCameraYaw(out float yawDeg)
    {
        yawDeg = float.NaN;
        try
        {
            var cam = Camera.main;
            if (cam == null) return false;
            var f = cam.transform.forward;
            if (f.x * f.x + f.z * f.z < 0.01f * 0.01f) return false;
            yawDeg = MathF.Atan2(f.x, f.z) * 180f / MathF.PI;
            return true;
        }
        catch { return false; }
    }

    // Scan-time and per-frame update of the local dot's camera yaw. Returns true when the dot changed enough to
    // repaint: cone appeared/disappeared, or the camera turned more than CamConeTurnDeg.
    private bool UpdateCameraCone(ref MinimapDot d)
    {
        if (!ShowCameraCone || !TryCameraYaw(out float yaw))
        {
            if (!d.HasCamYaw) return false;
            d.HasCamYaw = false;
            return true;
        }
        if (d.HasCamYaw && MathF.Abs(DeltaDeg(yaw, d.CamYaw)) <= CamConeTurnDeg) return false;
        d.CamYaw = yaw; d.HasCamYaw = true;
        return true;
    }
}
