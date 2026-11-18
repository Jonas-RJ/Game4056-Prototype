using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AutonomousCompanion.Vision;
using UnityEngine;

namespace AutonomousCompanion.Demo.Testing
{
    /// <summary>Drives the human's side of Ridge Relay during a live vision run.</summary>
    [DefaultExecutionOrder(-104)]
    public sealed class LivePlanPlaytestHarness : MonoBehaviour
    {
        private const float FirstTakeoff = -3.9f;
        private const float FirstLanding = -1.4f;
        private const float SecondTakeoff = 3.4f;
        private const float SecondLanding = 5.9f;
        private const float FloorY = .65f;
        private const float RidgeY = 1.35f;
        private const float PositionTolerance = .06f;
        private static readonly WaitForFixedUpdate PhysicsTick = new WaitForFixedUpdate();

        public DemoLevel level;
        public VisionAgent agent;
        public string endpointOverride = "";
        public string evidenceDirectory;
        private bool hasRun;
        private double lastPhysicalProgress;
        private Vector2 progressPlayer, progressCompanion;
        [Range(20, 240)] public float timeoutSeconds = 240f;
        [Range(10, 120)] public int maxModelActions = 80;
        public bool IsRunning => report.status == "running";
        public string ResultJson => JsonUtility.ToJson(report);

        [Serializable]
        private sealed class Sample
        {
            public float t;
            public Vector2 player, companion;
            public int local_plan_steps, requests;
            public string status;
            public bool door_a_open, door_b_latched;
        }

        [Serializable]
        private sealed class Report
        {
            public string status = "idle", phase = "idle", failure = "", source = "deepseek_live_short_plan";
            public string goal, endpoint, session_id, agent_status, last_model_reason;
            public bool level_completed, player_alive, companion_alive, player_exit, companion_exit;
            public bool ai_opened_a, human_latched_b, human_first_jump, human_second_jump;
            public bool exclusive_vision_control, completed_with_deepseek_plans;
            public int camera_frames, requests, local_plan_steps, fixture_actions, fallbacks, rejected_responses;
            public int model_decisions, local_vetoes, local_corrections, local_replans;
            public VisionAgent.AuditEntry[] control_audit = Array.Empty<VisionAgent.AuditEntry>();
            public int image_width, image_height, image_bytes;
            public int paid_http_requests, substitute_plans, plans_completed, plans_failed, suppressed_observations;
            public VisionAgent.PlanAuditEntry[] plan_audit;
            public float elapsed_seconds, wall_elapsed_seconds, companion_furthest_x, companion_peak_y;
            public Vector2 player_position, companion_position;
            public string[] memory = Array.Empty<string>();
            public List<string> events = new List<string>();
            public List<Sample> trajectory = new List<Sample>();
        }

        private Report report = new Report();
        private RelayManualControls manualControls;
        private bool restoreManual, restoreKeyboard, configured;
        private string restoreEndpoint;
        private string observedSession;
        private int startFrames, startRequests, startModelActions, startFixtureActions, startFallbacks, startRejects;
        private double started, wallStarted, lastModelProgress, nextSample;
        private string lastFailureStatus;
        private int lastSampledActions;
        private int startDecisions, startVetoes, startCorrections, startReplans;
        private IReadOnlyList<string> lastMemory;

