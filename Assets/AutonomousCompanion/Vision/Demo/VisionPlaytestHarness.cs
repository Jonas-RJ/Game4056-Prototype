using System;
using System.Collections;
using AutonomousCompanion.Vision;
using UnityEngine;

namespace AutonomousCompanion.Demo.Testing
{
    /// <summary>Offline fixture for camera requests, real companion motor input and session safety.</summary>
    public sealed class VisionPlaytestHarness : MonoBehaviour
    {
        public DemoLevel level;
        public VisionAgent agent;
        public bool IsRunning { get; private set; }
        public string ResultJson => JsonUtility.ToJson(report, true);

        [Serializable]
        private sealed class Report
        {
            public string source = "scripted_fixture", status = "not_started", failure = "";
            public bool camera_frame, wrong_frame_rejected, movement, jump, reset_rejects_old_response, death_rejects_response;
            public bool human_untouched, memory_retained, proves_model_autonomy;
            public int frames, fixture_actions, model_actions, network_requests, rejections;
            public int image_width, image_height, image_bytes;
            public float horizontal_distance, jump_height, jump_horizontal_distance;
        }

        private Report report = new Report();
        private VisionAction nextAction;
        private bool rejectFirst;
        private Coroutine routine;
        private float responseDelay;
        private bool restorePlanMode;

        public void BeginOfflineTest()
        {
            if (IsRunning) return;
            report = new Report { status = "running" };
            if (agent == null || agent.enabled || level == null || level.player == null || level.companion == null)
            {
                report.status = "failed";
                report.failure = "Configure the vision demo and disable the live VisionAgent before starting this fixture.";
                Debug.LogError("VISION_PLAYTEST " + ResultJson, this);
                return;
            }

            restorePlanMode = agent.useShortPlans;
            agent.useShortPlans = false;
            level.ResetLevel();
            responseDelay = agent.FixtureDelaySeconds;
            agent.FixtureDelaySeconds = .25f;
            agent.FixtureResponder = Respond;
            nextAction = Action(0, false, .15f);
            rejectFirst = true;
            IsRunning = true;
            agent.enabled = true;
            routine = StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            int framesBefore = agent.FramesCaptured;
            int fixturesBefore = agent.FixtureActionsApplied;
            int networkBefore = agent.NetworkRequests;
            int modelsBefore = agent.ModelActionsApplied;
            int rejectsBefore = agent.RejectedResponses;
            float initialCompanionX = level.companion.Position.x;
            float initialHumanX = level.player.Position.x;

            yield return WaitFor(() => agent.WaitingForResponse, 8f, "camera frame");
            if (!IsRunning) yield break;
            var first = agent.PendingRequest;
            report.camera_frame = agent.LastFrameBytes > 512 && agent.LastFrameWidth > 0 && agent.LastFrameHeight > 0 &&
                Mathf.Max(agent.LastFrameWidth, agent.LastFrameHeight) <= 960 && first.image_mime == "image/jpeg" &&
                first.actor.alive && first.actor.screen_x >= 0 && first.actor.screen_x <= 1 &&
                first.actor.screen_y >= 0 && first.actor.screen_y <= 1;
            if (!report.camera_frame) { Finish("Invalid captured camera observation"); yield break; }

            yield return WaitFor(() => agent.RejectedResponses > rejectsBefore, 5f, "wrong-frame rejection");
            if (!IsRunning) yield break;
            report.wrong_frame_rejected = agent.FixtureActionsApplied == fixturesBefore && !agent.ApplyingAction &&
                Mathf.Abs(level.companion.Position.x - initialCompanionX) < .05f;
            if (!report.wrong_frame_rejected) { Finish("Wrong-frame response moved the companion"); yield break; }

            nextAction = Action(1, false, .42f);
            yield return WaitFor(() => agent.FixtureActionsApplied > fixturesBefore && agent.ApplyingAction, 5f, "right movement");
            if (!IsRunning) yield break;
            yield return WaitFor(() => !agent.ApplyingAction, 4f, "end of right movement");
            if (!IsRunning) yield break;
            report.horizontal_distance = level.companion.Position.x - initialCompanionX;
            report.movement = report.horizontal_distance > .45f && Mathf.Abs(level.companion.Body.linearVelocity.x) < .05f;
            if (!report.movement) { Finish("Timed movement did not move and stop the physical companion"); yield break; }

            int jumpDirection = -1;
            if (!agent.actor.CheckControlSafety(jumpDirection, true, out string leftRejection))
            {
                jumpDirection = 1;
                if (!agent.actor.CheckControlSafety(jumpDirection, true, out string rightRejection))
                { Finish($"Fixture takeoff has no clear directional jump. Left: {leftRejection}. Right: {rightRejection}."); yield break; }
            }

            float takeoffY = level.companion.Position.y;
            float takeoffX = level.companion.Position.x;
            int appliedBeforeJump = agent.FixtureActionsApplied;
            nextAction = Action(jumpDirection, true, .1f);
            yield return WaitFor(() => agent.FixtureActionsApplied > appliedBeforeJump && agent.ApplyingAction, 5f, "directional jump");
            if (!IsRunning) yield break;
            float peak = takeoffY;
            float jumpDeadline = Time.unscaledTime + 4f;
            while (IsRunning && agent.ApplyingAction && Time.unscaledTime < jumpDeadline)
            {
                peak = Mathf.Max(peak, level.companion.Position.y);
                yield return null;
            }
            report.jump_height = peak - takeoffY;
            report.jump_horizontal_distance = jumpDirection * (level.companion.Position.x - takeoffX);
            report.jump = !agent.ApplyingAction && level.companion.IsGrounded &&
                report.jump_height > .6f && report.jump_horizontal_distance > .8f;
            report.human_untouched = Mathf.Abs(level.player.Position.x - initialHumanX) < .05f;
            if (!report.jump || !report.human_untouched)
            { Finish("Directional jump did not sustain movement to landing, or the human moved"); yield break; }

            nextAction = Action(0, false, .15f);
            agent.FixtureDelaySeconds = 1f;
            yield return WaitFor(() => agent.WaitingForResponse, 5f, "pending observation before reset");
            if (!IsRunning) yield break;
            var old = agent.PendingRequest;
            int appliedBeforeReset = agent.FixtureActionsApplied;
            level.ResetLevel();
            report.reset_rejects_old_response = old.session_id != agent.SessionId &&
                !agent.DeliverFixtureResponse(old, Packet(old, Action(1, true, .5f))) &&
                agent.FixtureActionsApplied == appliedBeforeReset && !agent.ApplyingAction;
            if (!report.reset_rejects_old_response) { Finish("Pre-reset response was accepted"); yield break; }

            yield return WaitFor(() => agent.WaitingForResponse, 5f, "fresh observation after reset");
            if (!IsRunning) yield break;
            var fresh = agent.PendingRequest;
            report.memory_retained = fresh.memory.Length == 1 && fresh.memory[0] == "fixture memory";
            level.companion.Kill();
            bool rejected = !agent.DeliverFixtureResponse(fresh, Packet(fresh, Action(1, true, .5f)));
            yield return WaitFor(() => !agent.WaitingForResponse, 3f, "cancel after death");
            if (!IsRunning) yield break;
            report.death_rejects_response = rejected && !agent.ApplyingAction &&
                agent.FixtureActionsApplied == appliedBeforeReset && Mathf.Abs(level.companion.Body.linearVelocity.x) < .05f;
            if (!report.death_rejects_response || !report.memory_retained)
            { Finish("Death guard or bounded same-level memory check failed"); yield break; }

            report.frames = agent.FramesCaptured - framesBefore;
            report.fixture_actions = agent.FixtureActionsApplied - fixturesBefore;
            report.model_actions = agent.ModelActionsApplied - modelsBefore;
            report.network_requests = agent.NetworkRequests - networkBefore;
            report.rejections = agent.RejectedResponses - rejectsBefore;
            if (report.model_actions != 0 || report.network_requests != 0)
            { Finish("Fixture unexpectedly used live transport"); yield break; }
            Finish(null);
        }

