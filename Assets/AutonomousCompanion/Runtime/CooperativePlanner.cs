using System;
using System.Collections.Generic;
using System.Globalization;

namespace AutonomousCompanion
{
    /// <summary>
    /// Bounded breadth-first search over joint navigation and mechanism states.
    /// The first companion action can be executed. Human moves are predictions.
    /// </summary>
    public static class CooperativePlanner
    {
        public sealed class Edge
        {
            public int to;
            public int gate = -1;
        }

        public sealed class Node
        {
            public float x;
            public Edge[] edges = Array.Empty<Edge>();
        }

        public sealed class Target
        {
            public string id;
            public int node;
            public bool pressure, latch;
            public ulong opens;
            public int releaseDoor = -1;
            public float releasePlayerMinX = float.NegativeInfinity;
            public bool releaseUnconditionally;
            public int owner = -1; // -1 either actor, 0 human, 1 companion
        }

        public sealed class Model
        {
            public Node[] nodes;
            public Target[] targets;
            public int playerGoal, companionGoal;
            public ulong permanentlyOpen;
            public bool[] initialCompanionTargets;
            public int maxExpanded = 4096, maxRouteExpanded = 4096, maxWork = 100000;
        }

        public struct State : IEquatable<State>
        {
            public int player, companion, held;
            public ulong latched;

            // Live door masks from actual overlaps. Simulated arrivals use authored node masks.
            public ulong playerPressure, companionPressure;
            public float? playerX;

            public bool Equals(State other)
            {
                return player == other.player && companion == other.companion && held == other.held &&
                    latched == other.latched && playerPressure == other.playerPressure &&
                    companionPressure == other.companionPressure && Nullable.Equals(playerX, other.playerX);
            }