        public void BeginRun()
        {
            if (IsRunning || hasRun) return;
            hasRun = true;
            StopAllCoroutines();
            report = new Report { status = "running", phase = "setup" };
            started = Time.fixedTimeAsDouble;
            wallStarted = Time.realtimeSinceStartupAsDouble;
            if (level == null) level = GetComponent<DemoLevel>();
            if (agent == null) agent = GetComponent<VisionAgent>();
            manualControls = level != null ? level.GetComponent<RelayManualControls>() : null;
            if (!Application.isPlaying || !isActiveAndEnabled || level == null || agent == null || manualControls == null ||
                level.gameObject.scene.name != "CoopVisionDemo" || level.player == null || level.companion == null ||
                level.switchA == null || level.switchA.trigger == null || level.switchB == null ||
                level.switchB.arrivalNode == null || level.doorA == null || level.doorB == null ||
                level.playerExit == null || level.playerExit.arrivalNode == null || level.companionExit == null ||
                !(agent.actor is DemoVisionActorAdapter adapter) || adapter.level != level)
            { Finish("failed", "Open CoopVisionDemo in Play Mode with the live agent and relay references assigned."); return; }
            if ((!agent.useShortPlans || !string.IsNullOrEmpty(agent.PlanMailboxDirectory) || !agent.allowPaidPlanRequests) || agent.FixtureResponder != null || LegacyControllerActive() || level.companion.allowKeyboard ||
                agent.planRequestsPerMinute != 5 || agent.maxPlanRequests != 20 || agent.maxConsecutivePlanFailures != 1 || agent.PlanRequests != 0 ||
                string.IsNullOrEmpty(evidenceDirectory) || !Path.IsPathRooted(evidenceDirectory) || !Directory.Exists(evidenceDirectory) || File.Exists(Path.Combine(evidenceDirectory, "unity-run-started.json")))
            { Finish("failed", "A fixture, legacy controller, or companion keyboard controller is active."); return; }

            File.WriteAllText(Path.Combine(evidenceDirectory, "unity-run-started.json"), "{\"started\":true,\"automatic_reruns\":0,\"max_requests\":20,\"requests_per_minute\":5,\"failure_limit\":1}");
            restoreManual = manualControls.InputEnabled;
            restoreKeyboard = level.player.allowKeyboard;
            restoreEndpoint = agent.endpoint;
            agent.enabled = false;
            manualControls.InputEnabled = false;
            level.player.allowKeyboard = false;
            level.ResetLevel();
            configured = true;
            if (!string.IsNullOrWhiteSpace(endpointOverride)) agent.endpoint = endpointOverride;
            report.endpoint = agent.endpoint;
            report.goal = agent.goal;
            report.companion_furthest_x = level.companion.Position.x;
            report.companion_peak_y = level.companion.Position.y;
            startFrames = agent.FramesCaptured;
            startRequests = agent.PlanRequests;
            startModelActions = agent.PlanStepsApplied;
            startFixtureActions = agent.FixtureActionsApplied;
            startFallbacks = agent.FallbackResponses;
            startRejects = agent.RejectedResponses;
            startDecisions = agent.ModelDecisionsAccepted;
            startVetoes = agent.LocalVetoes;
            startCorrections = agent.LocalCorrections;
            startReplans = agent.LocalReplans;
            lastModelProgress = started;
            lastPhysicalProgress = Time.realtimeSinceStartupAsDouble;
            progressPlayer = level.player.Position; progressCompanion = level.companion.Position;
            nextSample = started;
            lastSampledActions = 0;
            lastFailureStatus = null;
            lastMemory = null;

            agent.enabled = true;
            observedSession = agent.SessionId;
            report.session_id = observedSession;
            StartCoroutine(Run());
        }

