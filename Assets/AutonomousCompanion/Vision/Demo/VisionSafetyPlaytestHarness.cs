using System;
using System.Collections;
using System.Collections.Generic;
using AutonomousCompanion.Vision;
using UnityEngine;

namespace AutonomousCompanion.Demo.Testing
{
    /// <summary>Offline regression fixture for local vision safeguards and their physical effects.</summary>
    public sealed class VisionSafetyPlaytestHarness : MonoBehaviour
    {
        private const float OriginX = 100f;
        private const float StandingY = .65f;
        private const float MaxSupportedOverhang = .25f; // Half the motor's ground-sensor width.
        private static readonly WaitForFixedUpdate PhysicsTick = new WaitForFixedUpdate();

        [Serializable]
        private sealed class Report
        {
            public string status = "idle", source = "scripted_regression_fixture";
            public bool proves_model_autonomy = false;
            public int fixture_decisions, fixture_actions, local_vetoes, local_corrections, local_replans;
            public int model_actions, network_requests;
            public float drop_stop_relative_x;
            public List<TestResult> cases = new List<TestResult>();
        }

        [Serializable]
        private sealed class TestResult
        {
            public string name;
            public List<string> failures = new List<string>();
            public string last_plan_result;
            public int plan_requests, plans_failed, plans_completed, plan_steps;
            public bool actor_alive, actor_grounded;
            public VisionAgent.PlanAuditEntry[] plan_audit;
        }

        public bool IsRunning => report.status == "running";
        public string ResultJson => JsonUtility.ToJson(report);

        private Report report = new Report();
        private TestResult current;
        private GameObject arena;
        private DemoLevel level;
        private DemoVisionActorAdapter adapter;
        private VisionAgent agent;
        private VisionRequest request;
        private bool dynamicValidation;

        public void BeginDynamicValidation()
        {
            if (IsRunning || report.cases.Count != 0) throw new InvalidOperationException("This harness already ran.");
            dynamicValidation = true;
            report = new Report { status = "running" };
            StartCoroutine(RunDynamic());
        }

        private IEnumerator RunDynamic()
        {
            foreach (string mode in new[] { "approach", "direct", "stale", "support", "moved",
                "obstacle", "air_replaced", "air_moved", "air_removed" })
            {
                yield return RunCase("dynamic_jump_option_" + mode, () => JumpOptions(mode));
                if (current.failures.Count != 0) break; // First unexpected assertion failure stops the suite.
            }
            report.status = report.cases.Exists(test => test.failures.Count != 0) ? "failed" : "passed";
            LogReport();
        }

        public void BeginOfflineTest()
        {
            if (IsRunning) return;
            report = new Report { status = "running" };
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            yield return RunCase("flat_walk_and_exclusive_control", FlatWalk);
            yield return RunCase("drop_stops_and_reobserves", DropAndReobserve);
            yield return RunCase("lost_support_interrupts_walk", LostSupport);
            yield return RunCase("reachable_jump_and_in_flight_control", ReachableJump);
            yield return RunCase("jump_option_executes_approach_and_crosses_raised_gap", () => JumpOptions("approach"));
            yield return RunCase("jump_option_from_current_skips_approach", () => JumpOptions("direct"));
            yield return RunCase("jump_option_from_another_request_is_rejected_before_motion", () => JumpOptions("stale"));
            yield return RunCase("jump_option_revalidates_support_after_approach_begins", () => JumpOptions("support"));
            yield return RunCase("unreachable_jump_wall_and_ceiling", BlockedJumps);
            yield return RunCase("repeated_blocked_walk_reobserves", BlockedWalk);
            yield return RunCase("repeated_wait_reobserves", RepeatedWait);
            yield return RunCase("correlation_reset_and_death", StaleResponses);
            report.status = report.cases.Exists(test => test.failures.Count != 0) ? "failed" : "passed";
            LogReport();
        }

        private IEnumerator RunCase(string name, Func<IEnumerator> run)
        {
            current = new TestResult { name = name };
            report.cases.Add(current);
            yield return run();
            CleanupArena();
            yield return null;
        }