        private IEnumerator WaitFor(Func<bool> condition, float seconds, string description)
        {
            float deadline = Time.unscaledTime + seconds;
            while (IsRunning && !condition())
            {
                if (Time.unscaledTime >= deadline) { Finish("Timed out waiting for " + description + " (" + agent.Status + ")"); yield break; }
                yield return null;
            }
        }

        private VisionResponse Respond(VisionRequest request)
        {
            var response = Packet(request, nextAction);
            if (rejectFirst)
            {
                rejectFirst = false;
                response.frame_id++;
            }
            return response;
        }

        private static VisionAction Action(int horizontal, bool jump, float duration) =>
            new VisionAction { horizontal = horizontal, jump = jump, duration_seconds = duration };

        private static VisionResponse Packet(VisionRequest request, VisionAction action) =>
            new VisionResponse
            {
                request_id = request.request_id,
                session_id = request.session_id,
                frame_id = request.frame_id,
                source = "fixture",
                action = Action(action.horizontal, action.jump, action.duration_seconds),
                memory = new[] { "fixture memory" }
            };

        private void Finish(string failure)
        {
            if (!IsRunning) return;
            IsRunning = false;
            report.status = failure == null ? "passed" : "failed";
            report.failure = failure ?? "";
            report.image_width = agent.LastFrameWidth;
            report.image_height = agent.LastFrameHeight;
            report.image_bytes = agent.LastFrameBytes;
            Cleanup();
            if (failure == null) Debug.Log("VISION_PLAYTEST " + ResultJson, this);
            else Debug.LogError("VISION_PLAYTEST " + ResultJson, this);
        }

        private void Cleanup()
        {
            if (agent != null)
            {
                agent.enabled = false;
                agent.FixtureResponder = null;
                agent.useShortPlans = restorePlanMode;
                agent.FixtureDelaySeconds = responseDelay;
                agent.ClearMemory();
            }
            if (level != null && level.gameObject.activeInHierarchy) level.ResetLevel();
        }

        private void OnDisable()
        {
            if (!IsRunning) return;
            if (routine != null) StopCoroutine(routine);
            Finish("Fixture disabled before completion");
        }
    }
}
