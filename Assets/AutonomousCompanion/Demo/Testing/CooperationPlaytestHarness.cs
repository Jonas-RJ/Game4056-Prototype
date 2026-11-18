using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AutonomousCompanion.Demo.Testing
{
    /// <summary>PlayMode physical test driver. Only the fixture mode injects companion decisions.
    /// The autonomous tests drive human movement and jump input.</summary>
    public sealed class CooperationPlaytestHarness : MonoBehaviour
    {
        public DemoLevel level;
        public CompanionExecutor executor;
        [Min(20)] public float runTimeoutSeconds = 90f;
        public string ResultJson { get { if (IsRunning) Sample(); return JsonUtility.ToJson(report, true); } }
        public bool IsRunning { get { return report.status == "running"; } }

        [Serializable] public sealed class Check
        {
            public string name; public bool passed; public string evidence;
        }
        [Serializable] public sealed class Report
        {
            public string mode = "not_started", status = "not_started", phase = "idle";
            public string decision_source = "none", failure = "";
            public bool proves_model_autonomy, both_exits_completed;
            public float elapsed_seconds, player_x, player_y, companion_x, companion_y;
            public float player_max_y, companion_max_y;
            public int injected_fixture_decisions, live_model_applied, local_applied, local_movement_applied, fallback_events;
            public int local_search_max_expanded, http_busy_samples;
            public float local_search_max_ms;
            public string brain_diagnostic = "", executor_diagnostic = "";
            public List<Check> checks = new List<Check>();
            public List<string> events = new List<string>();
            public string limitations = "Fixture callbacks simulate transport; actual out-of-order network delivery is not tested here.";
        }

        private Report report = new Report();
        private Coroutine routine;
        private float started;
        private bool protectDoor;
        private bool restoreKeyboard;
        private bool restoreBrainEnabled;
        private bool restoreClientEnabled;
        private bool restoreExecutorEnabled;
        private AutonomousCompanionBrain.DecisionMode restoreDecisionMode;
        private bool settingsCaptured;
        private RelayManualControls manualControls;
        private bool restoreManualControls;
        private enum RunMode { Fixture, Live, Local }

        public void BeginFixtureSuite() { Begin(RunMode.Fixture); }
        public void BeginLiveRun() { Begin(RunMode.Live); }
        public void BeginLocalRun() { Begin(RunMode.Local); }

        private void Begin(RunMode mode)
        {
            if (IsRunning) return;
            if (routine != null) StopCoroutine(routine);
            Unsubscribe();
            if (level == null || executor == null || level.player == null || level.companion == null ||
                level.brain == null || level.world == null || level.switchA == null || level.switchB == null ||
                level.doorA == null || level.doorB == null || executor != level.brain.executor)
            {
                report = new Report { status = "failed", failure = "Harness references not wired" }; return;
            }
            var brain = level.brain;
            if ((mode == RunMode.Local && (brain.localPlanner == null || !brain.localPlanner.isActiveAndEnabled)) ||
                (mode == RunMode.Live && brain.client == null))
            {
                report = new Report { status = "failed", failure = "Selected decision source is unavailable" }; return;
            }
            // Reset restores pre-completion component state before capturing it for cleanup.
            level.ResetLevel();
            restoreKeyboard = level.player.allowKeyboard;
            restoreBrainEnabled = brain.enabled;
            restoreClientEnabled = brain.client != null && brain.client.enabled;
            restoreExecutorEnabled = executor.enabled;
            restoreDecisionMode = brain.decisionMode;
            settingsCaptured = true;
            manualControls = level.GetComponent<RelayManualControls>();
            if (manualControls != null) { restoreManualControls = manualControls.enabled; manualControls.enabled = false; }
            level.player.allowKeyboard = false;
            brain.enabled = false;
            executor.enabled = true;
            brain.decisionMode = mode == RunMode.Local ? AutonomousCompanionBrain.DecisionMode.LocalPlanner :
                AutonomousCompanionBrain.DecisionMode.RemoteModel;
            if (brain.client != null) brain.client.enabled = mode == RunMode.Live;
            protectDoor = false;
            started = Time.unscaledTime;
            report = new Report
            {
                mode = mode == RunMode.Local ? "local_planner" : mode == RunMode.Live ? "live_model" : "deterministic_fixture",
                status = "running",
                decision_source = mode == RunMode.Local ? "local_planner" : mode == RunMode.Live ? "awaiting_http_model" : "simulated_fixture_not_a_model"
            };
            if (mode == RunMode.Local) report.limitations = "This test uses authored demo navigation and scripted human motor input. The HTTP client is disabled.";
            if (mode == RunMode.Live) report.limitations = "This test uses scripted human motor input and the configured HTTP model.";
            brain.Diagnostic += ObserveBrain;
            level.world.Changed += ObserveWorld;
            if (mode != RunMode.Fixture) brain.enabled = true;
            routine = StartCoroutine(mode == RunMode.Local ? LocalRun() : mode == RunMode.Live ? LiveRun() : FixtureSuite());
        }

        private void Update()
        {
            if (!IsRunning) return;
            Sample();
            if (!level.player.IsAlive || !level.companion.IsAlive) Fail("An actor died");
            else if (report.elapsed_seconds > runTimeoutSeconds) Fail("Run timed out: " + report.phase);
            else if (report.mode == "local_planner" && level.brain.client != null &&
                (level.brain.client.isActiveAndEnabled || level.brain.client.IsBusy))
                Fail("HTTP was active during the local planner test");
            else if (protectDoor && !level.switchA.CanRelease && (!level.doorA.IsOpen || !executor.HasProtectedHold))
                Fail("Door A or protected hold was lost while the human depended on it");
        }

        private IEnumerator FixtureSuite()
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (!IsRunning) yield break;
            Phase("reset_invalidates_old_callback");
            if (!CheckResetCallback()) yield break;
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (!IsRunning) yield break;
            Phase("AI_walks_to_switch_A");
            if (!ApplyFixture("hold_switch", "switch_a")) yield break;
            yield return WaitFor(() => executor.HasProtectedHold && level.switchA.Active && level.doorA.IsOpen, 12f, "AI physically occupies A and opens door");
            if (!IsRunning) yield break;
            Record("physical_switch_hold", true, "AI x=" + level.companion.Position.x + "; A active and door open");
            protectDoor = true;

            Phase("human_enters_doorway_and_pauses");
            yield return MoveHuman(-2f, 8f);
            if (!IsRunning) yield break;
            yield return ObserveHold(1f);
            if (!IsRunning) yield break;
            Record("pause_preserves_open_door", true, "Door stayed open for 1 second with human at doorway");

            Phase("human_backtracks");
            yield return MoveHuman(-3.2f, 4f);
            if (!IsRunning) yield break;
            Record("backtrack_preserves_open_door", true, "Human returned to x=" + level.player.Position.x);
            bool accepted = executor.TryApply(new DecisionResponse { action = "wait", target_id = "", source = "fallback", reason = "simulated upstream timeout" });
            if (accepted || !executor.HasProtectedHold) { Fail("Fallback changed protected task"); yield break; }
            yield return ObserveHold(0.5f);
            if (!IsRunning) yield break;
            Record("fallback_preserves_hold", true, executor.LastDiagnostic + "; simulated fallback, no HTTP call");

            Phase("human_activates_return_switch_B");
            yield return MoveHuman(0f, 8f);
            if (!IsRunning) yield break;
            yield return WaitFor(() => level.switchB.Latched && level.doorA.Latched && level.doorB.Latched && level.switchA.CanRelease, 4f, "B latches return path");
            if (!IsRunning) yield break;
            protectDoor = false;
            Record("safe_release_condition", true, "B and both doors latched; A.CanRelease=true");
            if (!ApplyFixture("move_to", "exit_companion")) yield break;
            Phase("both_actors_traverse_gap");
            yield return HumanToExit();
            if (!IsRunning) yield break;
            yield return WaitFor(() => level.Completed, 25f, "Both actors physically occupy separate exits");
            if (!IsRunning) yield break;
            if (report.player_max_y < 1.1f || report.companion_max_y < 1.1f) { Fail("Missing observed physical jump height"); yield break; }
            Record("physical_jump_both_actors", true, "Peak y human=" + report.player_max_y + ", AI=" + report.companion_max_y);
            Record("both_exits", true, "DemoLevel.Completed=true; both actors alive");
            Finish(RunMode.Fixture);
        }

        private IEnumerator LocalRun()
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return LocalCircuit(true);
            if (!IsRunning) yield break;
            Record("both_exits", true, "Both assigned exits are physically occupied and both actors are alive");
            if (report.player_max_y < 1.1f || report.companion_max_y < 1.1f)
            { Fail("Missing observed physical jump height"); yield break; }
            Record("physical_jump_both_actors", true, "Peak y human=" + report.player_max_y + ", AI=" + report.companion_max_y);
            if (report.local_movement_applied == 0 || report.injected_fixture_decisions != 0 ||
                report.live_model_applied != 0 || report.http_busy_samples != 0)
            { Fail("The offline circuit did not use the local decision path exclusively"); yield break; }
            Record("offline_autonomous_cooperation", true, "Companion tasks came from the local planner with the HTTP client disabled");

            Phase("restart_after_completion");
            int movementsBeforeReset = report.local_movement_applied;
            level.ResetLevel();
            if (level.Completed || level.doorA.IsOpen || level.doorB.IsOpen || executor.HasProtectedHold ||
                !level.brain.isActiveAndEnabled || !executor.isActiveAndEnabled)
            { Fail("Reset after completion did not restore the starting puzzle and brain"); yield break; }
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return LocalCircuit(false);
            if (!IsRunning) yield break;
            if (report.local_movement_applied <= movementsBeforeReset)
            { Fail("No new local movement decisions followed reset"); yield break; }
            Record("restart_after_completion", true, "A fresh local circuit reached both exits after resetting the solved puzzle");
            Finish(RunMode.Local);
        }

        private IEnumerator LocalCircuit(bool checkPauseAndBacktrack)
        {
            Phase("human_approaches_closed_door");
            yield return MoveHuman(-3.2f, 8f);
            if (!IsRunning) yield break;
            Phase("awaiting_local_companion");
            yield return WaitFor(() => level.switchA.Active && level.doorA.IsOpen && executor.HasProtectedHold,
                12f, "Local planner sends companion to a physical pressure-plate hold");
            if (!IsRunning) yield break;
            protectDoor = true;
            if (checkPauseAndBacktrack)
            {
                Record("physical_switch_hold", true, "AI occupies A and holds the physical gate open");
                Phase("human_pauses_in_doorway");
                yield return MoveHuman(-2f, 5f);
                if (!IsRunning) yield break;
                yield return ObserveHold(5f);
                if (!IsRunning) yield break;
                Record("pause_preserves_open_door", true, "Door stayed open for five seconds with human in the doorway");

                Phase("human_backtracks");
                yield return MoveHuman(-3.2f, 4f);
                if (!IsRunning) yield break;
                yield return ObserveHold(1f);
                if (!IsRunning) yield break;
                Record("backtrack_preserves_open_door", true, "Hold persisted after the human returned to x=" + level.player.Position.x);
            }
            Phase("human_activates_return_switch");
            yield return MoveHuman(0f, 8f);
            if (!IsRunning) yield break;
            yield return WaitFor(() => level.switchB.Latched && level.doorA.Latched && level.doorB.Latched && level.switchA.CanRelease,
                4f, "Human switch latches the return path");
            if (!IsRunning) yield break;
            protectDoor = false;
            if (checkPauseAndBacktrack)
                Record("safe_release_condition", true, "Human B occupancy latched both doors and satisfied the live release condition");

            Phase("both_actors_traverse_gap");
            yield return HumanToExit();
            if (!IsRunning) yield break;
            yield return WaitFor(() => level.Completed, 25f, "Local companion and human physically occupy their assigned exits");
        }

        private IEnumerator LiveRun()
        {
            Phase("human_approaches_closed_door");
            yield return MoveHuman(-2.8f, 8f);
            if (!IsRunning) yield break;
            level.world.NotifyChanged("player_waiting_at_closed_door_without_commands");
            Phase("awaiting_autonomous_help");
            yield return WaitFor(() => level.doorA.IsOpen && executor.HasProtectedHold, runTimeoutSeconds - 10f, "Model autonomously holds door A open");
            if (!IsRunning) yield break;
            protectDoor = true;
            yield return MoveHuman(-2f, 5f);
            if (!IsRunning) yield break;
            yield return new WaitForSeconds(1f);
            if (!IsRunning) yield break;
            yield return MoveHuman(-3.2f, 4f);
            if (!IsRunning) yield break;
            yield return MoveHuman(0f, 8f);
            if (!IsRunning) yield break;
            yield return WaitFor(() => level.doorB.IsOpen, 5f, "Human switch opens return path");
            if (!IsRunning) yield break;
            protectDoor = false;
            Phase("human_continues_without_AI_commands");
            yield return HumanToExit();
            if (!IsRunning) yield break;
            yield return WaitFor(() => level.Completed, runTimeoutSeconds, "AI autonomously reaches its exit");
            if (!IsRunning) yield break;
            if (report.live_model_applied == 0) { Fail("No genuine model decisions were observed"); yield break; }
            Record("live_autonomous_cooperation", true, "Human motor input only; brain and HTTP chose all companion tasks");
            Finish(RunMode.Live);
        }

        private IEnumerator HumanToExit()
        {
            yield return MoveHuman(4.1f, 8f);
            if (!IsRunning) yield break;
            yield return WaitFor(() => level.player.IsGrounded, 2f, "Human grounded at jump takeoff");
            if (!IsRunning) yield break;
            level.player.SetTestInput(1f, true);
            yield return new WaitForFixedUpdate();
            level.player.SetTestInput(1f, false);
            yield return WaitFor(() => level.player.Position.x >= 6.65f && level.player.IsGrounded, 3f, "Human jumps across the real gap");
            level.player.SetTestInput(0f, false);
            if (!IsRunning) yield break;
            yield return MoveHuman(9f, 5f);
        }

        private IEnumerator MoveHuman(float x, float timeout)
        {
            float deadline = Time.unscaledTime + timeout;
            while (IsRunning && Mathf.Abs(level.player.Position.x - x) > 0.12f)
            {
                if (Time.unscaledTime >= deadline) { Fail("Human move timeout at x=" + level.player.Position.x + ", goal=" + x); break; }
                level.player.SetTestInput(Mathf.Sign(x - level.player.Position.x), false);
                yield return new WaitForFixedUpdate();
            }
            level.player.SetTestInput(0f, false);
        }

        private IEnumerator ObserveHold(float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (IsRunning && Time.unscaledTime < until)
            {
                if (!executor.HasProtectedHold || !level.doorA.IsOpen) { Fail("Protected hold lost during pause/backtrack"); yield break; }
                yield return new WaitForFixedUpdate();
            }
        }

        private IEnumerator WaitFor(Func<bool> condition, float seconds, string description)
        {
            float deadline = Time.unscaledTime + seconds;
            while (IsRunning && !condition())
            {
                if (Time.unscaledTime >= deadline) { Fail(description + " timed out"); yield break; }
                yield return new WaitForFixedUpdate();
            }
        }

        private bool ApplyFixture(string action, string target)
        {
            // Executor requires source=model. This is deliberately a TEST injection, counted
            // and reported as simulated_fixture_not_a_model. It never increments live_model_applied.
            report.injected_fixture_decisions++;
            var response = new DecisionResponse { action = action, target_id = target, source = "model", reason = "SIMULATED PHYSICS FIXTURE; not model reasoning" };
            if (executor.TryApply(response)) return true;
            Fail("Fixture action rejected: " + action + "/" + target + ": " + executor.LastDiagnostic); return false;
        }

        private bool CheckResetCallback()
        {
            // Invoke the actual callback gate synchronously; this checks stale-generation logic,
            // not real network reordering. No frame advances with the brain enabled here.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var field = typeof(AutonomousCompanionBrain).GetField("generation", flags);
            var callback = typeof(AutonomousCompanionBrain).GetMethod("OnDecision", flags);
            if (field == null || callback == null) { Fail("Brain callback API changed; stale reset fixture needs updating"); return false; }
            level.brain.enabled = true;
            long oldGeneration = (long)field.GetValue(level.brain);
            var request = level.brain.CaptureRequest();
            level.ResetLevel();
            var response = new DecisionResponse { request_id = request.request_id, state_version = request.state_version,
                action = "hold_switch", target_id = "switch_a", source = "model", reason = "SIMULATED stale transport callback" };
            try { callback.Invoke(level.brain, new object[] { oldGeneration, request, response, null }); }
            catch (Exception ex) { level.brain.enabled = false; Fail("Stale callback fixture exception: " + ex.GetType().Name); return false; }
            bool passed = level.brain.LastDiagnostic == "obsolete_generation_preserved_task" && executor.CurrentTask.action == "wait";
            level.brain.enabled = false;
            if (!passed) { Fail("World reset did not reject stale callback"); return false; }
            Record("reset_rejects_stale_callback", true, "Actual brain callback gate invoked with pre-reset generation; simulated transport");
            return true;
        }

        private void ObserveBrain(string message)
        {
            if (!IsRunning) return;
            if (report.mode == "live_model" && message.StartsWith("decision_applied:", StringComparison.Ordinal)) report.live_model_applied++;
            if (report.mode == "local_planner")
            {
                if (message.StartsWith("local_decision_applied:", StringComparison.Ordinal))
                {
                    report.local_applied++;
                    if (message != "local_decision_applied:wait") report.local_movement_applied++;
                }
                if (level.brain.localPlanner is DemoLocalPlanner planner)
                {
                    report.local_search_max_ms = Mathf.Max(report.local_search_max_ms, (float)planner.LastMilliseconds);
                    report.local_search_max_expanded = Mathf.Max(report.local_search_max_expanded, planner.LastExpanded);
                }
            }
            if (message.IndexOf("fallback", StringComparison.OrdinalIgnoreCase) >= 0) report.fallback_events++;
            Event("brain:" + message);
        }
        private void ObserveWorld(string message) { if (IsRunning) Event("world:" + message); }
        private void Phase(string phase) { report.phase = phase; Event("phase:" + phase); }
        private void Event(string message)
        {
            report.events.Add((Time.unscaledTime - started).ToString("F3") + " " + message);
            if (report.events.Count > 150) report.events.RemoveAt(0);
        }
        private void Record(string name, bool passed, string evidence)
        { report.checks.Add(new Check { name = name, passed = passed, evidence = evidence }); }
        private void Sample()
        {
            if (level == null || level.player == null || level.companion == null) return;
            if (IsRunning) report.elapsed_seconds = Time.unscaledTime - started;
            report.player_x = level.player.Position.x; report.player_y = level.player.Position.y;
            report.companion_x = level.companion.Position.x; report.companion_y = level.companion.Position.y;
            report.player_max_y = Mathf.Max(report.player_max_y, report.player_y);
            report.companion_max_y = Mathf.Max(report.companion_max_y, report.companion_y);
            report.both_exits_completed = level.Completed;
            report.brain_diagnostic = level.brain != null ? level.brain.LastDiagnostic : "missing";
            report.executor_diagnostic = executor != null ? executor.LastDiagnostic : "missing";
            if (level.brain != null && level.brain.client != null && level.brain.client.IsBusy) report.http_busy_samples++;
        }
        private void Fail(string reason)
        {
            if (!IsRunning) return;
            Sample(); report.failure = reason; report.status = "failed";
            Record(report.phase, false, reason); Cleanup();
            Debug.LogError("COOP_PLAYTEST " + JsonUtility.ToJson(report));
        }
        private void Finish(RunMode mode)
        {
            Sample(); report.status = "passed"; report.phase = "finished";
            report.proves_model_autonomy = mode == RunMode.Live;
            if (mode == RunMode.Live) report.decision_source = "live_http_model";
            Cleanup(); Debug.Log("COOP_PLAYTEST " + JsonUtility.ToJson(report));
        }
        private void Cleanup()
        {
            protectDoor = false;
            Unsubscribe();
            if (!settingsCaptured || level == null) return;
            settingsCaptured = false;
            if (manualControls != null) { manualControls.enabled = restoreManualControls; manualControls = null; }
            if (level.player != null)
            {
                level.player.ClearTestInput();
                level.player.allowKeyboard = restoreKeyboard;
            }
            if (level.brain == null) return;
            var brain = level.brain;
            brain.enabled = false;
            brain.decisionMode = restoreDecisionMode;
            if (brain.client != null)
            {
                brain.client.CancelPending();
                brain.client.enabled = restoreClientEnabled;
            }
            if (!level.Completed && gameObject.activeInHierarchy)
            {
                executor.enabled = restoreExecutorEnabled;
                brain.enabled = restoreBrainEnabled;
            }
        }
        private void Unsubscribe()
        {
            if (level == null) return;
            if (level.brain != null) level.brain.Diagnostic -= ObserveBrain;
            if (level.world != null) level.world.Changed -= ObserveWorld;
        }
        private void OnDisable()
        {
            if (routine != null) StopCoroutine(routine);
            if (IsRunning) { Sample(); report.status = "cancelled"; report.failure = "Harness disabled"; }
            Cleanup();
        }
    }
}
