using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AutonomousCompanion.Vision;
using UnityEngine;

namespace AutonomousCompanion.Demo.Testing
{
    public sealed class VisionVisualHoldPlaytest : MonoBehaviour
    {
        [Serializable]
        private sealed class Report
        {
            public string scene, outcome = "running";
            public int httpRequests;
            public List<string> checks = new List<string>();
            public List<string> failures = new List<string>();
        }

        private readonly Report report = new Report();
        private VisionAgent agent;
        private DemoLevel level;
        private string directory;
        private RelayManualControls manualControls;
        private bool restoreManualInput;

        public void Begin(string evidenceDirectory, bool testLatch)
        {
            directory = evidenceDirectory;
            report.scene = gameObject.scene.name;
            StartCoroutine(Run(testLatch));
        }

        private IEnumerator Run(bool testLatch)
        {
            level = GetComponent<DemoLevel>();
            agent = GetComponent<VisionAgent>();
            if (agent == null)
            {
                agent = gameObject.AddComponent<VisionAgent>();
                agent.enabled = false;
                var adapter = gameObject.AddComponent<DemoVisionActorAdapter>();
                adapter.level = level;
                agent.actor = adapter;
                agent.gameCamera = Camera.main;
            }
            if (!Check(agent != null && !agent.enabled && agent.gameCamera != null && level != null, "agent_setup"))
            { Finish(); yield break; }

            manualControls = GetComponent<RelayManualControls>();
            if (manualControls != null)
            {
                restoreManualInput = manualControls.InputEnabled;
                manualControls.InputEnabled = false;
            }
            level.companion.allowKeyboard = false;
            level.ResetLevel();
            gameObject.AddComponent<DemoVisualHoldSignals>();
            yield return null;
            yield return new WaitForFixedUpdate();
            if (!Check(agent.gateLatchedMarker != null && agent.playerPassedGateMarker != null, "rendered_indicators_bound"))
            { Finish(); yield break; }

            if (testLatch)
            {
                Vector3 platform = level.switchB.arrivalNode.transform.position;
                // Start nearby so the test isolates the wait while the real trigger still requires walking.
                level.player.ResetActor(new Vector2(platform.x - .95f, platform.y));
                Physics2D.SyncTransforms();
                yield return new WaitForFixedUpdate();
            }
            string kind = testLatch ? VisionAgent.HoldUntilGateLatched : VisionAgent.HoldUntilPlayerPassedGate;
            string observed = "visual_hold_observed:" + kind;
            float plateX = level.switchA.trigger.bounds.center.x;
            float screenX = agent.gameCamera.WorldToViewportPoint(new Vector3(plateX, level.companion.Position.y)).x;
            VisionPlanOutcome outcome = null;
            StartFixture("event", kind, 6f, screenX, value => outcome = value);

            yield return Until(() => HasAudit("step_started", kind) || agent.PlansFailed > 0, 5);
            yield return new WaitForSeconds(.30f);
            if (!Check(agent.ExecutingPlan && level.switchA.Occupied && !level.doorA.Latched, "holds_while_event_is_absent"))
            { Finish(); yield break; }

            float destination = testLatch ? level.switchB.arrivalNode.transform.position.x :
                level.doorA.transform.position.x + .82f;
            double deadline = Time.fixedTimeAsDouble + 4;
            while (level.player.Position.x < destination && Time.fixedTimeAsDouble < deadline && !HasAudit("step_completed", observed))
            {
                level.player.SetTestInput(.55f, false);
                yield return new WaitForFixedUpdate();
            }
            level.player.ClearTestInput();
            level.player.StopLocomotion();
            yield return Until(() => outcome != null || agent.PlansFailed > 0, 12);
            Check(outcome?.status == "completed" && outcome.step_results.Length == 2 &&
                outcome.step_results[1].detail == observed && outcome.step_results[1].elapsed_seconds < 6f,
                "visual_event_ends_hold_and_reaches_next_observation");
            Check(level.switchA.Occupied && Mathf.Abs(level.companion.Position.x - plateX) <= .08f,
                "companion_keeps_plate_occupied");
            Check(level.doorA.Latched == testLatch, testLatch ? "real_pressure_switch_latched_gate" : "player_passed_before_gate_latched");

            if (!testLatch && outcome?.status == "completed")
            {
                agent.enabled = false;
                // Cover the live positive marker in the actual render without changing the underlying event.
                var marker = agent.playerPassedGateMarker;
                var cover = Instantiate(marker, marker.transform.parent);
                cover.name = "Test-only marker occlusion";
                cover.color = Color.black;
                cover.sortingOrder = marker.sortingOrder + 1;
                outcome = null;
                StartFixture("occluded", kind, .70f, null, value => outcome = value);
                yield return Until(() => outcome != null || agent.PlansFailed > 0, 5);
                Check(outcome?.status == "completed" && outcome.step_results[0].detail == "visual_hold_timeout:" + kind &&
                    outcome.step_results[0].elapsed_seconds >= .69f, "occluded_positive_event_times_out");
                Check(level.switchA.Occupied && marker.color.r > .9f, "hidden_event_remains_physically_true");
                Destroy(cover.gameObject);
            }
            Finish();
        }