        private IEnumerator FlatWalk()
        {
            CreateArena(-2f);
            Platform(-10f, 10f);
            Obstacle(new Vector2(-1f, .8f), new Vector2(.2f, 2f), isTrigger: true);
            yield return StartAgent();
            if (!Check(request != null, "No initial camera observation.")) yield break;

            var other = Child("Foreign controller");
            other.SetActive(false);
            var foreign = other.AddComponent<VisionAgent>();
            Check(agent.OwnsCompanionControl && level.companion.HasAutonomousControl, "Vision did not acquire the companion motor.");
            Check(!adapter.TryAcquireControl(foreign) && !adapter.ApplyControl(foreign, -1, true), "A second owner obtained companion control.");
            float x = level.companion.Position.x;
            Vector2 playerPosition = level.player.Position;
            Deliver(request, 1, false, .36f);
            yield return WaitFor(() => agent.FixtureActionsApplied == 1 && !agent.ApplyingAction);
            Check(level.companion.Position.x - x > 1.1f && level.companion.IsGrounded, "Safe walk did not pass through the non-solid trigger.");
            Check(Mathf.Abs(level.companion.Body.linearVelocity.x) < .05f, "Timed walk failed to stop the companion.");
            Check(Vector2.Distance(level.player.Position, playerPosition) < .05f && !level.player.HasAutonomousControl,
                "Vision input affected the human motor.");
            Check(agent.LocalVetoes == 0 && agent.LocalCorrections == 0, "Guard intervened on a clear flat walk.");
            agent.enabled = false;
            agent.useShortPlans = false;
            Check(!level.companion.HasAutonomousControl, "Disabling vision left its motor claim in place.");
        }

        private IEnumerator DropAndReobserve()
        {
            CreateArena(-.16f);
            Platform(-10f, 0f);
            yield return StartAgent();
            if (!Check(request != null && level.companion.IsGrounded, "Expected a grounded observation with partial support at the edge.")) yield break;

            VisionRequest proposedFrom = request;
            Deliver(request, 1, false, .85f);
            yield return WaitFor(() => agent.LocalVetoes + agent.LocalCorrections > 0);
            if (!Check(agent.LocalVetoes + agent.LocalCorrections > 0, "Unsafe walk was never vetoed or interrupted.")) yield break;
            Vector2 stopped = level.companion.Position;
            report.drop_stop_relative_x = stopped.x - OriginX;
            bool alive = level.companion.IsAlive;
            bool grounded = level.companion.IsGrounded;
            Vector2 stoppedVelocity = level.companion.Body.linearVelocity;
            Check(alive && grounded && report.drop_stop_relative_x <= MaxSupportedOverhang && Mathf.Abs(stoppedVelocity.x) < .05f,
                $"Companion did not stop with support at the drop (alive={alive}, grounded={grounded}, " +
                $"relative x={report.drop_stop_relative_x:F3}, y={stopped.y:F3}, velocity={stoppedVelocity:F3}).");

            yield return WaitForRequest();
            if (!Check(request != null && request.frame_id > proposedFrom.frame_id, "Intervention did not produce a fresh camera observation.")) yield break;
            Check(level.companion.IsAlive && level.companion.IsGrounded && Vector2.Distance(stopped, level.companion.Position) < .03f,
                "Companion lost support while waiting for a fresh observation.");
            string reason = agent.LocalVetoes > 0 ? "local_veto" : "local_correction";
            Check(agent.LocalReplans > 0 && HasReplanEvidence(request, proposedFrom, reason), "Reobservation omitted local intervention feedback.");
            int previousDirection = agent.FixtureActionsApplied == 0 ? 0 : 1;
            Check(request.previous_action.horizontal == previousDirection && !request.previous_action.jump,
                "The next request's previous action did not match input actually applied to the motor.");

            int applied = agent.FixtureActionsApplied;
            Deliver(request, -1, false, .3f);
            yield return WaitFor(() => agent.FixtureActionsApplied > applied && !agent.ApplyingAction);
            Check(level.companion.Position.x < stopped.x - .75f && level.companion.IsGrounded,
                "A new fixture decision could not safely move away from the drop.");
        }

