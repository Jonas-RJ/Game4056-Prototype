using System;
using System.Linq;
using AutonomousCompanion;
using Edge = AutonomousCompanion.CooperativePlanner.Edge;
using Model = AutonomousCompanion.CooperativePlanner.Model;
using Node = AutonomousCompanion.CooperativePlanner.Node;
using Plan = AutonomousCompanion.CooperativePlanner.Plan;
using State = AutonomousCompanion.CooperativePlanner.State;
using Target = AutonomousCompanion.CooperativePlanner.Target;

namespace AutonomousCompanion.Editor
{
    /// <summary>Pure planner checks runnable from the Editor or a standalone C# runner.</summary>
    public static class LocalPlannerTests
    {
        public static string Run()
        {
            DemoLayout();
            ReversedRoles();
            BranchedSwitchHandoff();
            ReleaseBoundary();
            MovingActorCannotPowerItsGate();
            AllowedActionsAndExactRoute();
            BudgetAndUnreachable();
            return "Passed seven local planner scenarios.";
        }

        private static void DemoLayout()
        {
            float[] xs = { -8, -7, -5, -2.8f, -1.2f, 0, 2.2f, 3.8f, 4.1f, 6.7f, 8, 9 };
            var nodes = xs.Select(x => new Node { x = x }).ToArray();
            for (int i = 0; i < nodes.Length - 1; i++)
            {
                int gate = i == 3 ? 0 : i == 6 ? 1 : -1;
                Connect(nodes, i, i + 1, gate);
                Connect(nodes, i + 1, i, gate);
            }

            var model = Scenario(nodes, playerGoal: 11, companionGoal: 10,
                Plate("switch_a", 0, 1, releaseDoor: 0, minX: -1.4f),
                Latch("switch_b", 5, 3), Exit("exit_player", 11, 0), Exit("exit_companion", 10, 1));
            var initial = Observation(player: 2, companion: 1);
            var plan = CooperativePlanner.Search(model, initial);
            Check(ActsOn(plan, 0) && plan.route.SequenceEqual(new[] { nodes[1].edges[0] }),
                "Demo starts by sending the companion to the pressure plate.");
            var repeated = CooperativePlanner.Search(model, initial);
            Check(plan.steps.SequenceEqual(repeated.steps) && plan.route.SequenceEqual(repeated.route),
                "Identical observations produce the same plan and route.");

            initial.companion = 0;
            initial.companionPressure = 1;
            plan = CooperativePlanner.Search(model, initial);
            Check(ActsOn(plan, 0) && plan.route.Length == 0,
                "A useful plate already occupied by the companion is committed without travel.");

            initial.held = 0;
            plan = CooperativePlanner.Search(model, initial);
            Check(WaitsForHuman(plan), "The protected hold continues while the human still needs to cross and latch.");

            initial.player = 5;
            initial.latched = 3;
            plan = CooperativePlanner.Search(model, initial);
            Check(ActsOn(plan, 3) && plan.route.Length == 10 &&
                plan.route.Any(edge => edge.gate == 0) && plan.route.Any(edge => edge.gate == 1),
                "Once the latch and boundary are observed, the companion takes its own exit.");
        }

        private static void ReversedRoles()
        {
            var nodes = Nodes(6);
            Connect(nodes, 0, 1);
            var toLatch = Connect(nodes, 2, 3, gate: 0);
            Connect(nodes, 3, 4, gate: 1);
            Connect(nodes, 1, 5, gate: 1);
            Connect(nodes, 0, 5, gate: 1);
            var model = Scenario(nodes, playerGoal: 5, companionGoal: 4,
                Exit("machine-looking exit", 5, 0), Latch("blue tab", 3, 3),
                Exit("human-looking exit", 4, 1), Plate("yellow tile", 1, 1));
            var initial = Observation(player: 0, companion: 2);
            var plan = CooperativePlanner.Search(model, initial);
            Check(WaitsForHuman(plan) && plan.steps[0] == "player (predicted): yellow tile",
                "The human can be predicted to hold a renamed plate in a directed layout.");

            initial.playerPressure = 1; // Actual overlap even though the human is off the plate's authored node.
            model.initialCompanionTargets = new[] { false, true, false, false };
            plan = CooperativePlanner.Search(model, initial);
            Check(ActsOn(plan, 1) && plan.route.SequenceEqual(new[] { toLatch }),
                "Observed human pressure supports a companion crossing to the latch.");
            Check(plan.steps.Contains("companion: human-looking exit") &&
                plan.steps.Contains("player (predicted): machine-looking exit"),
                "Exit ownership follows the role assignment.");
        }

