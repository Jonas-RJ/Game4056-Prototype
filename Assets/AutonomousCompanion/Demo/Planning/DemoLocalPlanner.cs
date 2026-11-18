using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    /// <summary>Translates the demo's authored navigation and live mechanisms into the planning domain.</summary>
    public sealed class DemoLocalPlanner : LocalDecisionProvider
    {
        private const int MaxNodes = 128;
        private const int MaxLinks = 512;
        private const int MaxDoors = 60;
        private const int MaxTargets = 48;

        public CompanionWorld world;
        [Range(128, 8192)] public int searchStateLimit = 4096;
        public string LastDiagnostic { get; private set; } = "not_started";
        public string[] LastPlan { get; private set; } = Array.Empty<string>();
        public int LastExpanded { get; private set; }
        public double LastMilliseconds { get; private set; }

        public override DecisionResponse Decide(DecisionRequest request, out NavigationLink[] route)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            LastExpanded = 0;
            LastPlan = Array.Empty<string>();
            try { return Choose(request, out route); }
            finally { LastMilliseconds = timer.Elapsed.TotalMilliseconds; }
        }

        private DecisionResponse Choose(DecisionRequest request, out NavigationLink[] route)
        {
            route = Array.Empty<NavigationLink>();
            if (world == null || !(world.player is DemoActor player) || !(world.companion is DemoActor companion) ||
                !player.IsAlive || !companion.IsAlive || !player.IsGrounded || companion.CurrentNode == null)
                return PreserveTask(request, "waiting_for_grounded_observation");

            var nodes = new List<NavigationNode>();
            foreach (var node in companion.nodes ?? Array.Empty<NavigationNode>())
                if (node != null && node.isActiveAndEnabled && !nodes.Contains(node)) nodes.Add(node);
            if (nodes.Count == 0 || nodes.Count > MaxNodes) return PreserveTask(request, "unsupported_navigation_size");

            var doors = new List<DemoDoor>();
            var targets = new List<CompanionTarget>();
            foreach (var target in world.Targets)
            {
                if (target == null || !target.isActiveAndEnabled || !nodes.Contains(target.arrivalNode)) continue;
                if (target is DemoPressureSwitch plate && IsLiveTrigger(plate.trigger))
                {
                    targets.Add(plate);
                    foreach (var door in plate.controlledDoors ?? Array.Empty<DemoDoor>()) AddDoor(doors, door);
                    if (plate.safeReleaseCondition is DemoSafeReleaseCondition release && release.player == player)
                        AddDoor(doors, release.latchedDoor);
                }
                else if (target is DemoExit exit && IsLiveTrigger(exit.trigger) &&
                    (exit.requiredActor == player || exit.requiredActor == companion)) targets.Add(exit);
            }

            var authoredLinks = new Dictionary<CooperativePlanner.Edge, NavigationLink>();
            var model = new CooperativePlanner.Model
            {
                nodes = new CooperativePlanner.Node[nodes.Count + 1],
                targets = new CooperativePlanner.Target[targets.Count],
                initialCompanionTargets = new bool[targets.Count],
                playerGoal = -1,
                companionGoal = -1,
                maxExpanded = Mathf.Clamp(searchStateLimit, 128, 8192)
            };
            for (int i = 0; i < nodes.Count; i++)
            {
                var edges = new List<CooperativePlanner.Edge>();
                foreach (var link in nodes[i].links ?? Array.Empty<NavigationLink>())
                {
                    if (link == null || !nodes.Contains(link.destination) ||
                        (link.traversal != "walk" && link.traversal != "jump")) continue;
                    if (link.gate != null && !(link.gate is DemoDoor)) continue;
                    var door = link.gate as DemoDoor;
                    AddDoor(doors, door);
                    var edge = new CooperativePlanner.Edge { to = nodes.IndexOf(link.destination), gate = doors.IndexOf(door) };
                    edges.Add(edge);
                    authoredLinks.Add(edge, link);
                    if (authoredLinks.Count > MaxLinks) return PreserveTask(request, "unsupported_navigation_size");
                }
                model.nodes[i] = new CooperativePlanner.Node { x = nodes[i].transform.position.x, edges = edges.ToArray() };
            }

            if (doors.Count > MaxDoors || targets.Count > MaxTargets) return PreserveTask(request, "unsupported_mechanism_size");
            var playerBody = player.GetComponent<BoxCollider2D>();
            var companionBody = companion.GetComponent<BoxCollider2D>();
            if (playerBody == null || companionBody == null || !playerBody.enabled || !companionBody.enabled)
                return PreserveTask(request, "actor_colliders_unavailable");
            var state = new CooperativePlanner.State
            {
                player = ObserveHuman(player, playerBody, companionBody, nodes, model, authoredLinks),
                companion = nodes.IndexOf(companion.CurrentNode),
                held = -1,
                playerX = player.Position.x
            };
            if (state.player < 0 || state.companion < 0)
                return PreserveTask(request, "no_grounded_navigation_observation");
            for (int i = 0; i < doors.Count; i++)
                if (doors[i].Latched) state.latched |= 1UL << i;

            for (int i = 0; i < targets.Count; i++)
            {
                var source = targets[i];
                var target = new CooperativePlanner.Target { id = source.targetId, node = nodes.IndexOf(source.arrivalNode) };
                model.targets[i] = target;
                model.initialCompanionTargets[i] = IsAllowed(request, source.SupportsHold ? "hold_switch" : "move_to", source.targetId);
                if (source is DemoPressureSwitch plate)
                {
                    target.pressure = !plate.latchOnPress;
                    target.latch = plate.latchOnPress;
                    foreach (var door in plate.controlledDoors ?? Array.Empty<DemoDoor>())
                    {
                        int index = doors.IndexOf(door);
                        if (index >= 0) target.opens |= 1UL << index;
                    }
                    if (plate.IsHolding(companion)) state.held = i;
                    if (target.pressure)
                    {
                        // Use actual overlap for the observed state. Standing on an authored
                        // arrival node is only the prediction for a future traversal.
                        bool playerOccupies = playerBody.Distance(plate.trigger).isOverlapped;
                        bool companionOccupies = companionBody.Distance(plate.trigger).isOverlapped;
                        if (playerOccupies) state.playerPressure |= target.opens;
                        if (companionOccupies) state.companionPressure |= target.opens;
                        if (target.node == state.companion && !companionOccupies) model.initialCompanionTargets[i] = false;
                    }
                    if (plate.safeReleaseCondition is DemoSafeReleaseCondition release && release.player == player)
                    {
                        target.releaseDoor = doors.IndexOf(release.latchedDoor);
                        target.releasePlayerMinX = release.safePlayerX;
                    }
                }
                else if (source is DemoExit exit)
                {
                    target.owner = exit.requiredActor == player ? 0 : 1;
                    if (target.owner == 0 && model.playerGoal < 0) model.playerGoal = target.node;
                    else if (target.owner == 1 && model.companionGoal < 0) model.companionGoal = target.node;
                    else return PreserveTask(request, "multiple_exits_for_actor");
                }
            }

            var plan = CooperativePlanner.Search(model, state);
            LastExpanded = plan.expanded;
            LastPlan = plan.steps;
            if (!plan.found) return PreserveTask(request, plan.diagnostic);
            if (plan.target < 0) return Reply(request, "wait", "", plan.diagnostic, "local_planner");

            var chosen = targets[plan.target];
            string action = chosen.SupportsHold ? "hold_switch" : "move_to";
            if (!IsAllowed(request, action, chosen.targetId))
                return PreserveTask(request, "plan_first_step_not_currently_allowed");
            var selected = new NavigationLink[plan.route.Length];
            for (int i = 0; i < selected.Length; i++)
                if (!authoredLinks.TryGetValue(plan.route[i], out selected[i]))
                    return PreserveTask(request, "plan_route_not_authored");
            route = selected;
            return Reply(request, action, chosen.targetId, plan.diagnostic, "local_planner");
        }

        private static int ObserveHuman(DemoActor player, BoxCollider2D playerBody, BoxCollider2D companionBody,
            List<NavigationNode> nodes, CooperativePlanner.Model model,
            Dictionary<CooperativePlanner.Edge, NavigationLink> authoredLinks)
        {
            Vector2 position = player.Position;
            Vector2 center = playerBody.bounds.center;
            Vector2 size = (Vector2)playerBody.bounds.size * .9f;
            var filter = new ContactFilter2D { useTriggers = false };
            var hits = new RaycastHit2D[8];
            var current = player.CurrentNode;
            int currentIndex = nodes.IndexOf(current);
            if (currentIndex >= 0 && IsClear(center, size, (Vector2)current.transform.position - position,
                    filter, hits, playerBody, companionBody, null)) return currentIndex;

            // A person can stop between authored nodes. Only project onto an authored walk
            // segment, and only through its own modeled gate. A wall, other obstacle or
            // jump gap cannot create a fictional path from this observation.
            var edges = new List<CooperativePlanner.Edge>();
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector2 start = nodes[i].transform.position;
                foreach (var edge in model.nodes[i].edges)
                {
                    var link = authoredLinks[edge];
                    if (link.traversal != "walk") continue;
                    Vector2 end = nodes[edge.to].transform.position;
                    Vector2 segment = end - start;
                    if (segment.sqrMagnitude < .001f) continue;
                    float along = Vector2.Dot(position - start, segment) / segment.sqrMagnitude;
                    if (along < 0 || along > 1 || Vector2.Distance(position, start + along * segment) > .2f) continue;
                    Vector2 delta = end - position;
                    var door = link.gate as DemoDoor;
                    var blocker = door != null ? door.blocker : null;
                    if (!IsClear(center, size, delta, filter, hits, playerBody, companionBody, blocker)) continue;
                    // If the player is already past a door, retreating further onto that
                    // side of the segment does not depend on the door staying open.
                    int gate = blocker is BoxCollider2D box && !SweepMayTouch(center, size, delta, box) ? -1 : edge.gate;
                    edges.Add(new CooperativePlanner.Edge { to = edge.to, gate = gate });
                }
            }
            if (edges.Count == 0) return -1;
            model.nodes[nodes.Count] = new CooperativePlanner.Node { x = position.x, edges = edges.ToArray() };
            return nodes.Count;
        }

        private static bool IsClear(Vector2 center, Vector2 size, Vector2 delta, ContactFilter2D filter,
            RaycastHit2D[] hits, Collider2D playerBody, Collider2D companionBody, Collider2D modeledGate)
        {
            if (delta.sqrMagnitude < .00001f) return true;
            int count = Physics2D.BoxCast(center, size, 0, delta.normalized, filter, hits, delta.magnitude);
            if (count >= hits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                var obstacle = hits[i].collider;
                if (obstacle != null && obstacle != playerBody && obstacle != companionBody && obstacle != modeledGate)
                    return false;
            }
            return true;
        }

        private static bool SweepMayTouch(Vector2 center, Vector2 size, Vector2 delta, BoxCollider2D blocker)
        {
            Vector2 blockerCenter = blocker.transform.TransformPoint(blocker.offset);
            Vector2 axisX = blocker.transform.TransformVector(Vector2.right * (blocker.size.x * .5f));
            Vector2 axisY = blocker.transform.TransformVector(Vector2.up * (blocker.size.y * .5f));
            Vector2 extent = new Vector2(Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x), Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y));
            Vector2 sweptMin = Vector2.Min(center, center + delta) - size * .5f;
            Vector2 sweptMax = Vector2.Max(center, center + delta) + size * .5f;
            return sweptMin.x <= blockerCenter.x + extent.x && sweptMax.x >= blockerCenter.x - extent.x &&
                sweptMin.y <= blockerCenter.y + extent.y && sweptMax.y >= blockerCenter.y - extent.y;
        }

        private static bool IsLiveTrigger(Collider2D collider)
        { return collider != null && collider.enabled && collider.isTrigger && collider.gameObject.activeInHierarchy; }

        private static void AddDoor(List<DemoDoor> doors, DemoDoor door)
        { if (door != null && !doors.Contains(door)) doors.Add(door); }

        private static bool IsAllowed(DecisionRequest request, string action, string id)
        {
            foreach (var option in request.allowed_actions)
                if (option.action == action && option.target_id == id) return true;
            return false;
        }

        private DecisionResponse PreserveTask(DecisionRequest request, string reason)
        { return Reply(request, "wait", "", reason, "local_unavailable"); }

        private DecisionResponse Reply(DecisionRequest request, string action, string id, string diagnostic, string source)
        {
            LastDiagnostic = diagnostic;
            return new DecisionResponse { request_id = request.request_id, state_version = request.state_version,
                action = action, target_id = id, source = source, reason = diagnostic };
        }
    }
}