        private IEnumerator LostSupport()
        {
            CreateArena(-1.4f);
            Platform(-10f, 0f);
            var continuation = Platform(0f, 10f);
            yield return StartAgent();
            if (!Check(request != null, "No observation on joined platforms.")) yield break;

            Deliver(request, 1, false, .9f);
            yield return WaitFor(() => agent.FixtureActionsApplied == 1);
            if (!Check(agent.ApplyingAction, "Safe movement failed to start before geometry changed.")) yield break;
            continuation.enabled = false;
            Physics2D.SyncTransforms();
            yield return WaitFor(() => agent.LocalCorrections > 0);
            Check(agent.LocalCorrections > 0 && agent.LocalVetoes == 0 && !agent.ApplyingAction &&
                level.companion.IsGrounded && Mathf.Abs(level.companion.Body.linearVelocity.x) < .05f,
                "Changing support during a walk did not interrupt the active motor command.");
        }

        private IEnumerator ReachableJump()
        {
            CreateArena(-.55f);
            Platform(-10f, 0f);
            Platform(1.55f, 8f, .7f);
            Obstacle(new Vector2(.8f, 1.2f), new Vector2(.35f, 2.6f), isTrigger: true);
            yield return StartAgent();
            if (!Check(request != null && level.companion.IsGrounded, "Expected a grounded takeoff observation.")) yield break;
            if (!Check(adapter.CheckControlSafety(1, true, out string reason), "Guard denied reachable jump: " + reason)) yield break;

            Vector2 takeoff = level.companion.Position;
            Deliver(request, 1, true, .1f);
            yield return WaitFor(() => agent.FixtureActionsApplied == 1 && !level.companion.IsGrounded);
            if (!Check(agent.ApplyingAction && !level.companion.IsGrounded, "Safe jump did not leave the launch platform.")) yield break;
            Check(adapter.CheckControlSafety(1, false, out reason), "The guard blocked continuation of horizontal movement in flight: " + reason);
            float peak = takeoff.y;
            double deadline = Time.realtimeSinceStartupAsDouble + 3d;
            while (agent.ApplyingAction && Time.realtimeSinceStartupAsDouble < deadline)
            {
                peak = Mathf.Max(peak, level.companion.Position.y);
                yield return PhysicsTick;
            }
            Check(!agent.ApplyingAction && level.companion.IsGrounded && level.companion.IsAlive &&
                level.companion.Position.x > OriginX + 1.55f && level.companion.Position.y > takeoff.y + .55f && peak - takeoff.y > .9f,
                "A short directional jump command did not retain horizontal input through landing.");
            Check(agent.LocalVetoes == 0 && agent.LocalCorrections == 0, "Guard intervened on an unobstructed reachable jump.");
        }

        private IEnumerator BlockedJumps()
        {
            CreateArena(-.55f);
            Platform(-10f, 0f);
            var landing = Platform(1.55f, 8f);
            yield return StartAgent();
            if (!Check(request != null, "No observation for the unreachable jump test.")) yield break;
            VisionRequest proposedFrom = request;
            landing.transform.localPosition += Vector3.right * 3f;
            Physics2D.SyncTransforms();
            Deliver(request, 1, true, .1f);
            yield return WaitFor(() => agent.LocalVetoes > 0);
            Check(agent.LocalVetoes == 1 && agent.FixtureDecisionsAccepted == 1 && agent.FixtureActionsApplied == 0 && level.companion.IsGrounded,
                "A formerly reachable jump issued motor input after the landing surface moved, or failed to register a veto.");
            yield return WaitForRequest();
            Check(request != null && request.frame_id > proposedFrom.frame_id && HasReplanEvidence(request, proposedFrom, "local_veto") &&
                request.previous_action.horizontal == 0 && !request.previous_action.jump,
                "A jump veto failed to request a new observation with feedback and the previous applied action.");

            landing.transform.localPosition -= Vector3.right * 3f;
            var wall = Obstacle(new Vector2(.35f, 1.5f), new Vector2(.18f, 3f));
            Physics2D.SyncTransforms();
            Check(!adapter.CheckControlSafety(1, true, out string wallReason) && !string.IsNullOrWhiteSpace(wallReason),
                "A solid wall across a reachable arc did not block the jump.");
            wall.enabled = false;
            var ceiling = Obstacle(new Vector2(-.45f, 1.5f), new Vector2(2f, .18f));
            Physics2D.SyncTransforms();
            Check(!adapter.CheckControlSafety(1, true, out string ceilingReason) && !string.IsNullOrWhiteSpace(ceilingReason),
                "A low ceiling did not block the jump.");
            ceiling.enabled = false;
            Physics2D.SyncTransforms();
            Check(adapter.CheckControlSafety(1, true, out string reason), "Removing both solid obstacles did not clear the jump: " + reason);
        }