            public override bool Equals(object other) => other is State state && Equals(state);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = ((player * 397) ^ companion) * 397 ^ held;
                    hash = hash * 397 ^ latched.GetHashCode();
                    hash = hash * 397 ^ playerPressure.GetHashCode();
                    hash = hash * 397 ^ companionPressure.GetHashCode();
                    return hash * 397 ^ playerX.GetHashCode();
                }
            }
        }

        public sealed class Plan
        {
            public bool found, waitForPlayer;
            public int target = -1, expanded, work;
            public string diagnostic;
            public string[] steps = Array.Empty<string>();
            public Edge[] route = Array.Empty<Edge>();
        }

        private sealed class Entry
        {
            public State state;
            public Entry parent;
            public int actor, target;
            public Edge[] firstRoute;
        }

        private sealed class RouteEntry
        {
            public State state;
            public RouteEntry parent;
            public Edge edge;
        }

        private sealed class NodeRules
        {
            public readonly List<int> targets = new List<int>();
            public bool hasPressure;
            public ulong pressure, latch;
        }

        private sealed class SearchBudget
        {
            public readonly Plan plan = new Plan();
            public int workLimit;
            public bool exhausted;

            public bool Spend()
            {
                if (plan.work >= workLimit)
                {
                    exhausted = true;
                    return false;
                }

                plan.work++;
                return true;
            }
        }

        public static Plan Search(Model model, State initial)
        {
            if (model?.nodes == null || model.targets == null ||
                !HasNode(model, initial.player) || !HasNode(model, initial.companion) ||
                !HasNode(model, model.playerGoal) || !HasNode(model, model.companionGoal))
                return new Plan { diagnostic = "waiting_for_grounded_observation" };

            if (initial.held < -1 || initial.held >= model.targets.Length ||
                (initial.held >= 0 && (model.targets[initial.held] == null ||
                    !model.targets[initial.held].pressure || model.targets[initial.held].node != initial.companion)) ||
                (model.initialCompanionTargets != null && model.initialCompanionTargets.Length != model.targets.Length))
                return new Plan { diagnostic = "invalid_mechanism_observation" };

            var rules = IndexTargets(model);
            var budget = new SearchBudget { workLimit = model.maxWork };
            var queue = new Queue<Entry>();
            var seen = new HashSet<State> { initial };
            queue.Enqueue(new Entry { state = initial });
            while (queue.Count > 0 && budget.plan.expanded < model.maxExpanded && budget.Spend())
            {
                var entry = queue.Dequeue();
                budget.plan.expanded++;
                if (entry.state.player == model.playerGoal && entry.state.companion == model.companionGoal)
                    return Finish(model, entry, budget.plan);

                // Equal-length solutions favor an immediate companion action.
                if (entry.state.held < 0 || CanRelease(model, entry.state))
                    Expand(model, rules, entry, 1, budget, queue, seen);
                Expand(model, rules, entry, 0, budget, queue, seen);
            }

            budget.plan.diagnostic = queue.Count > 0 || budget.exhausted ? "planning_budget_reached" : "no_cooperative_route";
            return budget.plan;
        }

        private static void Expand(Model model, NodeRules[] rules, Entry entry, int actor,
            SearchBudget budget, Queue<Entry> queue, HashSet<State> seen)
        {
            bool firstCompanionAction = entry.parent == null && actor == 1;
            var routes = new Queue<RouteEntry>();
            var visited = new HashSet<State> { entry.state };
            routes.Enqueue(new RouteEntry { state = entry.state });
            int expanded = 0;
            while (routes.Count > 0)
            {
                if (expanded >= model.maxRouteExpanded)
                {
                    budget.exhausted = true;
                    return;
                }

                if (!budget.Spend()) return;
                expanded++;
                var route = routes.Dequeue();
                var state = route.state;
                int at = actor == 1 ? state.companion : state.player;
                bool moved = route.parent != null;
                var node = rules[at];

                foreach (int index in node.targets)
                {
                    if (!budget.Spend()) return;
                    var target = model.targets[index];
                    if (target.owner >= 0 && target.owner != actor) continue;
                    if (!moved && (actor != 1 || !target.pressure || state.held == index)) continue;
                    if (firstCompanionAction && model.initialCompanionTargets != null && !model.initialCompanionTargets[index]) continue;
                    var next = state;
                    if (actor == 1 && target.pressure) next.held = index;
                    Enqueue(entry, actor, index, route, next, firstCompanionAction, queue, seen);
                }

                // The human can stop past a release boundary even when there is no target there.
                if (actor == 0 && moved && state.held >= 0 &&
                    !CanRelease(model, entry.state) && CanRelease(model, state))
                    Enqueue(entry, actor, -1, route, state, false, queue, seen);

                // Stop at a plate that needs commitment or just opened a route. The executor
                // can take the next companion action once the switch change has been observed.
                if (actor == 1 && moved && (node.hasPressure || state.latched != entry.state.latched)) continue;
                foreach (var edge in model.nodes[at].edges ?? Array.Empty<Edge>())
                {
                    if (!budget.Spend()) return;
                    if (edge == null || !HasNode(model, edge.to) || edge.to == at || !CanTraverse(model, state, actor, edge.gate)) continue;

                    var next = state;
                    next.latched |= rules[edge.to].latch;
                    if (actor == 1)
                    {
                        next.companion = edge.to;
                        next.companionPressure = rules[edge.to].pressure;
                        next.held = -1;
                    }
                    else
                    {
                        next.player = edge.to;
                        next.playerPressure = rules[edge.to].pressure;
                        next.playerX = null;
                    }

                    if (visited.Add(next)) routes.Enqueue(new RouteEntry { state = next, parent = route, edge = edge });
                }
            }
        }

        private static bool HasNode(Model model, int index)
        {
            return index >= 0 && index < model.nodes.Length && model.nodes[index] != null;
        }

        private static NodeRules[] IndexTargets(Model model)
        {
            var rules = new NodeRules[model.nodes.Length];
            for (int i = 0; i < rules.Length; i++) rules[i] = new NodeRules();
            for (int i = 0; i < model.targets.Length; i++)
            {
                var target = model.targets[i];
                if (target == null || !HasNode(model, target.node)) continue;
                var node = rules[target.node];
                node.targets.Add(i);
                if (target.latch) node.latch |= target.opens;
                if (!target.pressure) continue;
                node.hasPressure = true;
                node.pressure |= target.opens;
            }
            return rules;
        }

        private static bool CanRelease(Model model, State state)
        {
            var target = model.targets[state.held];
            return target.releaseUnconditionally || (target.releaseDoor >= 0 && target.releaseDoor < 64 &&
                (state.latched & (1UL << target.releaseDoor)) != 0 &&
                (state.playerX ?? model.nodes[state.player].x) >= target.releasePlayerMinX);
        }

        private static bool CanTraverse(Model model, State state, int actor, int gate)
        {
            if (gate == -1) return true;
            if (gate < 0 || gate >= 64) return false;

            // The moving actor cannot keep its plate pressed through the crossing. A destination
            // switch cannot open its own approach. A waiting companion must commit to a hold.
            ulong stationaryPressure = actor == 1 ? state.playerPressure : state.held >= 0 ? state.companionPressure : 0UL;
            return ((state.latched | model.permanentlyOpen | stationaryPressure) & (1UL << gate)) != 0;
        }

        private static void Enqueue(Entry parent, int actor, int target, RouteEntry route, State state,
            bool firstCompanionAction, Queue<Entry> queue, HashSet<State> seen)
        {
            if (!seen.Add(state)) return;
            var entry = new Entry { state = state, parent = parent, actor = actor, target = target };
            if (firstCompanionAction)
            {
                var edges = new List<Edge>();
                for (var cursor = route; cursor.edge != null; cursor = cursor.parent) edges.Add(cursor.edge);
                edges.Reverse();
                entry.firstRoute = edges.ToArray();
            }
            queue.Enqueue(entry);
        }

        private static Plan Finish(Model model, Entry entry, Plan plan)
        {
            plan.found = true;
            if (entry.parent == null)
            {
                plan.diagnostic = "both_at_exits";
                return plan;
            }

            var path = new List<Entry>();
            for (var cursor = entry; cursor.parent != null; cursor = cursor.parent) path.Add(cursor);
            path.Reverse();
            var first = path[0];
            plan.waitForPlayer = first.actor == 0;
            if (!plan.waitForPlayer)
            {
                plan.target = first.target;
                plan.route = first.firstRoute;
            }

            plan.steps = path.ConvertAll(step =>
            {
                string destination = step.target >= 0 ? model.targets[step.target].id :
                    "walk to x " + model.nodes[step.state.player].x.ToString("0.##", CultureInfo.InvariantCulture);
                return (step.actor == 1 ? "companion: " : "player (predicted): ") + destination;
            }).ToArray();
            plan.diagnostic = plan.waitForPlayer ? "waiting_for_player_progress" : "cooperative_plan_found";
            return plan;
        }
    }
}
