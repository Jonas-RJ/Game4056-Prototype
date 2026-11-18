using UnityEngine;

namespace AutonomousCompanion.Vision
{
    public sealed partial class VisionAgent
    {
        private const string JumpOptionKind = "jump_option";
        public bool useJumpOptionIds = true;

        private VisionJumpOption selectedJumpOption;
        private float jumpOptionTakeoffX, jumpOptionLaunchTime;
        private bool jumpOptionLaunched;

        private bool TickJumpOption(VisionPlanStep step, Vector3 position, bool grounded)
        {
            var option = selectedJumpOption;
            if (jumpOptionLaunched)
            {
                stepAirborne |= !grounded;
                if (stepAirborne && grounded)
                {
                    if (Mathf.Abs(position.x - option.LandingWorld.x) > .30f ||
                        Mathf.Abs(position.y - option.LandingWorld.y) > .20f)
                        FailPlan("jump_option_landed_outside_selected_region");
                    else if (!boundActor.ValidateJumpOptionLanding(option, out string landingReason))
                        FailPlan("jump_option_landing_support: " + landingReason);
                    else
                        FinishPlanStep("jump_option_landed:" + option.option_id);
                    return false;
                }
                if (stepSeconds >= step.timeout_seconds || stepSeconds - jumpOptionLaunchTime >= MaxJumpSeconds)
                {
                    FailPlan("jump_landing_timeout");
                    return false;
                }
                if (!ApplyPlanStep(option.horizontal, false)) return false;
                previous.duration_seconds = Mathf.Clamp(stepSeconds - jumpOptionLaunchTime, .05f, 1f);
                return true;
            }

            if (!grounded) { FailPlan("jump_option_requires_ground"); return false; }
            if (stepSeconds + MaxJumpSeconds > step.timeout_seconds)
            {
                FailPlan("jump_option_takeoff_timeout");
                return false;
            }
            float dx = jumpOptionTakeoffX - position.x;
            if (Mathf.Abs(dx) > MoveArrivalTolerance)
            {
                if (option.from_current) { FailPlan("jump_option_start_pose_changed"); return false; }
                if (!stepStarted) AuditPlan("jump_option_approach", option.option_id);
                return DrivePlanWalk(dx, position);
            }

            // The observation only supplied a prediction. Check the actual pose and current
            // colliders against the selected landing immediately before sending jump input.
            if (!boundActor.RevalidateJumpOption(option, out string reason))
            {
                LocalVetoes++;
                AuditPlan("local_veto", "jump_option_revalidation: " + reason);
                FailPlan("jump_option_revalidation: " + reason);
                return false;
            }
            AuditPlan("jump_option_revalidated", option.option_id);
            if (!ApplyPlanStep(option.horizontal, true)) return false;
            jumpOptionLaunched = true;
            jumpOptionLaunchTime = stepSeconds;
            previous = new VisionAction { horizontal = option.horizontal, jump = true, duration_seconds = .05f };
            AuditPlan("jump_option_launched", option.option_id);
            return true;
        }
    }
}
