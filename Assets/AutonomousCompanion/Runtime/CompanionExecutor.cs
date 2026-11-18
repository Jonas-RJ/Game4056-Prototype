using System;
using System.Collections.Generic;
using UnityEngine;
namespace AutonomousCompanion
{
    /// <summary>Executes model- or local-planner-selected tasks locally. It never solves the puzzle or selects a new task.</summary>
    public sealed class CompanionExecutor : MonoBehaviour
    {
        public CompanionWorld world;
        [Min(0.2f)] public float traversalTimeoutSeconds = 8f;
        private TaskState task = new TaskState();
        private CompanionTarget target;
        private CompanionTarget heldSwitch;
        private NavigationLink activeLink;
        private NavigationNode activeLinkOrigin;
        private NavigationLink[] plannedRoute;
        private int plannedLinkIndex;
        private float linkElapsed;
        public TaskState CurrentTask { get { return task; } }
        public bool HasProtectedHold { get { return heldSwitch != null; } }
        public bool CanAcceptDecision { get { return isActiveAndEnabled && activeLink == null; } }
        public string LastDiagnostic { get; private set; } = "initial";
        public event Action<string> TaskChanged;
        public bool IsAllowed(string action, string targetId)
        {
            foreach (var option in AllowedActions())
                if (option.action == action && option.target_id == targetId) return true;
            return false;
        }
        public ActionOption[] AllowedActions()
        {
            var result = new List<ActionOption> { new ActionOption("wait", "") };
            if (world == null || world.companion == null || !world.companion.IsAlive || activeLink != null) return result.ToArray();
            if (heldSwitch != null && !heldSwitch.CanRelease)
            {
                result.Add(new ActionOption("hold_switch", heldSwitch.targetId));
                return result.ToArray();
            }
            foreach (var candidate in world.Targets)
            {
                if (candidate == null || !world.TryRoute(candidate.arrivalNode, out _)) continue;
                // A pressure plate must be approached as a commitment, not a plain
                // arrival that lets the next model decision walk off mid-crossing.
                if (candidate.SupportsHold) result.Add(new ActionOption("hold_switch", candidate.targetId));
                else result.Add(new ActionOption("move_to", candidate.targetId));
            }
            if (world.player != null && world.player.IsAlive && world.TryRoute(world.player.CurrentNode, out _))
                result.Add(new ActionOption("follow_player", "player"));
            return result.ToArray();
        }
        /// <summary>Returns false for fallback, stale local preconditions or protected commitments.
        /// Wait never releases an occupied switch, even when its release predicate is currently true.</summary>
        public bool TryApply(DecisionResponse response) { return TryApply(response, null); }

        internal bool TryApply(DecisionResponse response, NavigationLink[] route)
        {
            if (world == null || world.companion == null || !world.companion.IsAlive)
                return Reject("companion_unavailable");
            if (response == null || (response.source != "model" && response.source != "local_planner")) return Reject("fallback_or_invalid_source_preserved_task");
            if (!CanAcceptDecision || !IsAllowed(response.action, response.target_id)) return Reject("action_no_longer_available");
            if (heldSwitch != null && (response.action == "wait" ||
                (response.action == "hold_switch" && response.target_id == heldSwitch.targetId)))
                return true;
            var nextTarget = response.action == "follow_player" || response.action == "wait" ? null : world.Find(response.target_id);
            if (response.source == "local_planner" && response.action != "wait" &&
                (nextTarget == null || !IsPlannedRouteValid(route, nextTarget.arrivalNode)))
                return Reject("local_route_no_longer_available");
            if (heldSwitch != null)
            {
                if (!heldSwitch.CanRelease) return Reject("switch_commitment_protected");
                heldSwitch.ReleaseHold(world.companion);
                // Adapter must confirm release, rather than silently changing local ownership.
                if (heldSwitch.IsHolding(world.companion)) return Reject("switch_release_not_confirmed");
                heldSwitch = null;
            }
            plannedRoute = response.source == "local_planner" && nextTarget != null ? route : null;
            plannedLinkIndex = 0;
            if (task.action == response.action && task.target_id == response.target_id && task.status == "running") return true;
            world.companion.StopLocomotion();
            target = nextTarget;
            task = new TaskState { action = response.action, target_id = response.target_id, status = "running", reason = response.reason ?? "" };
            LastDiagnostic = "accepted:" + task.action; TaskChanged?.Invoke(LastDiagnostic);
            return true;
        }
        private bool Reject(string message) { LastDiagnostic = message; return false; }

        private bool IsPlannedRouteValid(NavigationLink[] route, NavigationNode destination)
        {
            if (route == null || destination == null) return false;
            var node = world.companion.CurrentNode;
            foreach (var link in route)
            {
                if (node == null || !IsAuthoredLink(node, link) || !world.companion.CanTraverse(link)) return false;
                node = link.destination;
            }
            return node == destination;
        }

        private static bool IsAuthoredLink(NavigationNode from, NavigationLink link)
        {
            return from != null && from.isActiveAndEnabled && link != null && link.IsOpen &&
                link.destination.isActiveAndEnabled && from.links != null && Array.IndexOf(from.links, link) >= 0;
        }