        private IEnumerator JumpOptions(string mode)
        {
            CreateArena(-3.9f);
            Platform(-10f, 0f);
            var landing = Platform(1.55f, 8f, .7f);
            var wall = Obstacle(new Vector2(-1.3f, 1.5f), new Vector2(.18f, 3f));
            wall.enabled = false;
            agent.useShortPlans = true;
            agent.allowPaidPlanRequests = false;
            if (dynamicValidation)
            {
                agent.planRequestsPerMinute = 5;
                agent.maxPlanRequests = 20;
                agent.maxConsecutivePlanFailures = 1;
            }
            yield return StartAgent();
            if (!Check(request is VisionPlanRequest, "No short-plan observation for jump options.")) yield break;

            var wire = JsonUtility.FromJson<VisionPlanRequest>(JsonUtility.ToJson(request));
            var immediate = Array.Find(wire.jump_options, option => option.horizontal == 1 && option.from_current);
            var approach = Array.Find(wire.jump_options, option => option.horizontal == 1 && !option.from_current);
            Camera camera = agent.gameCamera;
            Vector3 viewport = camera.WorldToViewportPoint(level.companion.Position);
            float left = camera.ViewportToWorldPoint(new Vector3(0f, viewport.y, viewport.z)).x;
            float right = camera.ViewportToWorldPoint(new Vector3(1f, viewport.y, viewport.z)).x;
            if (!Check(immediate != null && approach != null, "Expected separate immediate and approach jump options.")) yield break;
            Check(wire.jump_option_ids && immediate.option_id.StartsWith(wire.request_id + ":", StringComparison.Ordinal) &&
                approach.option_id.StartsWith(wire.request_id + ":", StringComparison.Ordinal) && immediate.option_id != approach.option_id,
                "The options must have distinct IDs belonging to the request.");
            Check(Mathf.Abs(immediate.takeoff_screen_x - wire.actor.screen_x) < .001f &&
                Mathf.Lerp(left, right, immediate.landing_screen_x) < OriginX,
                "The immediate right jump should land on the starting platform.");
            Check(Mathf.Lerp(left, right, approach.takeoff_screen_x) < OriginX &&
                Mathf.Lerp(left, right, approach.landing_screen_x) > OriginX + 1.55f &&
                approach.landing_screen_y < immediate.landing_screen_y,
                "The approach jump should start before the gap and land on the raised platform.");

            wall.enabled = true;
            Physics2D.SyncTransforms();
            Check(!Array.Exists(adapter.ObserveJumpOptions(camera, left, right),
                option => option.horizontal == 1 && !option.from_current),
                "A solid wall should remove the approach jump prediction.");
            wall.enabled = false;
            Physics2D.SyncTransforms();

            var selected = mode == "direct" ? immediate : approach;
            var reply = new VisionResponse
            {
                request_id = wire.request_id, session_id = wire.session_id, frame_id = wire.frame_id,
                source = "fixture", reason = "offline prediction fixture", memory = Array.Empty<string>(),
                progress_memory = new VisionProgressMemory(),
                plan = new VisionPlan
                {
                    objective = "Cross the raised gap", expected_result = "Stand on the raised platform",
                    steps = new[]
                    {
                        new VisionPlanStep { kind = "jump_option",
                            option_id = mode == "stale" ? "different-request:1" : selected.option_id, timeout_seconds = 5f }
                    }
                }
            };
            if (dynamicValidation && mode != "approach" && mode != "direct" && mode != "stale")
                reply.plan.steps = new[] { reply.plan.steps[0], new VisionPlanStep { kind = "wait", timeout_seconds = .2f } };
            if (mode == "stale")
            {
                float origin = level.companion.Position.x;
                Check(!agent.DeliverFixtureResponse(request, reply), "A stale option ID was accepted for the current request.");
                yield return PhysicsTick;
                Check(agent.PlanStepsApplied == 0 && agent.RejectedResponses == 1 &&
                    Mathf.Abs(level.companion.Position.x - origin) < .01f &&
                    agent.LastPlanResult.Contains("jump_option_not_in_observation"),
                    "Stale option did not stop before movement.");
                yield break;
            }
            if (!Check(agent.DeliverFixtureResponse(request, reply), "Fixture plan was rejected.")) yield break;
            if (mode == "support")
            {
                yield return WaitFor(() => agent.PlanStepsApplied > 0);
                Check(Array.Exists(agent.GetPlanAuditTrail(), item => item.kind == "jump_option_approach"),
                    "The approach must start before replacing the landing support.");
                landing.enabled = false;
                Platform(1.55f, 8f, .7f);
                Physics2D.SyncTransforms();
                yield return WaitFor(() => agent.PlansFailed > 0);
                Check(agent.PlansFailed == 1 && agent.LocalVetoes == 1 &&
                    agent.LastPlanResult.Contains("different support") && level.companion.IsGrounded &&
                    !Array.Exists(agent.GetPlanAuditTrail(), item => item.kind == "jump_option_launched"),
                    "Changed landing support was not rejected before launch: " + agent.LastPlanResult);
                if (dynamicValidation) yield return VerifyFirstFailureStop();
                yield break;
            }
            if (mode == "moved" || mode == "obstacle" || mode.StartsWith("air_", StringComparison.Ordinal))
            {
                bool airborneChange = mode.StartsWith("air_", StringComparison.Ordinal);
                if (airborneChange)
                    yield return WaitFor(() => !level.companion.IsGrounded &&
                        Array.Exists(agent.GetPlanAuditTrail(), item => item.kind == "jump_option_launched"));
                else
                    yield return WaitFor(() => Array.Exists(agent.GetPlanAuditTrail(), item => item.kind == "jump_option_approach"));
                if (!Check(agent.ExecutingPlan && (airborneChange ? !level.companion.IsGrounded : level.companion.IsGrounded),
                    "Geometry change was not applied in the intended execution phase.")) yield break;
                if (mode == "moved" || mode == "air_moved") landing.transform.localPosition += Vector3.up * .5f;
                if (mode == "obstacle") wall.enabled = true;
                if (mode == "air_replaced" || mode == "air_removed")
                {
                    landing.enabled = false;
                    // For removal use a lower catch floor: validate reporting, not fall rescue.
                    Platform(1.55f, 8f, mode == "air_replaced" ? .7f : -.5f);
                }
                Physics2D.SyncTransforms();
                yield return WaitFor(() => agent.PlansFailed > 0);
                string expected = mode == "moved" ? "jump_option_revalidation:" :
                    mode == "obstacle" ? "movement_stuck" :
                    mode == "air_replaced" ? "jump_option_landing_support:" : "jump_option_landed_outside_selected_region";
                Check(agent.PlansFailed == 1 && agent.PlansCompleted == 0 && agent.LastPlanResult.Contains(expected),
                    "Expected geometry-change failure " + expected + ", got " + agent.LastPlanResult);
                Check(Array.Exists(agent.GetPlanAuditTrail(), item => item.kind == "jump_option_launched") == airborneChange,
                    "Unexpected launch state after geometry changed.");
                Check(level.companion.IsAlive && level.companion.IsGrounded && !agent.ExecutingPlan,
                    "Fixture did not end stopped on a supporting surface.");
                if (dynamicValidation) yield return VerifyFirstFailureStop();
                yield break;
            }
            yield return WaitFor(() => agent.PlansCompleted + agent.PlansFailed > 0);
            float predictedLanding = Mathf.Lerp(left, right, selected.landing_screen_x);
            var audit = agent.GetPlanAuditTrail();
            int validated = Array.FindIndex(audit, item => item.kind == "jump_option_revalidated" && item.detail == selected.option_id);
            int launched = Array.FindIndex(audit, item => item.kind == "jump_option_launched" && item.detail == selected.option_id);
            Check(validated >= 0 && launched > validated &&
                Array.Exists(audit, item => item.kind == "jump_option_approach") == (mode == "approach"),
                "Expected a local recheck before launch and the advertised approach behavior.");
            Check(agent.PlansCompleted == 1 && agent.PlanStepsApplied == 1 &&
                level.companion.IsGrounded && Mathf.Abs(level.companion.Position.x - predictedLanding) < .2f,
                "The advertised pair should execute and land near its prediction: " + agent.LastPlanResult);
        }

