using UnityEngine;

namespace AutonomousCompanion
{
    /// <summary>A synchronous, bounded local decision source. Never controls the human actor.</summary>
    public abstract class LocalDecisionProvider : MonoBehaviour
    {
        /// <summary>For a movement decision, return the authored links selected by the planner.
        /// An empty route means the companion is already at the target.</summary>
        public abstract DecisionResponse Decide(DecisionRequest request, out NavigationLink[] route);
    }
}
