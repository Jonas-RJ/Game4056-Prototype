using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AutonomousCompanion.Demo.Testing
{
    public sealed class RelayLevelPlaytestHarness : MonoBehaviour
    {
        private const float FirstTakeoff = -3.9f;
        private const float FirstLanding = -1.4f;
        private const float SecondTakeoff = 3.4f;
        private const float SecondLanding = 5.9f;
        private const float StartPlatformY = .65f;
        private const float MiddlePlatformY = 1.35f;
        private const float PositionTolerance = .06f;
        private static readonly WaitForFixedUpdate PhysicsTick = new WaitForFixedUpdate();

        [Serializable]
        private sealed class CheckResult
        {
            public string name;
            public bool passed;
            public int circuit;
            public string evidence;
        }

        [Serializable]
        private sealed class SuiteResult
        {
            public string status = "idle";
            public string phase = "idle";
            public string failure;
            public float elapsed_seconds;
            public int completed_circuits;
            public bool level_completed;
            public bool player_alive;
            public bool companion_alive;
            public Vector2 player_position;
            public Vector2 companion_position;
            public List<CheckResult> checks = new List<CheckResult>();
        }

        public DemoLevel level;
        public bool IsRunning => report.status == "running";
        public string ResultJson => JsonUtility.ToJson(report);

        private SuiteResult report = new SuiteResult();
        private RelayManualControls controls;
        private bool restoreControls;
        private float started;
        private int circuit;

        public void BeginSuite()
        {
            if (IsRunning) return;
            StopAllCoroutines();
            report = new SuiteResult { status = "running", phase = "scene_setup" };
            started = Time.realtimeSinceStartup;
            circuit = 0;
            if (!Application.isPlaying || !isActiveAndEnabled || level == null ||
                level.Player == null || level.Companion == null || level.SwitchA == null ||
                level.SwitchB == null || level.DoorA == null || level.DoorB == null ||
                level.PlayerExit == null || level.CompanionExit == null ||
                level.SwitchA.arrivalNode == null || level.SwitchB.arrivalNode == null ||
                level.PlayerExit.arrivalNode == null || level.CompanionExit.arrivalNode == null ||
                level.PlayerExit.trigger == null || level.CompanionExit.trigger == null ||
                level.DoorA.blocker == null || level.DoorB.blocker == null)
            {
                Fail("Run in Play Mode with the relay level references assigned.");
                return;
            }
            var manualControls = level.GetComponent<RelayManualControls>();
            if (manualControls == null || level.Player.allowKeyboard || level.Companion.allowKeyboard ||
                (level.Brain != null && (level.Brain.isActiveAndEnabled ||
                    (level.Brain.executor != null && level.Brain.executor.isActiveAndEnabled) ||
                    (level.Brain.client != null && (level.Brain.client.isActiveAndEnabled || level.Brain.client.IsBusy)))))
            {
                Fail("Relay manual controls must own both actors, and the brain, executor and HTTP client must be off.");
                return;
            }

            controls = manualControls;
            restoreControls = controls.InputEnabled;
            controls.InputEnabled = false;
            StartCoroutine(RunSuite());
        }

        private void Update()
        {
            if (IsRunning && Time.realtimeSinceStartup - started > 30f)
                Fail("Suite timed out after 30 seconds.");
        }

        private IEnumerator RunSuite()
        {
            foreach (var step in Steps())
            {
                yield return step;
                if (!IsRunning) yield break;
            }
            report.phase = "finished";
            Finish("passed");
        }

        private IEnumerable<IEnumerator> Steps()
        {
            yield return ResetAndCheck("initial_reset");
            yield return TryClosedDoor();
            yield return PressA();
            yield return MoveTo(level.Player, -4.1f, "player_walks_through_open_a");
            yield return Check("a_stays_open_during_crossing", AOpenTemporarily, Snapshot);
            yield return MoveTo(level.Companion, level.companionSpawn.x, "companion_leaves_unlatched_a");
            yield return Check("a_recloses_without_b", () => !level.SwitchA.Occupied &&
                !level.SwitchA.Active && DoorClosed(level.DoorA) && DoorClosed(level.DoorB), Snapshot);
            yield return WalkIntoFirstPit();
            yield return ResetAndCheck("reset_after_death");

            circuit = 1;
            foreach (var step in CompleteCircuit(true)) yield return step;

            yield return ResetAndCheck("reset_after_completion");
            circuit = 2;
            foreach (var step in CompleteCircuit(false)) yield return step;
        }

        private IEnumerable<IEnumerator> CompleteCircuit(bool checkExits)
        {
            var player = level.Player;
            var companion = level.Companion;
            yield return PressA();
            yield return MoveTo(player, FirstTakeoff, "player_first_takeoff");
            yield return JumpTo(player, FirstLanding, MiddlePlatformY, "player_first_gap");
            yield return Check("first_landing_before_b", AOpenTemporarily, Snapshot);
            yield return MoveTo(player, level.SwitchB.arrivalNode.transform.position.x, "player_steps_on_b");
            yield return Check("b_latches_both_doors", () => level.SwitchA.Occupied &&
                level.SwitchB.Occupied && BothDoorsLatched() && level.SwitchA.CanRelease, Snapshot);

            yield return MoveTo(companion, FirstTakeoff, "companion_leaves_a_and_crosses_door_a");
            yield return JumpTo(companion, FirstLanding, MiddlePlatformY, "companion_first_gap");
            yield return MoveTo(player, SecondTakeoff, "player_leaves_b_and_crosses_door_b");
            yield return Check("both_doors_stay_open_after_switches_vacate", () => !level.SwitchA.Occupied &&
                !level.SwitchA.Active && !level.SwitchB.Occupied && BothDoorsLatched(), Snapshot);
            yield return JumpTo(player, SecondLanding, StartPlatformY, "player_second_gap");
            yield return MoveTo(player, ExitX(level.PlayerExit), "player_reaches_own_exit_first");
            yield return Check("player_exit_alone_does_not_complete", () => player.IsGrounded &&
                level.PlayerExit.Occupied && !level.CompanionExit.Occupied && !level.Completed, Snapshot);

            yield return MoveTo(companion, SecondTakeoff, "companion_crosses_door_b");
            yield return JumpTo(companion, SecondLanding, StartPlatformY, "companion_second_gap");
            if (checkExits)
            {
                yield return MoveTo(player, ExitX(level.CompanionExit), "player_steps_on_companion_exit");
                yield return MoveTo(companion, ExitX(level.PlayerExit), "companion_steps_on_player_exit");
                yield return Check("swapped_exits_do_not_complete", () =>
                    PhysicallyOverlaps(player, level.CompanionExit) && PhysicallyOverlaps(companion, level.PlayerExit) &&
                    player.IsGrounded && companion.IsGrounded &&
                    !level.PlayerExit.Occupied && !level.CompanionExit.Occupied && !level.Completed, Snapshot);

                float jumpStart = ExitX(level.PlayerExit) - .65f;
                yield return MoveTo(player, jumpStart, "player_prepares_exit_jump");
                yield return MoveTo(companion, ExitX(level.CompanionExit), "companion_reaches_own_exit_first");
                yield return Check("companion_exit_alone_does_not_complete", () => companion.IsGrounded &&
                    level.CompanionExit.Occupied && !level.PlayerExit.Occupied && !level.Completed, Snapshot);
                yield return JumpTo(player, ExitX(level.PlayerExit), StartPlatformY, "player_exit_airborne_then_grounded", level.PlayerExit);
            }
            else
            {
                yield return MoveTo(companion, ExitX(level.CompanionExit), "companion_reaches_own_exit");
            }

            yield return Check("both_living_grounded_actors_at_assigned_exits", () => level.Completed &&
                player.IsAlive && companion.IsAlive && player.IsGrounded && companion.IsGrounded &&
                level.PlayerExit.Occupied && level.CompanionExit.Occupied, Snapshot);
            report.completed_circuits++;
        }

        private IEnumerator ResetAndCheck(string phase)
        {
            report.phase = phase;
            level.ResetLevel();
            yield return PhysicsTick;
            yield return PhysicsTick;
            yield return Check(phase, () => !level.Completed &&
                level.Player.IsAlive && level.Companion.IsAlive &&
                Vector2.Distance(level.Player.Position, level.playerSpawn) < .15f &&
                Vector2.Distance(level.Companion.Position, level.companionSpawn) < .15f &&
                !level.SwitchA.Active && !level.SwitchB.Active && !level.SwitchA.CanRelease &&
                DoorClosed(level.DoorA) && DoorClosed(level.DoorB) &&
                !level.PlayerExit.Occupied && !level.CompanionExit.Occupied, Snapshot);
        }

        private IEnumerator TryClosedDoor()
        {
            report.phase = "remote_a_and_closed_door";
            var player = level.Player;
            float initialX = player.Position.x;
            float wallX = level.DoorA.blocker.bounds.min.x - player.GetComponent<Collider2D>().bounds.extents.x;
            float deadline = Time.realtimeSinceStartup + .55f;
            if (level.SwitchA.CanHold(level.Companion) || level.SwitchA.CanHold(player))
            {
                Fail("An actor could hold A from its spawn instead of the pressure plate.");
                yield break;
            }
            Record("remote_a_hold_unavailable", true, Snapshot());

            player.SetTestInput(1f, false);
            while (IsRunning && Time.realtimeSinceStartup < deadline)
            {
                if (level.SwitchA.Occupied || !DoorClosed(level.DoorA) || player.Position.x > wallX + .12f)
                {
                    Fail("A activated remotely or the player crossed its closed blocker.");
                    yield break;
                }
                yield return PhysicsTick;
            }
            if (!IsRunning) yield break;

            player.SetTestInput(0f, false);
            yield return Check("closed_a_physically_blocks_player", () => player.Position.x > initialX + .25f &&
                Mathf.Abs(player.Position.x - wallX) < .12f && Mathf.Abs(player.Body.linearVelocity.x) < .08f,
                () => $"Player walked from {initialX:F2} to the closed blocker at x={wallX:F2}. {Snapshot()}");
        }

        private IEnumerator PressA()
        {
            yield return MoveTo(level.Companion, level.SwitchA.arrivalNode.transform.position.x, "companion_steps_on_a");
            if (!IsRunning) yield break;
            yield return Check("a_opens_only_while_occupied_before_b", AOpenTemporarily, Snapshot);
        }

        private IEnumerator WalkIntoFirstPit()
        {
            report.phase = "walk_into_first_pit";
            var player = level.Player;
            float deadline = Time.realtimeSinceStartup + 3f;
            while (IsRunning && player.IsAlive && Time.realtimeSinceStartup < deadline)
            {
                player.SetTestInput(InputTowards(player, -2.65f), false);
                yield return PhysicsTick;
            }
            if (!IsRunning) yield break;

            player.SetTestInput(0f, false);
            yield return Check("pit_fall_kills_player_without_completing", () => !player.IsAlive &&
                player.Position.y <= player.deathY && level.Companion.IsAlive && !level.Completed, Snapshot);
        }

        private IEnumerator MoveTo(DemoActor actor, float x, string phase)
        {
            report.phase = phase;
            float deadline = Time.realtimeSinceStartup + 4f;
            while (IsRunning && actor.IsAlive && Mathf.Abs(actor.Position.x - x) > PositionTolerance)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Fail($"{actor.name} could not walk to x={x:F2}.");
                    yield break;
                }
                actor.SetTestInput(InputTowards(actor, x), false);
                yield return PhysicsTick;
            }
            if (!IsRunning) yield break;

            actor.SetTestInput(0f, false);
            yield return PhysicsTick;
            yield return PhysicsTick;
            if (IsRunning && (!actor.IsAlive || !actor.IsGrounded || Mathf.Abs(actor.Position.x - x) > PositionTolerance + .02f))
                Fail($"{actor.name} did not arrive alive and grounded at x={x:F2}.");
        }

        private IEnumerator JumpTo(DemoActor actor, float x, float landingY, string phase, DemoExit airborneExit = null)
        {
            report.phase = phase;
            if (!actor.IsAlive || !actor.IsGrounded)
            {
                Fail($"{actor.name} was not alive and grounded at takeoff.");
                yield break;
            }

            Vector2 takeoff = actor.Position;
            float peakY = takeoff.y;
            float deadline = Time.realtimeSinceStartup + 2.5f;
            bool sawAirborneExit = false;
            actor.SetTestInput(InputTowards(actor, x), true);
            yield return PhysicsTick;
            while (IsRunning && actor.IsAlive && Time.realtimeSinceStartup < deadline)
            {
                var position = actor.Position;
                peakY = Mathf.Max(peakY, position.y);
                bool grounded = actor.IsGrounded;
                bool atTarget = Mathf.Abs(position.x - x) <= PositionTolerance;
                if (airborneExit != null && !grounded && PhysicallyOverlaps(actor, airborneExit))
                {
                    if (airborneExit.Occupied || !level.CompanionExit.Occupied || level.Completed)
                    {
                        Fail("The player's airborne exit overlap did not preserve the incomplete state with the companion already at its exit.");
                        yield break;
                    }
                    if (!sawAirborneExit)
                        Record("airborne_exit_overlap_does_not_complete", true, Snapshot());
                    sawAirborneExit = true;
                }

                if (atTarget && grounded && Mathf.Abs(position.y - landingY) < .15f && peakY - takeoff.y > .5f)
                {
                    actor.SetTestInput(0f, false);
                    if (airborneExit != null && !sawAirborneExit)
                    {
                        Fail("The player never overlapped its own exit while airborne.");
                        yield break;
                    }
                    Record(phase, true, $"{actor.name} jumped from ({takeoff.x:F2}, {takeoff.y:F2}) " +
                        $"to ({position.x:F2}, {position.y:F2}), peak y={peakY:F2}.");
                    yield return PhysicsTick;
                    yield return PhysicsTick;
                    yield break;
                }

                actor.SetTestInput(InputTowards(actor, x), false);
                yield return PhysicsTick;
            }
            if (IsRunning) Fail($"{actor.name} did not land at x={x:F2}, y={landingY:F2}; peak y={peakY:F2}.");
        }

        private IEnumerator Check(string name, Func<bool> condition, Func<string> evidence)
        {
            report.phase = name;
            if (condition()) Record(name, true, evidence());
            else Fail(evidence());
            yield break;
        }

        private bool AOpenTemporarily()
        {
            return level.SwitchA.Occupied && !level.SwitchA.Latched && !level.SwitchA.CanRelease &&
                !level.SwitchB.Active && level.DoorA.IsOpen && !level.DoorA.Latched &&
                !level.DoorA.blocker.enabled && DoorClosed(level.DoorB);
        }

        private bool BothDoorsLatched()
        {
            return level.SwitchB.Latched && level.DoorA.Latched && level.DoorB.Latched &&
                level.DoorA.IsOpen && level.DoorB.IsOpen && !level.DoorA.blocker.enabled && !level.DoorB.blocker.enabled;
        }

        private static bool DoorClosed(DemoDoor door) => !door.IsOpen && !door.Latched && door.blocker.enabled;
        private static float ExitX(DemoExit exit) => exit.arrivalNode.transform.position.x;
        private static bool PhysicallyOverlaps(DemoActor actor, DemoExit exit) =>
            actor.GetComponent<Collider2D>().bounds.Intersects(exit.trigger.bounds);

        private static float InputTowards(DemoActor actor, float x) =>
            Mathf.Clamp((x - actor.Position.x) / (actor.moveSpeed * Time.fixedDeltaTime), -1f, 1f);

        private string Snapshot()
        {
            return $"Player {level.Player.Position:F2}, alive={level.Player.IsAlive}, grounded={level.Player.IsGrounded}. " +
                $"Companion {level.Companion.Position:F2}, alive={level.Companion.IsAlive}, grounded={level.Companion.IsGrounded}. " +
                $"A occupied={level.SwitchA.Occupied}, releasable={level.SwitchA.CanRelease}, door open={level.DoorA.IsOpen}, latched={level.DoorA.Latched}. " +
                $"B occupied={level.SwitchB.Occupied}, latched={level.SwitchB.Latched}, door open={level.DoorB.IsOpen}, latched={level.DoorB.Latched}. " +
                $"Player exit={level.PlayerExit.Occupied}, companion exit={level.CompanionExit.Occupied}, completed={level.Completed}.";
        }

        private void Record(string name, bool passed, string evidence)
        {
            report.checks.Add(new CheckResult { name = name, passed = passed, circuit = circuit, evidence = evidence });
        }

        private void Fail(string reason)
        {
            if (!IsRunning) return;
            report.failure = reason;
            Record(report.phase, false, reason);
            Finish("failed");
        }

        private void Finish(string status)
        {
            report.status = status;
            report.elapsed_seconds = Time.realtimeSinceStartup - started;
            if (level != null)
            {
                report.level_completed = level.Completed;
                if (level.Player != null)
                {
                    report.player_alive = level.Player.IsAlive;
                    report.player_position = level.Player.Position;
                    if (controls != null) level.Player.ClearTestInput();
                }
                if (level.Companion != null)
                {
                    report.companion_alive = level.Companion.IsAlive;
                    report.companion_position = level.Companion.Position;
                    if (controls != null) level.Companion.ClearTestInput();
                }
            }
            if (controls != null)
            {
                controls.InputEnabled = restoreControls;
                controls = null;
            }
            if (status == "failed") Debug.LogError("RELAY_PLAYTEST " + ResultJson, this);
            else Debug.Log("RELAY_PLAYTEST " + ResultJson, this);
        }

        private void OnDisable()
        {
            if (!IsRunning) return;
            StopAllCoroutines();
            report.failure = "Harness disabled during the suite.";
            Finish("cancelled");
        }
    }
}
