using System;
using UnityEngine;
namespace AutonomousCompanion
{
    /// <summary>Directed, authored reachability. A jump is a link with a traversal tag understood
    /// by the game's motor adapter. Gate conditions must reflect current door/platform state.</summary>
    public sealed class NavigationNode : MonoBehaviour
    {
        public NavigationLink[] links = Array.Empty<NavigationLink>();
    }
    [Serializable] public sealed class NavigationLink
    {
        public NavigationNode destination;
        public string traversal = "walk";
        public LocalCondition gate;
        public bool IsOpen { get { return destination != null && (gate == null || gate.IsSatisfied); } }
    }
}