        private void Update()
        {
            if (!IsRunning || !configured) return;
            Refresh();
            double now = Time.fixedTimeAsDouble;
            if ((level.player.Position - progressPlayer).sqrMagnitude > .075f * .075f ||
                (level.companion.Position - progressCompanion).sqrMagnitude > .075f * .075f)
            {
                lastPhysicalProgress = Time.realtimeSinceStartupAsDouble;
                progressPlayer = level.player.Position; progressCompanion = level.companion.Position;
            }
            if (now >= nextSample)
            {
                nextSample = now + 3f;
                if (report.trajectory.Count >= 80) report.trajectory.RemoveAt(0);
                report.trajectory.Add(new Sample { t = report.elapsed_seconds, player = report.player_position,
                    companion = report.companion_position, local_plan_steps = report.local_plan_steps, requests = report.requests,
                    status = report.agent_status, door_a_open = level.doorA.IsOpen, door_b_latched = level.doorB.Latched });
            }

            if (report.local_plan_steps != lastSampledActions)
            {
                lastSampledActions = report.local_plan_steps;
                lastModelProgress = now;
                lastFailureStatus = null;
            }
            if (IsTransportFailure(report.agent_status) && report.agent_status != lastFailureStatus)
            {
                lastFailureStatus = report.agent_status;
                Event("transport:" + lastFailureStatus);
            }

            if (!report.player_alive || !report.companion_alive)
                Finish("failed", report.player_alive ? "Companion fell or died." : "Human fell or died.");
            else if (manualControls.InputEnabled || LegacyControllerActive() || level.companion.allowKeyboard ||
                agent.FixtureResponder != null || report.fixture_actions != 0)
                Finish("failed", "A controller other than the live vision agent became active.");
            else if (!agent.isActiveAndEnabled || observedSession != agent.SessionId)
                Finish("failed", "The live vision session stopped or reset during the run.");
            else if (report.local_plan_steps > 0 && (!agent.OwnsCompanionControl || !level.companion.HasAutonomousControl))
                Finish("failed", "The vision agent lost exclusive companion control.");
            else if (report.plans_failed > 0 || report.fallbacks > 0 || report.rejected_responses > 0 || report.local_vetoes > 0 ||
                agent.LastPlanResult.StartsWith("transport_failed:", StringComparison.Ordinal) || lastFailureStatus != null)
                Finish("failed", "First failure: " + agent.LastPlanResult + " | " + report.last_model_reason);
            else if (level.Completed)
                VerifyCompletion();
            else if (report.agent_status == "plan_budget_exhausted" || report.agent_status == "plan_failure_limit")
                Finish("failed", report.agent_status);
            else if (report.wall_elapsed_seconds >= timeoutSeconds || report.local_plan_steps >= maxModelActions)
                Finish("failed", "Live test budget exhausted before both exits were occupied.");
            else if (Time.realtimeSinceStartupAsDouble - lastPhysicalProgress >= 30d)
                Finish("failed", "No physical progress by either actor for 30 wall-clock seconds.");
        }

        private IEnumerator Run()
        {
            SetPhase("waiting_for_companion_pressure_plate");
            yield return WaitFor(CompanionPhysicallyOpensA);
            if (!IsRunning) yield break;
            report.ai_opened_a = true;
            Event("vision companion physically opened gate A");

            yield return MoveHuman(FirstTakeoff, "human_first_takeoff", 20f);
            if (!IsRunning) yield break;
            yield return JumpHuman(FirstLanding, RidgeY, "human_first_gap");
            if (!IsRunning) yield break;
            report.human_first_jump = true;

            yield return MoveHuman(level.switchB.arrivalNode.transform.position.x, "human_latches_b");
            if (!IsRunning) yield break;
            yield return WaitFor(() => level.switchB.Latched && level.doorA.Latched && level.doorB.Latched);
            if (!IsRunning) yield break;
            report.human_latched_b = true;
            Event("human occupancy latched both gates");

            yield return MoveHuman(SecondTakeoff, "human_second_takeoff");
            if (!IsRunning) yield break;
            yield return JumpHuman(SecondLanding, FloorY, "human_second_gap");
            if (!IsRunning) yield break;
            report.human_second_jump = true;

            yield return MoveHuman(level.playerExit.arrivalNode.transform.position.x, "human_own_exit");
            if (!IsRunning) yield break;
            SetPhase("waiting_for_companion_exit");
            yield return WaitFor(() => level.Completed);
        }

        private bool CompanionPhysicallyOpensA()
        {
            var body = level.companion.GetComponent<Collider2D>();
            return agent.OwnsCompanionControl && agent.PlanStepsApplied > startModelActions &&
                body != null && body.Distance(level.switchA.trigger).isOverlapped && level.switchA.Occupied &&
                level.doorA.IsOpen && !level.doorA.Latched;
        }