        private void StartFixture(string name, string kind, float timeout, float? walkX, Action<VisionPlanOutcome> onFollowUp)
        {
            agent.ResetPlanBudget();
            agent.allowPaidPlanRequests = false;
            agent.planRequestsPerMinute = 20;
            agent.maxPlanRequests = 2;
            agent.maxConsecutivePlanFailures = 1;
            agent.FixtureDelaySeconds = .10f;
            agent.FixtureResponder = request =>
            {
                bool first = request.frame_id == 1;
                string prefix = Path.Combine(directory, name + (first ? "-initial" : "-follow-up"));
                File.WriteAllText(prefix + ".request.json", JsonUtility.ToJson(request));
                agent.SaveLastFrame(prefix + ".jpg");
                if (!first && request is VisionPlanRequest followUp && followUp.previous_plan.Length == 1)
                    onFollowUp(followUp.previous_plan[0]);
                var hold = new VisionPlanStep { kind = kind, timeout_seconds = timeout };
                var steps = walkX.HasValue && first ?
                    new[] { new VisionPlanStep { kind = "move_to", screen_x = walkX.Value, timeout_seconds = 3 }, hold } :
                    new[] { first ? hold : new VisionPlanStep { kind = "wait", timeout_seconds = .2f } };
                return new VisionResponse
                {
                    request_id = request.request_id, session_id = request.session_id, frame_id = request.frame_id,
                    source = "fixture", reason = "Offline visual hold fixture",
                    plan = new VisionPlan { objective = "Hold position", expected_result = "Check visible event", steps = steps }
                };
            };
            agent.enabled = true;
        }

        private bool HasAudit(string kind, string detail) =>
            Array.Exists(agent.GetPlanAuditTrail(), entry => entry.session_id == agent.SessionId &&
                entry.kind == kind && entry.detail == detail);

        private bool Check(bool ok, string label)
        {
            (ok ? report.checks : report.failures).Add(label);
            return ok;
        }

        private IEnumerator Until(Func<bool> condition, float timeout)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + timeout;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        }

        private void Finish()
        {
            if (agent != null)
            {
                agent.enabled = false;
                agent.FixtureResponder = null;
                report.httpRequests = agent.NetworkRequests;
            }
            if (level != null && level.player != null) level.player.ClearTestInput();
            if (manualControls != null) manualControls.InputEnabled = restoreManualInput;
            Check(report.httpRequests == 0, "no_http_requests");
            report.outcome = report.failures.Count == 0 ? "passed" : "failed";
            File.WriteAllText(Path.Combine(directory, "unity-result.json"), JsonUtility.ToJson(report, true));
        }
    }
}