        private IEnumerator VerifyFirstFailureStop()
        {
            // Wait beyond the real 12-second request interval: a short wait alone
            // could confuse rate limiting with the first-failure stop condition.
            yield return new WaitForSecondsRealtime(12.2f);
            Check(agent.Status == "plan_failure_limit" && agent.PlanRequests == 1 &&
                agent.PlansFailed == 1 && !agent.WaitingForResponse && !agent.ExecutingPlan &&
                agent.PlanStepsApplied == 1 &&
                agent.NetworkRequests == 0 && !agent.allowPaidPlanRequests &&
                agent.planRequestsPerMinute == 5 && agent.maxPlanRequests == 20,
                "First failure did not stop requests with the original 5/20 limits.");
        }

        private IEnumerator BlockedWalk()
        {
            CreateArena(-2f);
            Platform(-10f, 10f);
            Obstacle(new Vector2(-1.555f, 1.5f), new Vector2(.25f, 3f));
            yield return StartAgent();
            if (!Check(request != null, "No initial observation before attempting a blocked walk.")) yield break;

            float startX = level.companion.Position.x;
            VisionRequest proposedFrom = request;
            for (int i = 0; i < 4 && agent.LocalReplans == 0; i++)
            {
                proposedFrom = request;
                Deliver(proposedFrom, 1, false, .35f);
                yield return WaitForRequest();
                if (!Check(request != null && request.frame_id > proposedFrom.frame_id, "No new observation after blocked walk.")) yield break;
            }
            Check(agent.FixtureActionsApplied >= 2 && agent.LocalVetoes == 0 && agent.LocalCorrections == 0,
                "A grounded wall contact was handled as a drop or prevented the stagnation test from exercising applied actions.");
            Check(agent.LocalReplans > 0 && HasReplanEvidence(request, proposedFrom, "ineffective_movement") &&
                request.previous_action.horizontal == 1 && !request.previous_action.jump,
                "Repeated movement into a wall did not request a fresh decision with execution feedback.");
            Check(Mathf.Abs(level.companion.Position.x - startX) < .07f && level.companion.IsGrounded &&
                Mathf.Abs(level.companion.Body.linearVelocity.x) < .05f, "The blocked-walk replan moved the motor on its own.");
        }

