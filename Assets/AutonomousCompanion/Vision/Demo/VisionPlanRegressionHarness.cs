using System;
using System.Collections;
using System.Collections.Generic;
using AutonomousCompanion.Vision;
using UnityEngine;

namespace AutonomousCompanion.Demo.Testing
{
    public sealed class VisionPlanRegressionHarness : MonoBehaviour
    {
        [Serializable] private sealed class Report
        {
            public string status = "running", source = "scripted_offline_regression";
            public List<string> checks = new List<string>(), failures = new List<string>();
            public int http_requests;
        }
        private Report report = new Report();
        public string ResultJson => JsonUtility.ToJson(report);
        public bool IsRunning => report.status == "running";
        private DemoLevel level;
        private VisionAgent agent;
        private int requestsBefore;
        public void Begin()
        {
            level = GetComponent<DemoLevel>(); agent = GetComponent<VisionAgent>();
            if (agent.enabled) { report.status = "failed"; report.failures.Add("Agent must be disabled"); return; }
            StartCoroutine(Run());
        }
        private IEnumerator Run()
        {
            requestsBefore = agent.NetworkRequests;
            agent.useShortPlans = true; agent.allowPaidPlanRequests = false;
            agent.enabled = true;
            yield return new WaitForSecondsRealtime(.4f);
            Check(agent.Status == "paid_plan_requests_disabled" && agent.NetworkRequests == requestsBefore, "paid_requests_default_off");
            agent.enabled = false;
            agent.ResetPlanBudget(); level.ResetLevel();
            agent.planRequestsPerMinute = 20; agent.maxPlanRequests = 2; agent.FixtureDelaySeconds = .1f;
            agent.FixtureResponder = req => Packet(req, new VisionPlanStep {kind="wait",screen_x=0,horizontal=0,timeout_seconds=.2f});
            agent.enabled = true;
            yield return Wait(() => agent.PlansCompleted >= 1, 5);
            Check(agent.PlanRequests == 1 && agent.PlansCompleted >= 1, "one_plan_one_request");
            yield return new WaitForSecondsRealtime(.5f);
            Check(agent.PlanRequests == 1, "unchanged_outcome_respects_minimum_interval");
            yield return Wait(() => agent.PlanRequests == 2, 4);
            Check(agent.PlanRequests == 2, "completed_wait_reobserves_unchanged_frame");
            var followUp = JsonUtility.FromJson<VisionPlanRequest>(JsonUtility.ToJson(agent.PendingRequest));
            var waitResult = followUp?.previous_plan?.Length == 1 ? followUp.previous_plan[0] : null;
            Check(followUp?.protocol == "short_plan_v2" && waitResult?.frame_id == 1 &&
                waitResult.plan.objective == "Offline regression" && waitResult.plan.expected_result == "Physical step checks" &&
                waitResult.status == "completed" && waitResult.cancelled_steps == 0 && waitResult.step_results.Length == 1 &&
                waitResult.step_results[0].detail == "wait_elapsed_not_mechanism_verified" &&
                waitResult.step_results[0].elapsed_seconds >= .2f &&
                waitResult.step_results[0].start_x == waitResult.step_results[0].end_x,
                "next_wire_request_includes_completed_wait_details");
            var progress = followUp?.progress_memory;
            Check(progress?.observed_frame_id == 1 && progress.observations.Length == 1 && progress.hypotheses.Length == 1 &&
                progress.observations[0] == "Companion is on a platform." &&
                progress.hypotheses[0] == "The platform may lead to an exit.",
                "next_wire_request_distinguishes_observations_from_hypotheses");
            yield return Wait(() => !agent.WaitingForResponse && !agent.ExecutingPlan, 3);
            agent.enabled = false; agent.enabled = true; level.ResetLevel();
            yield return new WaitForSecondsRealtime(.5f);
            Check(agent.PlanRequests == 2 && agent.Status == "plan_budget_exhausted", "budget_survives_toggle_and_world_reset");
            agent.enabled = false; agent.ResetPlanBudget(); level.ResetLevel(); agent.maxPlanRequests = 10;
            float target = level.companion.Position.x + 1.234f;
            float screenX = agent.gameCamera.WorldToViewportPoint(new Vector3(target, .65f, 0)).x;
            int completed = agent.PlansCompleted;
            agent.FixtureResponder = req => Packet(req,
                new VisionPlanStep {kind="move_to",screen_x=screenX,horizontal=0,timeout_seconds=3},
                new VisionPlanStep {kind="wait",screen_x=0,horizontal=0,timeout_seconds=.3f});
            agent.enabled = true;
            yield return Wait(() => agent.PlansCompleted > completed, 5);
            Check(agent.PlansCompleted > completed && Mathf.Abs(level.companion.Position.x-target) <= .045f &&
                Mathf.Abs(level.companion.Body.linearVelocity.x)<.02f, "local_multistep_precision_and_stop");
            Check(agent.PlanRequests == 1, "multiple_steps_share_one_model_request");
            yield return new WaitForSecondsRealtime(.5f);
            Check(agent.PlanRequests == 1, "minimum_interval_throttles_changed_scene");
            agent.enabled = false; agent.ResetPlanBudget(); level.ResetLevel();
            agent.maxConsecutivePlanFailures = 1;
            int beforeNoopCompleted = agent.PlansCompleted, beforeNoopFailed = agent.PlansFailed, beforeNoopSteps = agent.PlanStepsApplied;
            agent.FixtureResponder = req => Packet(req,
                new VisionPlanStep {kind="move_to",screen_x=req.actor.screen_x,horizontal=0,timeout_seconds=2},
                new VisionPlanStep {kind="wait",screen_x=0,horizontal=0,timeout_seconds=.2f});
            agent.enabled = true;
            yield return Wait(() => agent.PlansFailed > beforeNoopFailed, 4);
            Check(agent.PlansFailed == beforeNoopFailed + 1 && agent.LastPlanResult.Contains("move_target_already_reached_no_progress") &&
                agent.PlansCompleted == beforeNoopCompleted, "move_at_current_position_fails_not_success");
            Check(agent.PlanStepsApplied == beforeNoopSteps && !agent.ExecutingPlan, "noop_cancels_remaining_steps");
            yield return new WaitForSecondsRealtime(3.2f);
            Check(agent.PlanRequests == 1 && agent.Status == "plan_failure_limit", "noop_first_failure_has_no_retry");
            agent.enabled = false; agent.ResetPlanBudget(); level.ResetLevel();
            agent.FixtureDelaySeconds = 5;
            agent.enabled = true;
            yield return Wait(()=>agent.WaitingForResponse,3);
            var old=agent.PendingRequest;
            level.ResetLevel();
            Check(old!=null && !agent.DeliverFixtureResponse(old, Packet(old, new VisionPlanStep {kind="wait",timeout_seconds=.2f})), "stale_plan_after_reset_rejected");
            yield return Wait(() => agent.PendingRequest?.session_id != null && agent.PendingRequest.session_id != old?.session_id, 5);
            var reset = agent.PendingRequest as VisionPlanRequest;
            Check(reset?.previous_plan.Length == 0 && reset.progress_memory.observed_frame_id == 0 &&
                reset.progress_memory.observations.Length == 0, "new_session_does_not_inherit_old_progress");
            agent.enabled = false; agent.ResetPlanBudget(); level.ResetLevel(); agent.FixtureDelaySeconds=.1f;
            agent.maxConsecutivePlanFailures=3;
            VisionPlanOutcome failedOutcome = null;
            agent.FixtureResponder = req =>
            {
                if (req.frame_id == 2 && req is VisionPlanRequest followUpRequest && followUpRequest.previous_plan.Length == 1)
                    failedOutcome = followUpRequest.previous_plan[0];
                return Packet(req, new VisionPlanStep {kind="move_to",screen_x=0,horizontal=0,timeout_seconds=3},
                    new VisionPlanStep {kind="wait",timeout_seconds=.2f});
            };
            agent.enabled=true;
            yield return Wait(()=>agent.Status=="plan_failure_limit",14);
            int used=agent.PlanRequests;
            Check(agent.Status=="plan_failure_limit" && used==3,"three_failed_plans_stop_requests");
            Check(failedOutcome?.frame_id == 1 && failedOutcome.status == "failed" && failedOutcome.cancelled_steps == 1 &&
                failedOutcome.step_results.Length == 1 && failedOutcome.step_results[0].status == "failed",
                "failed_plan_reports_failed_step_and_cancelled_tail");
            yield return new WaitForSecondsRealtime(3.2f);
            Check(agent.PlanRequests==used,"failure_stop_has_no_background_retry");
            agent.enabled=false; agent.ResetPlanBudget(); level.ResetLevel(); agent.maxConsecutivePlanFailures=1;
            agent.enabled=true;
            yield return Wait(()=>agent.Status=="plan_failure_limit",5);
            Check(agent.Status=="plan_failure_limit" && agent.PlanRequests==1,"first_failed_plan_stops_requests");
            yield return new WaitForSecondsRealtime(3.2f);
            Check(agent.PlanRequests==1,"first_failure_has_no_retry");
            agent.enabled=false; agent.ResetPlanBudget(); level.ResetLevel();
            agent.FixtureResponder=req=>null;
            agent.enabled=true;
            yield return Wait(()=>agent.Status=="plan_failure_limit",5);
            Check(agent.PlanRequests==1 && agent.LastPlanResult.StartsWith("transport_failed:"),"first_bad_response_stops_requests");
            yield return new WaitForSecondsRealtime(3.2f);
            Check(agent.PlanRequests==1,"bad_response_has_no_retry");
            Check(!VisionAgent.ValidPlan(new VisionPlan {objective="x",expected_result="x",steps=new[]{new VisionPlanStep{kind="jump",horizontal=0,timeout_seconds=1}}}), "invalid_plan_rejected");
            Check(!VisionAgent.ValidPlan(new VisionPlan {objective="x",expected_result="x",steps=new[]{new VisionPlanStep{kind="jump",horizontal=1,screen_x=.5f,timeout_seconds=.2f}}}), "short_jump_timeout_rejected_before_launch");
            var a=new Color32[100];var b=new Color32[100];
            Check(!VisionAgent.VisuallyChanged(a,b),"identical_frame_unchanged");
            for(int i=0;i<8;i++)b[i]=new Color32(255,0,0,255);
            Check(VisionAgent.VisuallyChanged(a,b),"visible_change_detected");
            agent.enabled=false;agent.FixtureResponder=null;agent.FixtureDelaySeconds=.2f;
            agent.planRequestsPerMinute=5;agent.maxPlanRequests=20;agent.maxConsecutivePlanFailures=3;agent.ResetPlanBudget();level.ResetLevel();
            report.http_requests=agent.NetworkRequests-requestsBefore;
            Check(report.http_requests==0,"zero_paid_http_requests");
            report.status=report.failures.Count==0?"passed":"failed";
            Debug.Log("VISION_PLAN_REGRESSION "+ResultJson,this);
        }
        private VisionResponse Packet(VisionRequest req, params VisionPlanStep[] steps) => new VisionResponse {
            request_id=req.request_id,session_id=req.session_id,frame_id=req.frame_id,source="fixture",reason="offline fixture",
            plan=new VisionPlan {objective="Offline regression",expected_result="Physical step checks",steps=steps},
            progress_memory = new VisionProgressMemory {
                observations = new[] {"Companion is on a platform."},
                hypotheses = new[] {"The platform may lead to an exit."}
            }};
        private void Check(bool pass,string name) { if(pass)report.checks.Add(name);else report.failures.Add(name+" ("+agent.Status+")"); }
        private IEnumerator Wait(Func<bool> condition,float timeout) { float end=Time.realtimeSinceStartup+timeout;while(!condition()&&Time.realtimeSinceStartup<end)yield return null; }
        private void OnDisable() { if(agent!=null&&IsRunning){StopAllCoroutines();agent.enabled=false;agent.FixtureResponder=null;report.status="cancelled";} }
    }
}