        private IEnumerator MoveHuman(float x, string phase, float timeout = 4f)
        {
            SetPhase(phase);
            var human = level.player;
            double deadline = Time.fixedTimeAsDouble + timeout;
            while (IsRunning && Mathf.Abs(human.Position.x - x) > PositionTolerance)
            {
                if (Time.fixedTimeAsDouble >= deadline)
                { Finish("failed", $"Human could not reach x={x:F2}; gate A open={level.doorA.IsOpen}."); yield break; }
                human.SetTestInput(InputTowards(human, x), false);
                yield return PhysicsTick;
            }
            if (!IsRunning) yield break;
            human.SetTestInput(0f, false);
            yield return PhysicsTick;
            yield return PhysicsTick;
            if (IsRunning && (!human.IsGrounded || Mathf.Abs(human.Position.x - x) > PositionTolerance + .02f))
                Finish("failed", $"Human did not arrive grounded at x={x:F2}.");
        }

        private IEnumerator JumpHuman(float x, float landingY, string phase)
        {
            SetPhase(phase);
            var human = level.player;
            if (!human.IsGrounded) { Finish("failed", "Human was not grounded at takeoff."); yield break; }
            Vector2 takeoff = human.Position;
            float peak = takeoff.y;
            double deadline = Time.fixedTimeAsDouble + 2.5f;
            human.SetTestInput(InputTowards(human, x), true);
            yield return PhysicsTick;
            while (IsRunning && Time.fixedTimeAsDouble < deadline)
            {
                Vector2 p = human.Position;
                peak = Mathf.Max(peak, p.y);
                if (human.IsGrounded && Mathf.Abs(p.x - x) <= PositionTolerance &&
                    Mathf.Abs(p.y - landingY) < .15f && peak - takeoff.y > .5f)
                {
                    human.SetTestInput(0f, false);
                    Event($"human jump landed at {p:F2}, peak y={peak:F2}");
                    yield return PhysicsTick;
                    yield return PhysicsTick;
                    yield break;
                }
                human.SetTestInput(InputTowards(human, x), false);
                yield return PhysicsTick;
            }
            if (IsRunning) Finish("failed", $"Human missed landing at x={x:F2}, y={landingY:F2}.");
        }

        private IEnumerator WaitFor(Func<bool> condition)
        {
            while (IsRunning && !condition()) yield return PhysicsTick;
        }

        private static float InputTowards(DemoActor human, float x) =>
            Mathf.Clamp((x - human.Position.x) / (human.moveSpeed * Time.fixedDeltaTime), -1f, 1f);

        private bool LegacyControllerActive()
        {
            var brain = level.brain;
            return brain != null && (brain.isActiveAndEnabled ||
                (brain.executor != null && brain.executor.isActiveAndEnabled) ||
                (brain.client != null && (brain.client.isActiveAndEnabled || brain.client.IsBusy)));
        }

        private static bool IsTransportFailure(string status) =>
            !string.IsNullOrEmpty(status) && (status.StartsWith("backend_", StringComparison.Ordinal) ||
                status.StartsWith("camera_capture_", StringComparison.Ordinal) ||
                status.StartsWith("invalid_", StringComparison.Ordinal) ||
                status == "response_correlation_mismatch" || status == "companion_control_unavailable");

        private void VerifyCompletion()
        {
            report.completed_with_deepseek_plans = report.level_completed && report.player_alive && report.companion_alive &&
                report.player_exit && report.companion_exit && report.ai_opened_a && report.human_latched_b &&
                report.camera_frames > 0 && report.requests > 0 && report.local_plan_steps > 0 &&
                report.fixture_actions == 0 && agent.NetworkRequests > 0 && agent.ModelPlansAccepted > 0 && agent.SubstitutePlansAccepted == 0 && report.exclusive_vision_control;
            if (report.completed_with_deepseek_plans)
            {
                SetPhase("both_assigned_exits");
                Finish("passed", "");
            }
            else Finish("failed", "Completion occurred without the required live vision and physical cooperation evidence.");
        }