        private IEnumerator RepeatedWait()
        {
            CreateArena(-2f);
            Platform(-10f, 10f);
            yield return StartAgent();
            if (!Check(request != null, "No initial observation before the wait test.")) yield break;

            Vector2 start = level.companion.Position;
            VisionRequest proposedFrom = request;
            for (int i = 0; i < 5 && agent.LocalReplans == 0; i++)
            {
                proposedFrom = request;
                Deliver(proposedFrom, 0, false, .78f);
                yield return WaitForRequest();
                if (!Check(request != null && request.frame_id > proposedFrom.frame_id, "No new observation after a neutral wait.")) yield break;
            }
            Check(agent.FixtureActionsApplied >= 3 && agent.LocalReplans > 0 && HasReplanEvidence(request, proposedFrom, "prolonged_wait"),
                "Repeated neutral waits failed to trigger a new decision with elapsed-wait feedback.");
            Check(agent.LocalVetoes == 0 && agent.LocalCorrections == 0 && request.previous_action.horizontal == 0 &&
                !request.previous_action.jump && Vector2.Distance(start, level.companion.Position) < .05f,
                "Neutral-wait replan vetoed safe input or issued movement on its own.");
        }

        private IEnumerator StaleResponses()
        {
            CreateArena(-2f);
            Platform(-10f, 10f);
            yield return StartAgent();
            if (!Check(request != null, "No initial request for correlation test.")) yield break;

            VisionRequest first = request;
            var wrong = Packet(first, 1, true, .4f);
            wrong.frame_id++;
            Check(!agent.DeliverFixtureResponse(first, wrong), "Wrong-frame response was accepted.");
            yield return WaitForRequest();
            if (!Check(request != null && request.frame_id > first.frame_id, "No fresh request after correlation rejection.")) yield break;
            VisionRequest beforeReset = request;
            Check(!agent.DeliverFixtureResponse(first, Packet(first, 1, true, .4f)) && agent.PendingRequest == beforeReset,
                "A late response affected the newer outstanding request.");

            level.ResetLevel();
            Check(agent.SessionId != beforeReset.session_id && !agent.DeliverFixtureResponse(beforeReset, Packet(beforeReset, 1, true, .4f)),
                "Pre-reset response was accepted into a new session.");
            yield return WaitForRequest();
            if (!Check(request != null && request.session_id == agent.SessionId, "Reset did not start a fresh observation session.")) yield break;
            VisionRequest beforeDeath = request;
            level.companion.Kill();
            Check(!agent.DeliverFixtureResponse(beforeDeath, Packet(beforeDeath, 1, true, .4f)), "Response was accepted after death.");
            yield return WaitFor(() => !agent.WaitingForResponse && !agent.ApplyingAction);
            Check(!agent.WaitingForResponse && !agent.ApplyingAction && agent.FixtureActionsApplied == 0 &&
                Mathf.Abs(level.companion.Body.linearVelocity.x) < .05f && agent.RejectedResponses >= 4,
                "Correlation, reset, or death let a stale packet reach the motor.");
        }

