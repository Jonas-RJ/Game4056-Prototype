using AutonomousCompanion.Vision;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    /// <summary>Grants exclusive control over the companion while visual mode is active.</summary>
    public sealed class DemoVisionActorAdapter : VisionActorAdapter
    {
        public DemoLevel level;
        private CompanionWorld observedWorld;
        private bool ownsMotor, restoreControllers;
        private VisionAgent controller;
        private bool brainEnabled, executorEnabled, clientEnabled;
        private DemoVisionSafetyGuard safetyGuard;

        public override bool IsAlive => level != null && level.companion != null && level.companion.IsAlive;
        public override bool IsGrounded => IsAlive && level.companion.IsGrounded;
        public override bool CanAct => isActiveAndEnabled && IsAlive && !level.Completed &&
            level.player != null && level.player.IsAlive;
        public override Vector3 CameraFocus => level.companion.Position;
        public override string LocalSafetyObservation => GetSafetyGuard()?.Observe() ?? "";

        public override VisionJumpOption[] ObserveJumpOptions(Camera camera, float leftX, float rightX) =>
            GetSafetyGuard()?.ObserveJumpOptions(camera, leftX, rightX) ?? System.Array.Empty<VisionJumpOption>();

        public override bool RevalidateJumpOption(VisionJumpOption option, out string reason)
        {
            var guard = GetSafetyGuard();
            return guard != null ? guard.RevalidateJumpOption(option, out reason) : base.RevalidateJumpOption(option, out reason);
        }

        public override bool ValidateJumpOptionLanding(VisionJumpOption option, out string reason)
        {
            var guard = GetSafetyGuard();
            return guard != null ? guard.ValidateJumpOptionLanding(option, out reason) : base.ValidateJumpOptionLanding(option, out reason);
        }

        private DemoVisionSafetyGuard GetSafetyGuard()
        {
            var companion = level != null ? level.companion : null;
            if (companion == null) return null;
            if (safetyGuard == null || safetyGuard.Actor != companion)
                safetyGuard = new DemoVisionSafetyGuard(companion);
            return safetyGuard;
        }

        private void OnEnable()
        {
            observedWorld = level != null ? level.world : null;
            if (observedWorld != null) observedWorld.Changed += ObserveWorld;
            TryRestoreControllers();
        }

        private void OnDisable()
        {
            ReleaseControl(controller);
            if (observedWorld != null) observedWorld.Changed -= ObserveWorld;
            observedWorld = null;
        }

        private void ObserveWorld(string description)
        {
            // Read only the lifecycle notification. Puzzle events stay inside the scene.
            if (description != "world_reset") return;
            NotifyWorldReset();
        }

        private void LateUpdate() => TryRestoreControllers();

        public override bool TryAcquireControl(VisionAgent owner)
        {
            if (owner == null) return false;
            if (ownsMotor) return controller == owner;
            if (!CanAct || level.companion == level.player || level.companion.allowKeyboard) return false;

            var brain = level.brain;
            var executor = brain != null ? brain.executor : null;
            if (executor != null && (executor.HasProtectedHold ||
                (executor.isActiveAndEnabled && (!executor.CanAcceptDecision ||
                    (executor.CurrentTask.status == "running" && executor.CurrentTask.action != "wait"))))) return false;
            if (!level.companion.TryAcquireAutonomousControl(this)) return false;

            ownsMotor = true;
            controller = owner;
            brainEnabled = brain != null && brain.enabled;
            executorEnabled = executor != null && executor.enabled;
            clientEnabled = brain != null && brain.client != null && brain.client.enabled;
            if (brain != null)
            {
                brain.enabled = false;
                if (executor != null) executor.enabled = false;
                if (brain.client != null) brain.client.enabled = false;
            }
            return true;
        }

        public override bool ApplyControl(VisionAgent owner, int horizontal, bool jump) =>
            ownsMotor && controller == owner && CanAct && level.companion.ApplyAutonomousControl(this, horizontal, jump);

        public override float MovementSpeed => level.companion.moveSpeed;

        public override bool ApplyPlanControl(VisionAgent owner, float horizontal, bool jump) =>
            ownsMotor && controller == owner && CanAct && level.companion.ApplyAutonomousControl(this, horizontal, jump);

        public override bool CheckControlSafety(int horizontal, bool jump, out string reason)
        {
            var guard = GetSafetyGuard();
            if (guard != null) return guard.Check(horizontal, jump, out reason);
            reason = "Companion collision shape unavailable";
            return false;
        }

        public override void StopControl(VisionAgent owner)
        {
            if (ownsMotor && controller == owner && level != null && level.companion != null)
                level.companion.StopAutonomousControl(this);
        }

        public override void ReleaseControl(VisionAgent owner)
        {
            if (!ownsMotor || controller != owner) return;
            ownsMotor = false;
            controller = null;
            if (level != null && level.companion != null)
                level.companion.ReleaseAutonomousControl(this);
            restoreControllers = true;
            TryRestoreControllers();
        }

        private void TryRestoreControllers()
        {
            if (!restoreControllers || level == null || level.Completed || ownsMotor) return;
            restoreControllers = false;
            var brain = level.brain;
            if (brain == null) return;
            if (brain.executor != null) brain.executor.enabled = executorEnabled;
            if (brain.client != null) brain.client.enabled = clientEnabled;
            brain.enabled = brainEnabled;
        }
    }
}
