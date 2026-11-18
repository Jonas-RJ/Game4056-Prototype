using System;
using UnityEngine;

namespace AutonomousCompanion.Vision
{
    [Serializable]
    public sealed class VisionActorObservation
    {
        public bool alive, grounded;
        public float screen_x, screen_y;
    }

    [Serializable]
    public sealed class VisionAction
    {
        public int horizontal;
        public bool jump;
        public float duration_seconds;
    }

    [Serializable]
    public class VisionRequest
    {
        public string request_id, session_id, image_base64, image_mime, goal;
        public string protocol = "";
        public int frame_id;
        public VisionActorObservation actor;
        public VisionAction previous_action;
        public string[] recent_events, memory;
    }

    [Serializable]
    public class VisionPlanRequest : VisionRequest
    {
        public VisionPlanOutcome[] previous_plan;
        public VisionProgressMemory progress_memory;
        public VisionJumpOption[] jump_options = Array.Empty<VisionJumpOption>();
        public bool jump_option_ids;
    }

    [Serializable]
    public sealed class VisionVisualHoldRequest : VisionPlanRequest
    {
        public string[] visual_hold_events;
    }

    [Serializable]
    public sealed class VisionJumpOption
    {
        public string option_id;
        public int horizontal;
        public bool from_current;
        public float takeoff_screen_x, landing_screen_x, landing_screen_y;
        internal Vector2 LandingWorld;
        internal Collider2D LandingSurface;
    }

    [Serializable]
    public sealed class VisionProgressMemory
    {
        public int observed_frame_id;
        public string[] observations = Array.Empty<string>();
        public string[] hypotheses = Array.Empty<string>();
    }

    [Serializable]
    public sealed class VisionPlanOutcome
    {
        public int frame_id, cancelled_steps;
        public VisionPlan plan;
        public string status, detail;
        public VisionPlanStepOutcome[] step_results;
    }

    [Serializable]
    public sealed class VisionPlanStepOutcome
    {
        public string status, detail;
        public bool started, end_grounded;
        public float start_x, start_y, end_x, end_y, elapsed_seconds;
    }

    [Serializable]
    public sealed class VisionPlanStep
    {
        public string kind;
        public string option_id;
        public float screen_x;
        public int horizontal;
        public float timeout_seconds;
    }

    [Serializable]
    public sealed class VisionPlan
    {
        public string objective, expected_result;
        public VisionPlanStep[] steps;
    }

    [Serializable]
    public sealed class VisionResponse
    {
        public string request_id, session_id, source, reason;
        public int frame_id;
        public VisionAction action;
        public VisionPlan plan;
        public string[] memory;
        public VisionProgressMemory progress_memory;
    }
}