        private void CreateArena(float companionX)
        {
            arena = Child("Temporary vision test arena");
            arena.transform.position = new Vector3(OriginX, 0f, 0f);
            arena.SetActive(false);
            var world = arena.AddComponent<CompanionWorld>();
            level = arena.AddComponent<DemoLevel>();
            level.world = world;
            level.player = MakeActor("Human", -7f);
            level.companion = MakeActor("Companion", companionX);
            level.playerSpawn = level.player.transform.position;
            level.companionSpawn = level.companion.transform.position;
            world.player = level.player;
            world.companion = level.companion;
            adapter = arena.AddComponent<DemoVisionActorAdapter>();
            adapter.level = level;
            agent = arena.AddComponent<VisionAgent>();
            agent.enabled = false;
            agent.useShortPlans = false;
            agent.actor = adapter;
            agent.captureLongEdge = 320;
            agent.minObservationInterval = .2f;
            agent.FixtureResponder = _ => null;
            agent.FixtureDelaySeconds = 5f;

            var camera = Child("Fixture camera").AddComponent<Camera>();
            camera.transform.localPosition = new Vector3(0f, 1f, -10f);
            camera.orthographic = true;
            camera.orthographicSize = 6f;
            camera.depth = -100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.cullingMask = 0;
            agent.gameCamera = camera;
        }

        private DemoActor MakeActor(string name, float x)
        {
            var go = Child(name);
            go.transform.localPosition = new Vector3(x, StandingY, 0f);
            go.AddComponent<BoxCollider2D>().size = new Vector2(.65f, 1.2f);
            return go.AddComponent<DemoActor>();
        }