        private static void BranchedSwitchHandoff()
        {
            var nodes = Nodes(11);
            nodes[4].x = 6;
            nodes[5].x = 14;
            nodes[6].x = -4;
            Connect(nodes, 1, 10); // A closer plate leads to a hold with no safe release.
            Connect(nodes, 1, 2);
            var toFirstPlate = Connect(nodes, 2, 3);
            Connect(nodes, 0, 4, gate: 3);
            Connect(nodes, 4, 5, gate: 1);
            var toLowerPassage = Connect(nodes, 3, 9, gate: 1);
            var toSecondLatch = Connect(nodes, 9, 6, gate: 0);
            Connect(nodes, 5, 7, gate: 2);
            Connect(nodes, 6, 8, gate: 2);

            var firstPlate = Plate("bridge pedal", 3, 8, releaseDoor: 3, minX: 10);
            var secondLatch = Latch("lower wheel", 6, 5);
            var companionExit = Exit("south arch", 8, 1);
            var model = Scenario(nodes, playerGoal: 7, companionGoal: 8,
                secondLatch, Plate("spur pedal", 10, 8), companionExit, Plate("upper pedal", 5, 1),
                firstPlate, Exit("north arch", 7, 0), Latch("upper wheel", 4, 10));
            int firstHold = Array.IndexOf(model.targets, firstPlate);
            int secondWheel = Array.IndexOf(model.targets, secondLatch);
            int companionGoal = Array.IndexOf(model.targets, companionExit);

            var initial = Observation(player: 0, companion: 1);
            var plan = CooperativePlanner.Search(model, initial);
            Check(ActsOn(plan, firstHold) && plan.route.SequenceEqual(new[] { nodes[1].edges[1], toFirstPlate }),
                "The companion takes the longer branch to the releasable pressure plate.");
            Check(plan.steps.Take(3).SequenceEqual(new[] {
                "companion: bridge pedal", "player (predicted): upper pedal", "companion: lower wheel"
            }), "A four-switch plan hands pressure duty from the companion to the human.");

            initial = Observation(player: 4, companion: 3);
            initial.held = firstHold;
            initial.companionPressure = 8;
            initial.latched = 10;
            plan = CooperativePlanner.Search(model, initial);
            Check(WaitsForHuman(plan) && plan.steps[0] == "player (predicted): upper pedal",
                "The first latch alone is insufficient to release the hold or cross the second passage.");

            initial.player = 5;
            initial.playerPressure = 1;
            plan = CooperativePlanner.Search(model, initial);
            Check(ActsOn(plan, secondWheel) && plan.route.SequenceEqual(new[] { toLowerPassage, toSecondLatch }),
                "The companion can leave the first hold and use the passage powered by the human.");

            initial = Observation(player: 5, companion: 6);
            initial.latched = 15;
            plan = CooperativePlanner.Search(model, initial);
            Check(ActsOn(plan, companionGoal) && plan.steps.Contains("player (predicted): north arch"),
                "The second latch unlocks the two assigned exits after the human leaves the plate.");
        }

        private static void ReleaseBoundary()
        {
            var nodes = Nodes(6);
            nodes[1].x = 2.01f;
            nodes[2].x = 4;
            Connect(nodes, 1, 2);
            Connect(nodes, 2, 3, gate: 1);
            Connect(nodes, 0, 4, gate: 0);
            Connect(nodes, 4, 5, gate: 1);
            var model = Scenario(nodes, playerGoal: 3, companionGoal: 5,
                Plate("anchor", 0, 1, releaseDoor: 0, minX: 2),
                Latch("unblock exit", 4, 2), Exit("one", 3, 0), Exit("two", 5, 1));
            var initial = Observation(player: 1, companion: 0);
            initial.held = 0;
            initial.companionPressure = 1;
            initial.latched = 1;
            initial.playerX = 1.99f;
            var plan = CooperativePlanner.Search(model, initial);
            Check(WaitsForHuman(plan) && plan.steps[0] == "player (predicted): walk to x 4",
                "An unsafe observed position prevents release even when the authored node is beyond the boundary.");

            initial.player = 2;
            initial.playerX = 4;
            initial.latched = 0;
            plan = CooperativePlanner.Search(model, initial);
            Check(!plan.found && plan.target == -1, "An open pressure input does not substitute for the release latch.");

            initial.latched = 1;
            plan = CooperativePlanner.Search(model, initial);
            Check(ActsOn(plan, 1), "The companion leaves only after both release requirements are observed.");
        }

        private static void MovingActorCannotPowerItsGate()
        {
            var nodes = Nodes(4);
            Connect(nodes, 0, 1, gate: 0);
            Connect(nodes, 1, 2);
            var model = Scenario(nodes, playerGoal: 2, companionGoal: 3,
                Plate("departing tile", 0, 1), Plate("arriving tile", 1, 1),
                Exit("human exit", 2, 0), Exit("companion exit", 3, 1));
            var initial = Observation(player: 0, companion: 3);
            initial.playerPressure = 1;
            var plan = CooperativePlanner.Search(model, initial);
            Check(!plan.found && plan.diagnostic == "no_cooperative_route",
                "The same moving actor cannot transfer pressure between plates across their gate.");

            model.targets[0].releaseUnconditionally = true;
            model.playerGoal = 3;
            model.companionGoal = 2;
            model.targets[2].owner = 1;
            model.targets[3].owner = 0;
            initial = Observation(player: 3, companion: 0);
            initial.held = 0;
            initial.companionPressure = 1;
            plan = CooperativePlanner.Search(model, initial);
            Check(!plan.found, "Permission to leave a plate does not make its gate safe to cross.");
        }