        private void SetPhase(string phase)
        {
            report.phase = phase;
            Event(phase);
        }

        private void Event(string message)
        {
            if (report.events.Count >= 40) report.events.RemoveAt(0);
            report.events.Add($"{Time.fixedTimeAsDouble - started:F2} {message}");
        }

        private void Refresh()
        {
            report.elapsed_seconds = (float)(Time.fixedTimeAsDouble - started);
            report.wall_elapsed_seconds = (float)(Time.realtimeSinceStartupAsDouble - wallStarted);
            report.camera_frames = agent.FramesCaptured - startFrames;
            report.requests = agent.PlanRequests - startRequests;
            report.local_plan_steps = agent.PlanStepsApplied - startModelActions;
            report.fixture_actions = agent.FixtureActionsApplied - startFixtureActions;
            report.fallbacks = agent.FallbackResponses - startFallbacks;
            report.rejected_responses = agent.RejectedResponses - startRejects;
            report.model_decisions = agent.ModelPlansAccepted;
            report.paid_http_requests = agent.NetworkRequests;
            report.substitute_plans = agent.SubstitutePlansAccepted;
            report.plans_completed = agent.PlansCompleted;
            report.plans_failed = agent.PlansFailed;
            report.suppressed_observations = agent.SuppressedObservations;
            report.local_vetoes = agent.LocalVetoes - startVetoes;
            report.local_corrections = agent.LocalCorrections - startCorrections;
            report.local_replans = agent.LocalReplans - startReplans;
            report.image_width = agent.LastFrameWidth;
            report.image_height = agent.LastFrameHeight;
            report.image_bytes = agent.LastFrameBytes;
            report.agent_status = agent.Status;
            report.last_model_reason = agent.LastResponseReason;
            if (!ReferenceEquals(lastMemory, agent.Memory))
            {
                lastMemory = agent.Memory;
                report.memory = new List<string>(lastMemory).ToArray();
            }
            report.player_alive = level.player.IsAlive;
            report.companion_alive = level.companion.IsAlive;
            report.player_exit = level.playerExit.Occupied;
            report.companion_exit = level.companionExit.Occupied;
            report.level_completed = level.Completed;
            report.player_position = level.player.Position;
            report.companion_position = level.companion.Position;
            report.companion_furthest_x = Mathf.Max(report.companion_furthest_x, report.companion_position.x);
            report.companion_peak_y = Mathf.Max(report.companion_peak_y, report.companion_position.y);
            report.exclusive_vision_control = agent.OwnsCompanionControl && level.companion.HasAutonomousControl &&
                !manualControls.InputEnabled && !LegacyControllerActive();
        }

        private void Finish(string status, string failure)
        {
            if (!IsRunning) return;
            if (configured) { Refresh(); report.plan_audit = agent.GetPlanAuditTrail(); report.control_audit = Array.FindAll(agent.GetAuditTrail(), item => item.session_id == observedSession); }
            report.status = status;
            report.failure = failure;
            if (configured)
            {
                configured = false;
                agent.enabled = false;
                agent.allowPaidPlanRequests = false;
                agent.endpoint = restoreEndpoint;
                level.player.ClearTestInput();
                level.player.StopLocomotion();
                level.player.allowKeyboard = restoreKeyboard;
                manualControls.InputEnabled = restoreManual;
            }
            if (agent != null) { agent.enabled = false; agent.allowPaidPlanRequests = false; }
            if (!string.IsNullOrEmpty(evidenceDirectory) && Directory.Exists(evidenceDirectory))
                File.WriteAllText(Path.Combine(evidenceDirectory, "unity-result.json"), ResultJson);
            if (status == "passed") Debug.Log("DEEPSEEK_PLAN_PLAYTEST " + ResultJson, this);
            else Debug.LogWarning("DEEPSEEK_PLAN_PLAYTEST " + ResultJson, this);
        }

        private void OnDisable()
        {
            if (IsRunning) Finish("cancelled", "Live test harness disabled.");
        }
    }
}