        private BoxCollider2D Platform(float left, float right, float top = 0f) =>
            Obstacle(new Vector2((left + right) * .5f, top - .5f), new Vector2(right - left, 1f));

        private BoxCollider2D Obstacle(Vector2 position, Vector2 size, bool isTrigger = false)
        {
            var go = Child(isTrigger ? "Non-solid trigger" : "Solid obstacle");
            go.transform.localPosition = position;
            var collider = go.AddComponent<BoxCollider2D>();
            collider.size = size;
            collider.isTrigger = isTrigger;
            return collider;
        }

        private GameObject Child(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(arena != null ? arena.transform : transform, false);
            return go;
        }

        private IEnumerator StartAgent()
        {
            arena.SetActive(true);
            Physics2D.SyncTransforms();
            yield return PhysicsTick;
            yield return PhysicsTick;
            agent.enabled = true;
            yield return WaitForRequest();
        }

        private IEnumerator WaitForRequest()
        {
            yield return WaitFor(() => agent.WaitingForResponse);
            request = agent.PendingRequest;
        }

        private static IEnumerator WaitFor(Func<bool> condition)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 4d;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        }

        private void Deliver(VisionRequest pending, int horizontal, bool jump, float duration) =>
            agent.DeliverFixtureResponse(pending, Packet(pending, horizontal, jump, duration));

        private static VisionResponse Packet(VisionRequest pending, int horizontal, bool jump, float duration) =>
            new VisionResponse
            {
                request_id = pending.request_id,
                session_id = pending.session_id,
                frame_id = pending.frame_id,
                source = "fixture",
                action = new VisionAction { horizontal = horizontal, jump = jump, duration_seconds = duration },
                memory = Array.Empty<string>()
            };

        private bool HasReplanEvidence(VisionRequest observation, VisionRequest proposedFrom, string reason)
        {
            foreach (var entry in agent.GetAuditTrail())
            {
                if (entry.kind == "replan" && entry.reason == reason && entry.source == "fixture" &&
                    entry.request_id == proposedFrom.request_id && entry.session_id == proposedFrom.session_id &&
                    entry.frame_id == proposedFrom.frame_id && !string.IsNullOrWhiteSpace(entry.detail) &&
                    Array.IndexOf(observation.recent_events, entry.detail) >= 0) return true;
            }
            return false;
        }

        private bool Check(bool condition, string failure)
        {
            if (!condition) current.failures.Add(failure + " Agent status=" + agent.Status + ".");
            return condition;
        }

        private void CleanupArena()
        {
            if (arena == null) return;
            current.last_plan_result = agent.LastPlanResult;
            current.plan_requests = agent.PlanRequests;
            current.plans_failed = agent.PlansFailed;
            current.plans_completed = agent.PlansCompleted;
            current.plan_steps = agent.PlanStepsApplied;
            current.actor_alive = level.companion.IsAlive;
            current.actor_grounded = level.companion.IsGrounded;
            current.plan_audit = agent.GetPlanAuditTrail();
            Check(agent.ModelDecisionsAccepted == 0 && agent.ModelActionsApplied == 0 && agent.NetworkRequests == 0,
                "Offline fixture used live model transport.");
            report.fixture_decisions += agent.FixtureDecisionsAccepted;
            report.fixture_actions += agent.FixtureActionsApplied;
            report.local_vetoes += agent.LocalVetoes;
            report.local_corrections += agent.LocalCorrections;
            report.local_replans += agent.LocalReplans;
            report.model_actions += agent.ModelActionsApplied;
            report.network_requests += agent.NetworkRequests;
            arena.SetActive(false);
            Destroy(arena);
            arena = null;
            request = null;
        }

        private void OnDisable()
        {
            if (!IsRunning) return;
            StopAllCoroutines();
            CleanupArena();
            report.status = "cancelled";
            LogReport();
        }

        private void LogReport()
        {
            if (report.status == "passed") Debug.Log("VISION_SAFETY_PLAYTEST " + ResultJson, this);
            else Debug.LogWarning("VISION_SAFETY_PLAYTEST " + ResultJson, this);
        }
    }
}
