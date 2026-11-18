using System;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    /// <summary>Scene references, physical reset, and observable completion only.
    /// This coordinator never issues companion actions or decides who presses a switch.</summary>
    [DefaultExecutionOrder(100)]
    public sealed class DemoLevel : MonoBehaviour
    {
        public DemoActor player;
        public DemoActor companion;
        public AutonomousCompanionBrain brain;
        public CompanionWorld world;
        public DemoPressureSwitch switchA;
        public DemoPressureSwitch switchB;
        public DemoDoor doorA;
        public DemoDoor doorB;
        public DemoExit playerExit;
        public DemoExit companionExit;
        public Vector2 playerSpawn = new Vector2(-5f, 0.65f);
        public Vector2 companionSpawn = new Vector2(-7f, 0.65f);
        public bool Completed { get; private set; }
        private bool brainEnabledBeforeCompletion;
        private bool executorEnabledBeforeCompletion;
        public DemoActor Player { get { return player; } }
        public DemoActor Companion { get { return companion; } }
        public AutonomousCompanionBrain Brain { get { return brain; } }
        public DemoPressureSwitch SwitchA { get { return switchA; } }
        public DemoPressureSwitch SwitchB { get { return switchB; } }
        public DemoDoor DoorA { get { return doorA; } }
        public DemoDoor DoorB { get { return doorB; } }
        public DemoExit PlayerExit { get { return playerExit; } }
        public DemoExit CompanionExit { get { return companionExit; } }
        public event Action CompletedChanged;

        private void Start()
        {
            if (world == null) return;
            Register(switchA); Register(switchB); Register(playerExit); Register(companionExit);
            if (doorA != null) Register(doorA.observable);
            if (doorB != null) Register(doorB.observable);
            world.NotifyChanged("level_started:both_exits_required;switch_a_holds_door_a;switch_b_latches_both_doors");
        }
        private void Register(CompanionTarget target)
        {
            if (target != null && !world.Register(target))
                Debug.LogError("Duplicate or empty companion target ID: " + target.targetId, target);
        }
        private void FixedUpdate()
        {
            if (Completed || player == null || companion == null || playerExit == null || companionExit == null) return;
            if (!player.IsAlive || !companion.IsAlive || !playerExit.Occupied || !companionExit.Occupied) return;
            Completed = true;
            if (brain != null)
            {
                brainEnabledBeforeCompletion = brain.enabled;
                executorEnabledBeforeCompletion = brain.executor != null && brain.executor.enabled;
                if (brain.client != null) brain.client.CancelPending();
                brain.enabled = false;
                if (brain.executor != null) brain.executor.enabled = false;
            }
            player.StopLocomotion(); companion.StopLocomotion();
            if (world != null) world.NotifyChanged("level_completed:both_actors_physically_at_exits");
            CompletedChanged?.Invoke();
        }
        public void ResetLevel()
        {
            bool wasBrainEnabled = brain != null && brain.enabled;
            bool hadCompleted = Completed;
            if (brain != null)
            {
                if (brain.client != null) brain.client.CancelPending();
                brain.enabled = false;
            }
            if (player != null) player.ResetActor(playerSpawn);
            if (companion != null) companion.ResetActor(companionSpawn);
            if (switchA != null) switchA.ResetSwitch();
            if (switchB != null) switchB.ResetSwitch();
            if (doorA != null) doorA.ResetDoor();
            if (doorB != null) doorB.ResetDoor();
            if (playerExit != null) playerExit.ResetExit();
            if (companionExit != null) companionExit.ResetExit();
            Completed = false;
            Physics2D.SyncTransforms();
            if (switchA != null && switchA.safeReleaseCondition is DemoSafeReleaseCondition condition) condition.ResetCondition();
            if (brain != null)
            {
                brain.ResetAfterWorldReset();
                if (hadCompleted && brain.executor != null) brain.executor.enabled = executorEnabledBeforeCompletion;
                brain.enabled = wasBrainEnabled || (hadCompleted && brainEnabledBeforeCompletion);
            }
            else if (world != null) world.ResetAfterWorldReset();
            if (hadCompleted) CompletedChanged?.Invoke();
        }
    }
}