        private static void AllowedActionsAndExactRoute()
        {
            var nodes = Nodes(7);
            Connect(nodes, 0, 1);
            var toSecondPlate = Connect(nodes, 0, 2);
            Connect(nodes, 1, 6, gate: 1);
            Connect(nodes, 2, 6, gate: 1);
            Connect(nodes, 3, 4, gate: 0);
            Connect(nodes, 4, 5, gate: 1);
            var model = Scenario(nodes, playerGoal: 5, companionGoal: 6,
                Plate("first", 1, 1, releaseDoor: 0), Plate("second", 2, 1, releaseDoor: 0),
                Latch("latch", 4, 3), Exit("human finish", 5, 0), Exit("companion finish", 6, 1));
            model.initialCompanionTargets = new[] { false, true, false, false, false };
            var plan = CooperativePlanner.Search(model, Observation(player: 3, companion: 0));
            Check(ActsOn(plan, 1) && plan.route.SequenceEqual(new[] { toSecondPlate }),
                "An unavailable first action does not hide an equivalent available action.");

            nodes = Nodes(6);
            Connect(nodes, 0, 1);
            Connect(nodes, 1, 4);
            var bypassFirst = Connect(nodes, 0, 2);
            var bypassSecond = Connect(nodes, 2, 3);
            var bypassThird = Connect(nodes, 3, 4);
            model = Scenario(nodes, playerGoal: 5, companionGoal: 4,
                Plate("irrelevant plate", 1, 1), Exit("human finish", 5, 0), Exit("companion finish", 4, 1));
            plan = CooperativePlanner.Search(model, Observation(player: 5, companion: 0));
            Check(ActsOn(plan, 2) && plan.route.SequenceEqual(new[] { bypassFirst, bypassSecond, bypassThird }),
                "The returned route avoids a shorter path through a plate with no safe release.");
        }

        private static void BudgetAndUnreachable()
        {
            var nodes = Nodes(3);
            Connect(nodes, 0, 1);
            var model = Scenario(nodes, playerGoal: 2, companionGoal: 1,
                Exit("human finish", 2, 0), Exit("companion finish", 1, 1));
            var initial = Observation(player: 2, companion: 0);
            model.maxWork = 1;
            var plan = CooperativePlanner.Search(model, initial);
            Check(BudgetReached(plan) && plan.work == 1, "The global work counter stays within its limit.");

            model.maxWork = 100;
            model.maxRouteExpanded = 1;
            plan = CooperativePlanner.Search(model, initial);
            Check(BudgetReached(plan), "A truncated route search is reported separately from a proven dead end.");

            model.maxRouteExpanded = 100;
            model.maxExpanded = 1;
            plan = CooperativePlanner.Search(model, initial);
            Check(BudgetReached(plan) && plan.expanded == 1, "The joint-state limit is honored.");

            model.maxExpanded = 100;
            initial.player = 0;
            plan = CooperativePlanner.Search(model, initial);
            Check(!plan.found && plan.target == -1 && plan.route.Length == 0 &&
                plan.diagnostic == "no_cooperative_route", "An unreachable directed exit produces no action.");

            initial = Observation(player: 2, companion: 1);
            plan = CooperativePlanner.Search(model, initial);
            Check(plan.found && plan.target == -1 && plan.steps.Length == 0 &&
                plan.diagnostic == "both_at_exits", "An already complete observation produces no action.");
        }

        private static Model Scenario(Node[] nodes, int playerGoal, int companionGoal, params Target[] targets)
        {
            return new Model { nodes = nodes, targets = targets, playerGoal = playerGoal, companionGoal = companionGoal };
        }

        private static Node[] Nodes(int count)
        {
            return Enumerable.Range(0, count).Select(i => new Node { x = i }).ToArray();
        }

        private static Edge Connect(Node[] nodes, int from, int to, int gate = -1)
        {
            var edge = new Edge { to = to, gate = gate };
            nodes[from].edges = nodes[from].edges.Concat(new[] { edge }).ToArray();
            return edge;
        }

        private static Target Plate(string id, int node, ulong opens, int releaseDoor = -1, float minX = float.NegativeInfinity)
        {
            return new Target { id = id, node = node, pressure = true, opens = opens, releaseDoor = releaseDoor, releasePlayerMinX = minX };
        }

        private static Target Latch(string id, int node, ulong opens)
        {
            return new Target { id = id, node = node, latch = true, opens = opens };
        }

        private static Target Exit(string id, int node, int owner)
        {
            return new Target { id = id, node = node, owner = owner };
        }

        private static State Observation(int player, int companion)
        {
            return new State { player = player, companion = companion, held = -1 };
        }

        private static bool ActsOn(Plan plan, int target) => plan.found && !plan.waitForPlayer && plan.target == target;
        private static bool WaitsForHuman(Plan plan) => plan.found && plan.waitForPlayer && plan.target == -1 && plan.route.Length == 0;
        private static bool BudgetReached(Plan plan) => !plan.found && plan.diagnostic == "planning_budget_reached";

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
