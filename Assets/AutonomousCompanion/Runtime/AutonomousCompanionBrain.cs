using System;
using System.Collections.Generic;
using UnityEngine;
namespace AutonomousCompanion
{
    /// <summary>Autonomous observation/decision loop. No player command API is required.
    /// Connect meaningful game events to world.NotifyChanged; periodic observation is a backup.</summary>
    public sealed class AutonomousCompanionBrain : MonoBehaviour
    {
        public enum DecisionMode { RemoteModel, LocalPlanner }
        public DecisionMode decisionMode;
        public LocalDecisionProvider localPlanner;
        public CompanionWorld world;
        public CompanionExecutor executor;
        public DecisionHttpClient client;
        public string goal = "Both characters reach their exits alive";
        [Min(0.1f)] public float eventDebounceSeconds = 0.35f;
        [Min(0.5f)] public float periodicDecisionSeconds = 3f;
        public string LastDiagnostic { get; private set; } = "not_started";
        public DecisionRequest LastRequest { get; private set; }
        public event Action<string> Diagnostic;
        private long generation;
        private float changedAt;
        private float lastDispatch = float.NegativeInfinity;
        private bool dirty = true;
        private bool awaitingWorldReset;
        private void OnEnable()
        {
            generation++; dirty = true; changedAt = Time.unscaledTime;
            if (world != null) world.Changed += OnImportantEvent;
            if (executor != null) executor.TaskChanged += OnImportantEvent;
        }
        private void OnDisable()
        {
            generation++;
            if (world != null) world.Changed -= OnImportantEvent;
            if (executor != null) executor.TaskChanged -= OnImportantEvent;
        }
        private void OnImportantEvent(string description)
        { generation++; dirty = true; changedAt = Time.unscaledTime; }
        private void Update()
        {
            if (world == null || executor == null || world.player == null || world.companion == null) return;
            if (executor.world != world) { Report("executor_world_mismatch"); return; }
            if (!world.companion.IsAlive || !world.player.IsAlive)
            {
                if (!awaitingWorldReset)
                {
                    awaitingWorldReset = true; generation++; if (client != null) client.CancelPending();
                    executor.SuspendForActorDeath(); Report("actor_death_awaiting_world_reset");
                }
                return;
            }
            if (awaitingWorldReset || !executor.CanAcceptDecision) return;
            if (decisionMode == DecisionMode.RemoteModel && (client == null || !client.isActiveAndEnabled || client.IsBusy)) return;
            if (decisionMode == DecisionMode.LocalPlanner && (localPlanner == null || !localPlanner.isActiveAndEnabled))
            {
                if (LastDiagnostic != "local_planner_unavailable") Report("local_planner_unavailable");
                return;
            }
            if (dirty && Time.unscaledTime - changedAt < eventDebounceSeconds) return;
            if (!dirty && Time.unscaledTime - lastDispatch < periodicDecisionSeconds) return;
            long requestedGeneration = generation;
            var request = CaptureRequest();
            if (decisionMode == DecisionMode.LocalPlanner)
            {
                LastRequest = request; dirty = false; lastDispatch = Time.unscaledTime;
                DecisionResponse response;
                NavigationLink[] route;
                try { response = localPlanner.Decide(request, out route); }
                catch (Exception exception)
                {
                    Report("local_planner_error:" + exception.GetType().Name);
                    Debug.LogException(exception, this);
                    return;
                }
                ApplyDecision(requestedGeneration, request, response, null, route);
                return;
            }
            if (!client.TrySend(request, (response, error) => OnDecision(requestedGeneration, request, response, error))) return;
            LastRequest = request; dirty = false; lastDispatch = Time.unscaledTime;
            Report("decision_requested:" + request.request_id);
        }
        public DecisionRequest CaptureRequest()
        {
            var objects = new List<WorldObjectState>();
            foreach (var target in world.Targets)
            {
                if (target == null) continue;
                Vector3 p = target.transform.position;
                objects.Add(new WorldObjectState {
                    id = target.targetId, kind = target.kind, x = p.x, y = p.y,
                    active = target.Active, controls = target.controls ?? Array.Empty<string>(),
                    reachable = world.TryRoute(target.arrivalNode, out _)
                });
            }
            var current = executor.CurrentTask;
            return new DecisionRequest {
                request_id = Guid.NewGuid().ToString("N"), state_version = world.StateVersion,
                goal = goal, player = world.player.Capture("player"), companion = world.companion.Capture("companion"),
                objects = objects.ToArray(), events = world.RecentEvents(),
                current_task = new TaskState { action = current.action, target_id = current.target_id, status = current.status, reason = current.reason },
                allowed_actions = executor.AllowedActions()
            };
        }
        private void OnDecision(long requestedGeneration, DecisionRequest request, DecisionResponse response, string error)
        { ApplyDecision(requestedGeneration, request, response, error, null); }

        private void ApplyDecision(long requestedGeneration, DecisionRequest request, DecisionResponse response, string error,
            NavigationLink[] plannedRoute)
        {
            if (!isActiveAndEnabled) return;
            if (awaitingWorldReset || world == null || world.player == null || world.companion == null ||
                !world.player.IsAlive || !world.companion.IsAlive)
            { Report("actors_unavailable_preserved_task"); return; }
            if (requestedGeneration != generation || world.StateVersion != request.state_version)
            { Report("obsolete_generation_preserved_task"); return; }
            if (error != null || response == null)
            { Report(error ?? "empty_response_preserved_task"); return; }
            if (response.request_id != request.request_id || response.state_version != request.state_version)
            { Report("correlation_mismatch_preserved_task"); return; }
            if (response.source == "fallback") { Report("fallback_preserved_task"); return; }
            if (response.source == "local_unavailable" && decisionMode == DecisionMode.LocalPlanner)
            { Report("local_task_preserved:" + response.reason); return; }
            string expectedSource = decisionMode == DecisionMode.LocalPlanner ? "local_planner" : "model";
            if (response.source != expectedSource) { Report("decision_source_mismatch_preserved_task"); return; }
            if (!executor.TryApply(response, plannedRoute))
            {
                dirty = true; changedAt = Time.unscaledTime;
                Report(executor.LastDiagnostic); return;
            }
            Report((decisionMode == DecisionMode.LocalPlanner ? "local_decision_applied:" : "decision_applied:") + response.action);
        }
        /// <summary>Gameplay reset hook: first reset physical actors, doors and switch occupancy,
        /// then call this once. Invalidates pending decisions and clears task/hold ownership.</summary>
        public void ResetAfterWorldReset()
        {
            generation++;
            if (client != null) client.ResetAfterWorldReset();
            if (executor != null) executor.ResetAfterWorldReset();
            if (world != null) world.ResetAfterWorldReset();
            awaitingWorldReset = false; dirty = true;
            changedAt = Time.unscaledTime; lastDispatch = float.NegativeInfinity;
            Report("session_reset");
        }
        private void Report(string message) { LastDiagnostic = message; Diagnostic?.Invoke(message); }
    }
}
