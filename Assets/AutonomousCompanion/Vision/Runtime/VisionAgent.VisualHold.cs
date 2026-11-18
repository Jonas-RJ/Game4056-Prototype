using System;
using UnityEngine;

namespace AutonomousCompanion.Vision
{
    public sealed partial class VisionAgent
    {
        public const string HoldUntilGateLatched = "hold_until_gate_latched";
        public const string HoldUntilPlayerPassedGate = "hold_until_player_passed_gate";

        public SpriteRenderer gateLatchedMarker;
        public SpriteRenderer playerPassedGateMarker;

        private float nextHoldSample;
        private int matchedHoldSamples;

        private static bool IsVisualHold(string kind) =>
            kind == HoldUntilGateLatched || kind == HoldUntilPlayerPassedGate;

        private VisionPlanRequest CreatePlanObservation()
        {
            VisionPlanRequest request = new VisionPlanRequest();
            if (gateLatchedMarker != null || playerPassedGateMarker != null)
            {
                string[] events = gateLatchedMarker == null ? new[] { HoldUntilPlayerPassedGate } :
                    playerPassedGateMarker == null ? new[] { HoldUntilGateLatched } :
                    new[] { HoldUntilGateLatched, HoldUntilPlayerPassedGate };
                request = new VisionVisualHoldRequest { visual_hold_events = events };
            }
            request.protocol = "short_plan_v2";
            request.previous_plan = lastPlanOutcome == null ? Array.Empty<VisionPlanOutcome>() : new[] { lastPlanOutcome };
            request.progress_memory = planProgress;
            return request;
        }

        private bool TickVisualHold(VisionPlanStep step, bool grounded, Vector3 position)
        {
            if (!grounded || (stepStarted && (position - stepStart).sqrMagnitude > .12f * .12f))
            {
                FailPlan("visual_hold_lost_position");
                return false;
            }
            if (matchedHoldSamples >= 2)
            {
                FinishPlanStep("visual_hold_observed:" + step.kind);
                return false;
            }
            if (stepSeconds >= step.timeout_seconds)
            {
                FinishPlanStep("visual_hold_timeout:" + step.kind);
                return false;
            }
            return ApplyPlanStep(0, false);
        }

        private void SampleVisualHold()
        {
            var step = activePlan.steps[planStep];
            if (!IsVisualHold(step.kind) || !stepStarted || matchedHoldSamples >= 2 ||
                stepSeconds < nextHoldSample || stepSeconds >= step.timeout_seconds) return;
            nextHoldSample = stepSeconds + .12f;
            try
            {
                CapturePixels();
            }
            catch (Exception exception)
            {
                FailPlan("visual_hold_capture_failed:" + exception.GetType().Name);
                return;
            }
            var marker = step.kind == HoldUntilGateLatched ? gateLatchedMarker : playerPassedGateMarker;
            matchedHoldSamples = MarkerVisibleInCapture(marker) ? matchedHoldSamples + 1 : 0;
        }

        private bool MarkerVisibleInCapture(SpriteRenderer marker)
        {
            if (marker == null || !marker.enabled || !marker.gameObject.activeInHierarchy) return false;
            Bounds bounds = marker.bounds;
            Vector3 low = gameCamera.WorldToViewportPoint(bounds.center - bounds.extents * .4f);
            Vector3 high = gameCamera.WorldToViewportPoint(bounds.center + bounds.extents * .4f);
            if (low.z <= 0 || high.z <= 0 || low.x < 0 || low.y < 0 || high.x >= 1 || high.y >= 1 ||
                (high.x - low.x) * readback.width < 2 || (high.y - low.y) * readback.height < 2)
                return false;

            int hits = 0;
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                var pixel = readback.GetPixel((int)(Mathf.Lerp(low.x, high.x, x * .5f) * readback.width),
                    (int)(Mathf.Lerp(low.y, high.y, y * .5f) * readback.height));
                if (pixel.r > .74f && pixel.b > .56f && pixel.g < .45f) hits++;
            }
            return hits >= 7;
        }
    }
}
