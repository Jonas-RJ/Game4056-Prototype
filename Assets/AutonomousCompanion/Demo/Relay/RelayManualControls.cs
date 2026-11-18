using UnityEngine;
using UnityEngine.InputSystem;

namespace AutonomousCompanion.Demo
{
    [DefaultExecutionOrder(-110)]
    public sealed class RelayManualControls : MonoBehaviour
    {
        public DemoLevel level;

        // Disable keyboard ownership before a playtest drives the actor motors.
        public bool InputEnabled
        {
            get => enabled;
            set => enabled = value;
        }

        private bool ownsMotorInput;

        private bool CanMove => level != null && !level.Completed &&
            level.player != null && level.player.IsAlive &&
            level.companion != null && level.companion.IsAlive;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (level != null && keyboard != null && keyboard.rKey.wasPressedThisFrame)
            {
                level.ResetLevel();
                ownsMotorInput = false;
                return;
            }

            if (keyboard == null || !CanMove)
            {
                ReleaseMotorInput();
                return;
            }

            Drive(level.player,
                keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed,
                keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed,
                keyboard.spaceKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame ||
                    keyboard.upArrowKey.wasPressedThisFrame);
            Drive(level.companion, keyboard.jKey.isPressed, keyboard.lKey.isPressed,
                keyboard.iKey.wasPressedThisFrame);
            ownsMotorInput = true;
        }

        private void FixedUpdate()
        {
            // Several physics steps can run before the next keyboard sample.
            if (ownsMotorInput && !CanMove) ReleaseMotorInput();
        }

        private static void Drive(DemoActor actor, bool left, bool right, bool jumpPressed)
        {
            float movement = (right ? 1f : 0f) - (left ? 1f : 0f);
            actor.SetTestInput(movement, false);
            if (jumpPressed) actor.SetTestInput(movement, true);
        }

        private void OnDisable() => ReleaseMotorInput();

        private void ReleaseMotorInput()
        {
            if (!ownsMotorInput) return;
            ownsMotorInput = false;
            if (level == null) return;
            ReleaseActor(level.player);
            ReleaseActor(level.companion);
        }

        private static void ReleaseActor(DemoActor actor)
        {
            if (actor == null || actor.HasAutonomousControl) return;
            actor.ClearTestInput();
            actor.StopLocomotion();
        }
    }
}