        private void FixedUpdate()
        {
            if (world == null || world.companion == null) return;
            var actor = world.companion;
            if (!actor.IsAlive)
            {
                actor.StopLocomotion(); activeLink = null;
                if (task.status == "running") Fail("companion_not_alive");
                return;
            }
            if (heldSwitch != null)
            {
                heldSwitch.MaintainHold(actor);
                if (!heldSwitch.IsHolding(actor))
                {
                    // A physical displacement already ended occupancy. Keeping stale ownership
                    // would trap the executor in a protected hold it can never reacquire.
                    // Do not call ReleaseHold: no deliberate unsafe release is being authorized.
                    heldSwitch = null;
                    Fail("switch_occupancy_lost");
                }
                return;
            }
            if (task.status != "running" || task.action == "wait") return;
            if (activeLink != null)
            {
                linkElapsed += Time.fixedDeltaTime;
                if (!activeLink.IsOpen || !actor.CanTraverse(activeLink) ||
                    (plannedRoute != null && !IsAuthoredLink(activeLinkOrigin, activeLink)))
                { Fail("active_route_invalidated"); return; }
                var state = actor.TickTraversal(Time.fixedDeltaTime);
                if (state == TraversalResult.Failed || linkElapsed > traversalTimeoutSeconds) { Fail("traversal_failed_or_timed_out"); return; }
                if (state == TraversalResult.Succeeded)
                {
                    if (actor.CurrentNode != activeLink.destination) { Fail("adapter_arrival_mismatch"); return; }
                    activeLink = null;
                    activeLinkOrigin = null;
                    if (plannedRoute != null) plannedLinkIndex++;
                }
                return;
            }
            NavigationNode destination;
            if (task.action == "follow_player")
            {
                if (world.player == null || !world.player.IsAlive) { Fail("player_unavailable"); return; }
                destination = world.player.CurrentNode;
                // A player in mid-jump has no grounded node; wait locally for an observed destination.
                if (destination == null) return;
            }
            else
            {
                if (target == null || world.Find(task.target_id) != target) { Fail("target_unavailable"); return; }
                destination = target.arrivalNode;
            }
            NavigationLink nextLink = null;
            if (plannedRoute != null)
            {
                if (plannedLinkIndex < plannedRoute.Length)
                {
                    nextLink = plannedRoute[plannedLinkIndex];
                    if (!IsAuthoredLink(actor.CurrentNode, nextLink) || !actor.CanTraverse(nextLink))
                    { Fail("local_route_invalidated"); return; }
                }
                else if (actor.CurrentNode != destination) { Fail("local_route_arrival_mismatch"); return; }
            }
            else
            {
                if (!world.TryRoute(destination, out var route)) { Fail("route_unavailable"); return; }
                if (route.Count > 0) nextLink = route[0];
            }
            if (nextLink == null)
            {
                actor.StopLocomotion();
                if (task.action == "hold_switch")
                {
                    if (!target.CanHold(actor) || !target.BeginHold(actor) || !target.IsHolding(actor))
                    { Fail("hold_not_confirmed"); return; }
                    heldSwitch = target; LastDiagnostic = "holding:" + target.targetId;
                    TaskChanged?.Invoke(LastDiagnostic);
                }
                else if (task.action == "move_to")
                { task.status = "completed"; LastDiagnostic = "arrived:" + task.target_id; TaskChanged?.Invoke(LastDiagnostic); }
                return;
            }
            if (!actor.CanStartTraversal) return;
            var origin = actor.CurrentNode;
            if (!actor.BeginTraversal(nextLink)) { Fail("traversal_rejected"); return; }
            activeLink = nextLink; activeLinkOrigin = origin; linkElapsed = 0;
        }
        private void Fail(string message)
        {
            if (world != null && world.companion != null) world.companion.StopLocomotion();
            activeLink = null; activeLinkOrigin = null; plannedRoute = null; plannedLinkIndex = 0;
            task.status = "failed"; task.reason = message;
            LastDiagnostic = message; TaskChanged?.Invoke(message);
        }
        /// <summary>Stops travel when either actor dies. Keeps occupied-switch ownership until
        /// the game resets its world, rather than treating death notification as permission to release.</summary>
        public void SuspendForActorDeath() { Fail("actor_death_awaiting_world_reset"); }
        /// <summary>Call only AFTER your game has reset actors and switch occupancy. This drops
        /// stale ownership without invoking ReleaseHold against a live puzzle.</summary>
        public void ResetAfterWorldReset()
        {
            if (world != null && world.companion != null) world.companion.StopLocomotion();
            activeLink = null; activeLinkOrigin = null; heldSwitch = null; target = null; linkElapsed = 0;
            plannedRoute = null; plannedLinkIndex = 0;
            task = new TaskState(); LastDiagnostic = "world_reset"; TaskChanged?.Invoke(LastDiagnostic);
        }
        private void OnDisable()
        {
            if (world != null && world.companion != null) world.companion.StopLocomotion();
            activeLink = null; activeLinkOrigin = null;
            // Never call ReleaseHold here: network/component lifecycle is not a safe-release condition.
        }
    }
}
