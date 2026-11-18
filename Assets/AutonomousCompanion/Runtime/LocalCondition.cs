using UnityEngine;
namespace AutonomousCompanion
{
    /// <summary>Implement against live game state. Used for route gates and safe switch release.</summary>
    public abstract class LocalCondition : MonoBehaviour
    {
        public abstract bool IsSatisfied { get; }
    }
}
