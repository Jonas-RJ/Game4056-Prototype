using UnityEngine;
namespace AutonomousCompanion
{
    /// <summary>Bridge your existing character controller. CurrentNode is non-null only when
    /// physically at that authored node; never infer it by nearest distance through walls.</summary>
    public abstract class CompanionActorAdapter : MonoBehaviour
    {
        public abstract Vector2 Position { get; }
        public abstract bool IsGrounded { get; }
        public abstract bool IsAlive { get; }
        public abstract NavigationNode CurrentNode { get; }
        /// <summary>Whether a new traversal may safely start at this instant.</summary>
        public abstract bool CanStartTraversal { get; }
        /// <summary>Must evaluate actor-specific hazards and ability, as well as link geometry.</summary>
        public abstract bool CanTraverse(NavigationLink link);
        /// <summary>Called once to begin, then TickTraversal during subsequent physics updates.
        /// Only report Succeeded when physically at link.destination. This adapter owns jump timing.</summary>
        public abstract bool BeginTraversal(NavigationLink link);
        public abstract TraversalResult TickTraversal(float fixedDeltaTime);
        /// <summary>Cancel horizontal intent safely; preserve gravity and safe landing control.</summary>
        public abstract void StopLocomotion();
        public ActorState Capture(string id)
        {
            Vector2 p = Position;
            return new ActorState { id = id, x = p.x, y = p.y, grounded = IsGrounded, alive = IsAlive };
        }
    }
}
