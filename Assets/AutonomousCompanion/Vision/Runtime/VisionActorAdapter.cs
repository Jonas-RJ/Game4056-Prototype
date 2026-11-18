using System;
using UnityEngine;

namespace AutonomousCompanion.Vision
{
    /// <summary>Scene-specific bridge to an actor motor and its local movement safety checks.</summary>
    public abstract class VisionActorAdapter : MonoBehaviour
    {
        public abstract bool IsAlive { get; }
        public abstract bool IsGrounded { get; }
        public abstract bool CanAct { get; }
        public abstract Vector3 CameraFocus { get; }
        public virtual string LocalSafetyObservation => "";

        /// <summary>Predicts visible takeoff/landing pairs using the current geometry.</summary>
        public virtual VisionJumpOption[] ObserveJumpOptions(Camera camera, float leftX, float rightX) =>
            Array.Empty<VisionJumpOption>();

        /// <summary>Checks the current arc against the landing selected from the observation.</summary>
        public virtual bool RevalidateJumpOption(VisionJumpOption option, out string reason)
        {
            reason = "jump_option_revalidation_unavailable";
            return false;
        }

        public event Action WorldReset;
        /// <summary>Checks actual support on landing, separately from the prelaunch prediction.</summary>
        public virtual bool ValidateJumpOptionLanding(VisionJumpOption option, out string reason)
        {
            reason = "jump_option_landing_validation_unavailable";
            return false;
        }

        protected void NotifyWorldReset() => WorldReset?.Invoke();

        public abstract bool TryAcquireControl(VisionAgent owner);

        /// <summary>Checks the proposed motor input for this physics step. Jump is true at launch.</summary>
        public virtual bool CheckControlSafety(int horizontal, bool jump, out string reason)
        {
            reason = "";
            return true;
        }

        public abstract bool ApplyControl(VisionAgent owner, int horizontal, bool jump);
        public virtual float MovementSpeed => 4f;
        public virtual bool ApplyPlanControl(VisionAgent owner, float horizontal, bool jump) =>
            ApplyControl(owner, horizontal > 0 ? 1 : horizontal < 0 ? -1 : 0, jump);

        public abstract void StopControl(VisionAgent owner);
        public abstract void ReleaseControl(VisionAgent owner);
    }
}
