using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AutonomousCompanion.Vision
{
    public sealed partial class VisionAgent
    {
        public bool useShortPlans = true;
        [Tooltip("Keep off for offline/substitute tests. Requires a compatible restarted local plan gateway.")]
        public bool allowPaidPlanRequests = false;
        [Range(1, 20)] public int planRequestsPerMinute = 5;
        [Range(1, 100)] public int maxPlanRequests = 20;
        [Range(1, 10)] public int maxConsecutivePlanFailures = 3;
        [Range(.2f, 5f)] public float localObservationSeconds = 1f;
        public int PlanRequests { get; private set; }
        public int ModelPlansAccepted { get; private set; }
        public int SubstitutePlansAccepted { get; private set; }
        public int PlanStepsApplied { get; private set; }
        public int PlansCompleted { get; private set; }
        public int PlansFailed { get; private set; }
        public int SuppressedObservations { get; private set; }
        public string LastPlanResult { get; private set; } = "none";
        public bool ExecutingPlan => activePlan != null;
        public string PlanMailboxDirectory => planTestDirectory;
        public string PendingPlanRequestPath => pending != null && PlanMailboxActive ? MailboxPath("request.json") : "";
        public string LastPlanSource { get; private set; } = "";
        public PlanAuditEntry[] GetPlanAuditTrail() => planAudit.ToArray();

        [Serializable]
        public sealed class PlanAuditEntry
        {
            public string kind, source, request_id, session_id, detail;
            public int frame_id, step_index;
            public float time;
            public Vector3 position;
        }

        private readonly Queue<double> planRequestTimes = new Queue<double>();
        private readonly List<PlanAuditEntry> planAudit = new List<PlanAuditEntry>();
        private string planTestDirectory;
        private double mailboxCapturedTime;
        internal const float MoveArrivalTolerance = .045f;
        private double PendingObservationAge => useShortPlans && PlanMailboxActive
            ? Time.fixedTimeAsDouble - mailboxCapturedTime : Time.realtimeSinceStartupAsDouble - pendingCapturedAt;
        private bool PlanMailboxActive => !string.IsNullOrEmpty(planTestDirectory);
        private VisionPlan activePlan;
        private VisionPlanOutcome lastPlanOutcome;
        private VisionProgressMemory planProgress = new VisionProgressMemory();
        private readonly List<VisionPlanStepOutcome> planStepResults = new List<VisionPlanStepOutcome>(4);
        private int planStep, consecutivePlanFailures;
        private float stepSeconds, planSeconds, stepStillSeconds;
        private bool stepStarted, stepAirborne, planMoved, planOutcomePending;
        private Vector3 stepStart, stepLastPosition, lastRequestedPosition, pendingPlanPosition;
        private Vector3 pendingLeft, pendingRight;
        private float[] planTargets;
        private Color32[] requestedSamples;
        private bool haveRequestedFrame;
        private double lastPlanRequestAt = double.NegativeInfinity;
        private string planRequestId, planSessionId;
        private int planFrameId;

        // Explicit test transport: no fallback to HTTP if a file is absent or invalid.
        public void UsePlanSubstitute(string absoluteDirectory)
        {
            if (isActiveAndEnabled || !Path.IsPathRooted(absoluteDirectory))
                throw new InvalidOperationException("Disable agent and use an absolute test directory first.");
            Directory.CreateDirectory(absoluteDirectory);
            planTestDirectory = absoluteDirectory;
            allowPaidPlanRequests = false;
        }

        // Only an explicit user/test action resets budget; world resets/toggles do not.
        public void ResetPlanBudget()
        {
            if (isActiveAndEnabled) throw new InvalidOperationException("Disable agent before resetting its request budget.");
            PlanRequests = 0;
            planRequestTimes.Clear();
            lastPlanRequestAt = double.NegativeInfinity;
            consecutivePlanFailures = 0;
        }

        private void ResetPlanSession()
        {
            StopPlan("session_reset");
            haveRequestedFrame = false;
            requestedSamples = null;
            planOutcomePending = false;
            LastPlanResult = "none";
            lastPlanOutcome = null;
            planProgress = new VisionProgressMemory();
            planStepResults.Clear();
        }

        private bool PlanMayObserve()
        {
            if (PlanRequests >= Mathf.Clamp(maxPlanRequests, 1, 100))
            { Status = "plan_budget_exhausted"; return false; }
            if (consecutivePlanFailures >= Mathf.Clamp(maxConsecutivePlanFailures, 1, 10))
            { Status = "plan_failure_limit"; return false; }
            if (!PlanMailboxActive && FixtureResponder == null && !allowPaidPlanRequests)
            { Status = "paid_plan_requests_disabled"; return false; }
            double now = Time.realtimeSinceStartupAsDouble;
            int limit = Mathf.Clamp(planRequestsPerMinute, 1, 20);
            while (planRequestTimes.Count > 0 && now - planRequestTimes.Peek() >= 60d) planRequestTimes.Dequeue();
            if (planRequestTimes.Count >= limit || now - lastPlanRequestAt < 60d / limit)
            { Status = "plan_rate_limited"; return false; }
            return true;
        }

        private Color32[] SampleFrame()
        {
            const int width = 96, height = 64;
            Color32[] pixels = readback.GetPixels32();
            var result = new Color32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    result[y * width + x] = pixels[(y * readback.height / height) * readback.width + x * readback.width / width];
            return result;
        }

        internal static bool VisuallyChanged(Color32[] before, Color32[] after)
        {
            if (before == null || after == null || before.Length != after.Length) return true;
            int changes = 0;
            for (int i = 0; i < after.Length; i++)
            {
                var a = before[i]; var b = after[i];
                if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 60 && ++changes >= 8) return true;
            }
            return false;
        }

        private bool PlanFrameChanged()
        {
            var samples = SampleFrame();
            bool changed = !haveRequestedFrame || planOutcomePending ||
                (boundActor.CameraFocus - lastRequestedPosition).sqrMagnitude > .075f * .075f ||
                VisuallyChanged(requestedSamples, samples);
            if (!changed)
            {
                SuppressedObservations++;
                Status = "waiting_for_visual_change";
                nextObservation = Time.unscaledTime + Mathf.Clamp(localObservationSeconds, .2f, 5f);
                return false;
            }
            requestedSamples = samples;
            haveRequestedFrame = true;
            lastRequestedPosition = boundActor.CameraFocus;
            planOutcomePending = false;
            return true;
        }

        private void ReservePlanRequest()
        {
            PlanRequests++;
            lastPlanRequestAt = Time.realtimeSinceStartupAsDouble;
            planRequestTimes.Enqueue(lastPlanRequestAt);
            pendingPlanPosition = boundActor.CameraFocus;
            Vector3 v = gameCamera.WorldToViewportPoint(pendingPlanPosition);
            pendingLeft = gameCamera.ViewportToWorldPoint(new Vector3(0, v.y, v.z));
            pendingRight = gameCamera.ViewportToWorldPoint(new Vector3(1, v.y, v.z));
        }

        private string MailboxPath(string suffix) => Path.Combine(planTestDirectory,
            pending.session_id + "-" + pending.frame_id.ToString("D4") + "." + suffix);

        private void ExportPlanRequest()
        {
            try
            {
                File.WriteAllBytes(MailboxPath("jpg"), lastJpeg);
                File.WriteAllText(MailboxPath("request.json"), JsonUtility.ToJson(pending, true));
                mailboxCapturedTime = Time.fixedTimeAsDouble;
                Status = "awaiting_terra_substitute";
#if UNITY_EDITOR
                // A file-based substitute is coordinated by a human/agent, not an HTTP server.
                // Freeze only this explicit test transport so the screenshot remains current.
                UnityEditor.EditorApplication.isPaused = true;
#endif
            }
            catch (Exception ex) { CancelPending(); FailTransport("mailbox_write_" + ex.GetType().Name); }
        }

        private void PollPlanMailbox()
        {
            if (PendingObservationAge > Mathf.Clamp(requestTimeoutSeconds, 2, 90))
            { CancelPending(); FailTransport("substitute_response_expired"); return; }
            string file = MailboxPath("response.json");
            if (!File.Exists(file)) return;
            var request = pending;
            VisionResponse response = null;
            try
            {
                if (new FileInfo(file).Length <= MaxResponseBytes)
                    response = JsonUtility.FromJson<VisionResponse>(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException) { }
            Accept(request, response, false);
        }

        internal static bool ValidPlan(VisionPlan plan)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.objective) || plan.objective.Length > 160 ||
                string.IsNullOrWhiteSpace(plan.expected_result) || plan.expected_result.Length > 160 ||
                plan.steps == null || plan.steps.Length < 1 || plan.steps.Length > 4) return false;
            float total = 0;
            for (int index = 0; index < plan.steps.Length; index++)
            {
                var s = plan.steps[index];
                if (s == null || float.IsNaN(s.screen_x) || float.IsInfinity(s.screen_x) || s.screen_x < 0 || s.screen_x > 1 ||
                    float.IsNaN(s.timeout_seconds) || s.timeout_seconds < .2f || s.timeout_seconds > 8f) return false;
                if (s.kind != JumpOptionKind && !string.IsNullOrEmpty(s.option_id)) return false;
                if (s.kind == "move_to") { if (s.horizontal != 0) return false; }
                else if (s.kind == "jump") { if ((s.horizontal != -1 && s.horizontal != 1) || s.timeout_seconds < MaxJumpSeconds) return false; }
                else if (s.kind == JumpOptionKind)
                {
                    if (index != 0 || s.horizontal != 0 || s.screen_x != 0 || s.timeout_seconds < MaxJumpSeconds ||
                        string.IsNullOrEmpty(s.option_id) || s.option_id.Length > 82) return false;
                }
                else if (s.kind == "wait") { if (s.horizontal != 0) return false; }
                else if (IsVisualHold(s.kind))
                {
                    if (s.horizontal != 0 || s.screen_x != 0 || index != plan.steps.Length - 1) return false;
                }
                else return false;
                total += s.timeout_seconds;
            }
            return total <= 20f;
        }

        private bool AcceptPlan(VisionRequest request, VisionResponse response, bool fixture)
        {
            string expectedSource = fixture ? "fixture" : PlanMailboxActive ? "substitute:gpt-6.1-terra" : "model";
            if (response.source != expectedSource || !ValidPlan(response.plan)) return Reject("invalid_plan_response");
            string finalKind = response.plan.steps[response.plan.steps.Length - 1].kind;
            if (IsVisualHold(finalKind) && (!(request is VisionVisualHoldRequest visualRequest) ||
                Array.IndexOf(visualRequest.visual_hold_events, finalKind) < 0))
                return Reject("visual_hold_event_not_available");
            if ((boundActor.CameraFocus - pendingPlanPosition).sqrMagnitude > .15f * .15f)
                return Reject("plan_observation_moved");
            var planRequest = request as VisionPlanRequest;
            var firstStep = response.plan.steps[0];
            VisionJumpOption selected = null;
            if (firstStep.kind == JumpOptionKind)
            {
                if (planRequest == null || !planRequest.jump_option_ids ||
                    (selected = Array.Find(planRequest.jump_options, option => option.option_id == firstStep.option_id)) == null)
                    return Reject("jump_option_not_in_observation");
            }
            if (planRequest != null && planRequest.jump_option_ids &&
                Array.Exists(response.plan.steps, step => step.kind == "jump"))
                return Reject("jump_requires_current_option_id");
            activePlan = response.plan;
            selectedJumpOption = selected;
            if (selected != null)
                jumpOptionTakeoffX = Mathf.Lerp(pendingLeft.x, pendingRight.x, selected.takeoff_screen_x);
            LastPlanSource = expectedSource;
            planRequestId = request.request_id; planSessionId = request.session_id; planFrameId = request.frame_id;
            planTargets = new float[activePlan.steps.Length];
            for (int i = 0; i < planTargets.Length; i++) planTargets[i] = Vector3.Lerp(pendingLeft, pendingRight, activePlan.steps[i].screen_x).x;
            planStep = 0; planSeconds = 0; planMoved = false;
            planStepResults.Clear();
            ResetPlanStep();
            if (response.progress_memory != null)
            {
                planProgress = new VisionProgressMemory
                {
                    observed_frame_id = request.frame_id,
                    observations = BoundedMemory(response.progress_memory.observations, 3),
                    hypotheses = BoundedMemory(response.progress_memory.hypotheses, 2)
                };
            }
            failures = 0;
            if (fixture) FixtureDecisionsAccepted++;
            else if (PlanMailboxActive) SubstitutePlansAccepted++;
            else { ModelPlansAccepted++; ModelDecisionsAccepted++; }
            AuditPlan("plan_accepted", activePlan.objective + " | expected: " + activePlan.expected_result);
            if (selected != null) AuditPlan("jump_option_selected", selected.option_id);
            Status = "executing_short_plan";
            return true;
        }

        private void TickPlan()
        {
            if (activePlan == null) return;
            if (planSeconds > 20.5f) { FailPlan("plan_timeout"); return; }
            var step = activePlan.steps[planStep];
            bool grounded = boundActor.IsGrounded;
            Vector3 position = boundActor.CameraFocus;
            if (stepStarted && (position - stepStart).sqrMagnitude > .075f * .075f) planMoved = true;
            if (step.kind == "move_to")
            {
                if (!grounded) { FailPlan("walk_lost_ground"); return; }
                float dx = planTargets[planStep] - position.x;
                if (Mathf.Abs(dx) <= MoveArrivalTolerance)
                {
                    // A movement must actually start and displace the actor. Being at the
                    // requested target already is not evidence that the new plan succeeded.
                    if (!stepStarted || Mathf.Abs(position.x - stepStart.x) < .001f)
                    { FailPlan("move_target_already_reached_no_progress; reassess latest image or use wait"); return; }
                    FinishPlanStep("target_x_reached_grounded_after_movement"); return;
                }
                if (stepSeconds >= step.timeout_seconds) { FailPlan("move_timeout"); return; }
                if (!DrivePlanWalk(dx, position)) return;
            }
            else if (step.kind == "jump")
            {
                if (!stepStarted && !grounded) { FailPlan("jump_requires_ground"); return; }
                if (stepStarted)
                {
                    stepAirborne |= !grounded;
                    if (stepAirborne && grounded)
                    {
                        float screenError = Mathf.Abs(position.x - planTargets[planStep]) / Mathf.Max(.01f, Mathf.Abs(pendingRight.x - pendingLeft.x));
                        if (screenError > .08f) { FailPlan("landed_outside_expected_region"); return; }
                        FinishPlanStep("grounded_in_expected_landing_region"); return;
                    }
                    if (stepSeconds >= Mathf.Min(MaxJumpSeconds, step.timeout_seconds)) { FailPlan("jump_landing_timeout"); return; }
                }
                if (!ApplyPlanStep(step.horizontal, !stepStarted)) return;
            }
            else if (step.kind == JumpOptionKind)
            {
                if (!TickJumpOption(step, position, grounded)) return;
            }
            else if (IsVisualHold(step.kind))
            {
                if (!TickVisualHold(step, grounded, position)) return;
            }
            else
            {
                if (stepSeconds >= step.timeout_seconds) { FinishPlanStep("wait_elapsed_not_mechanism_verified"); return; }
                if (!ApplyPlanStep(0, false)) return;
            }
            stepLastPosition = position;
            stepSeconds += Time.fixedDeltaTime;
            planSeconds += Time.fixedDeltaTime;
        }

        private bool DrivePlanWalk(float dx, Vector3 position)
        {
            if (stepStarted && Mathf.Abs(position.x - stepLastPosition.x) < .001f) stepStillSeconds += Time.fixedDeltaTime;
            else stepStillSeconds = 0;
            if (stepStillSeconds >= .6f) { FailPlan("movement_stuck"); return false; }
            float movement = Mathf.Clamp(dx / (Mathf.Max(.01f, boundActor.MovementSpeed) * Time.fixedDeltaTime), -1f, 1f);
            return ApplyPlanStep(movement, false);
        }

        private bool ApplyPlanStep(float horizontal, bool jump)
        {
            int direction = horizontal > 0 ? 1 : horizontal < 0 ? -1 : 0;
            if (!boundActor.CheckControlSafety(direction, jump, out string reason))
            {
                if (stepStarted) LocalCorrections++; else LocalVetoes++;
                AuditPlan(stepStarted ? "local_correction" : "local_veto", reason);
                FailPlan("safety: " + reason); return false;
            }
            if (!boundActor.ApplyPlanControl(this, horizontal, jump)) { FailPlan("motor_control_rejected"); return false; }
            if (!stepStarted)
            {
                stepStarted = true; stepStart = boundActor.CameraFocus; stepLastPosition = stepStart;
                PlanStepsApplied++;
                previous = new VisionAction { horizontal = direction, jump = jump, duration_seconds = .05f };
                AuditPlan("step_started", activePlan.steps[planStep].kind);
            }
            previous.duration_seconds = Mathf.Clamp(stepSeconds, .05f, 1f);
            return true;
        }

        private void FinishPlanStep(string detail)
        {
            boundActor.StopControl(this);
            AuditPlan("step_completed", detail);
            RecordStepOutcome("completed", detail);
            planStep++;
            selectedJumpOption = null;
            if (planStep >= activePlan.steps.Length)
            {
                PlansCompleted++;
                if (planMoved) consecutivePlanFailures = 0;
                LastPlanResult = "completed: physical step checks passed; verify the intended visual effect in the next image";
                AuditPlan("plan_completed", LastPlanResult);
                SavePlanOutcome("completed", "physical_steps_passed");
                activePlan = null;
                // Consume exactly one follow-up observation for this outcome, even when
                // pixels are unchanged. Rate, total budget and failure gates still run first.
                planOutcomePending = true;
                Status = "plan_completed";
                return;
            }
            ResetPlanStep();
        }

        private void ResetPlanStep()
        {
            stepStarted = false; stepAirborne = false; stepSeconds = 0; stepStillSeconds = 0;
            nextHoldSample = 0; matchedHoldSamples = 0;
            jumpOptionLaunched = false; jumpOptionLaunchTime = 0;
        }

        private void FailPlan(string reason)
        {
            PlansFailed++; consecutivePlanFailures++; LocalReplans++;
            LastPlanResult = "failed: " + Limit(reason, 140);
            AuditPlan("plan_failed", LastPlanResult);
            RecordStepOutcome("failed", Limit(reason, 140));
            SavePlanOutcome("failed", Limit(reason, 140));
            activePlan = null;
            selectedJumpOption = null;
            boundActor.StopControl(this);
            planOutcomePending = true;
            Status = consecutivePlanFailures >= maxConsecutivePlanFailures ? "plan_failure_limit" : "plan_failed";
        }

        private void RecordStepOutcome(string status, string detail)
        {
            Vector3 end = boundActor.CameraFocus;
            Vector3 start = stepStarted ? stepStart : end;
            planStepResults.Add(new VisionPlanStepOutcome
            {
                status = status, detail = detail, started = stepStarted,
                start_x = start.x, start_y = start.y, end_x = end.x, end_y = end.y,
                end_grounded = boundActor.IsGrounded, elapsed_seconds = stepSeconds
            });
        }

        private void SavePlanOutcome(string status, string detail)
        {
            lastPlanOutcome = new VisionPlanOutcome
            {
                frame_id = planFrameId, status = status, detail = detail, plan = activePlan,
                step_results = planStepResults.ToArray(), cancelled_steps = activePlan.steps.Length - planStepResults.Count
            };
        }

        private void PlanTransportFailed(string reason)
        {
            consecutivePlanFailures++;
            planOutcomePending = true;
            LastPlanResult = "transport_failed: " + Limit(reason, 130);
        }

        private void StopPlan(string reason)
        {
            if (activePlan != null) AuditPlan("plan_cancelled", reason);
            activePlan = null;
            selectedJumpOption = null;
            if (ownsControl && boundActor != null) boundActor.StopControl(this);
        }

        private void AuditPlan(string kind, string detail)
        {
            if (planAudit.Count >= 512) planAudit.RemoveAt(0);
            planAudit.Add(new PlanAuditEntry { kind = kind, source = LastPlanSource, request_id = planRequestId,
                session_id = planSessionId, frame_id = planFrameId, step_index = planStep,
                time = Time.unscaledTime, position = boundActor != null ? boundActor.CameraFocus : Vector3.zero, detail = detail });
        }
    }
}
