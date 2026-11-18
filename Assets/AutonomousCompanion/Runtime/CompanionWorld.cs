using System;
using System.Collections.Generic;
using UnityEngine;
namespace AutonomousCompanion
{
    public sealed class CompanionWorld : MonoBehaviour
    {
        public CompanionActorAdapter player;
        public CompanionActorAdapter companion;
        [SerializeField] private CompanionTarget[] initialTargets = Array.Empty<CompanionTarget>();
        private readonly Dictionary<string, CompanionTarget> targets = new Dictionary<string, CompanionTarget>();
        public long StateVersion { get; private set; }
        public event Action<string> Changed;
        private readonly Queue<string> recentEvents = new Queue<string>();
        public IEnumerable<CompanionTarget> Targets { get { return targets.Values; } }
        private void Awake() { foreach (var t in initialTargets) Register(t); }
        /// <summary>Call on important events: door change, player reversal, death, route blocked.
        /// Do not call every frame for ordinary position changes.</summary>
        public void NotifyChanged(string description)
        {
            StateVersion++;
            recentEvents.Enqueue(description ?? "world_changed");
            while (recentEvents.Count > 12) recentEvents.Dequeue();
            Changed?.Invoke(description);
        }
        public bool Register(CompanionTarget target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.targetId)) return false;
            if (targets.TryGetValue(target.targetId, out var existing)) return existing == target;
            targets.Add(target.targetId, target); NotifyChanged("target_registered:" + target.targetId); return true;
        }
        public void Unregister(CompanionTarget target)
        {
            if (target != null && targets.TryGetValue(target.targetId, out var found) && found == target)
            { targets.Remove(target.targetId); NotifyChanged("target_removed:" + target.targetId); }
        }
        public CompanionTarget Find(string id)
        { return id != null && targets.TryGetValue(id, out var target) ? target : null; }
        public string[] RecentEvents() { return recentEvents.ToArray(); }
        public void ResetAfterWorldReset()
        { recentEvents.Clear(); NotifyChanged("world_reset"); }
        /// <summary>BFS through authored directed links and live actor constraints; no straight-line fallback.</summary>
        public bool TryRoute(NavigationNode destination, out List<NavigationLink> route)
        {
            route = new List<NavigationLink>();
            var start = companion != null ? companion.CurrentNode : null;
            if (start == null || destination == null || !companion.IsAlive) return false;
            if (start == destination) return true;
            var queue = new Queue<NavigationNode>();
            var previous = new Dictionary<NavigationNode, NavigationNode>();
            var incoming = new Dictionary<NavigationNode, NavigationLink>();
            queue.Enqueue(start); previous[start] = null;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var link in current.links ?? Array.Empty<NavigationLink>())
                {
                    if (link == null || !link.IsOpen || !companion.CanTraverse(link) || previous.ContainsKey(link.destination)) continue;
                    previous[link.destination] = current; incoming[link.destination] = link;
                    if (link.destination == destination)
                    {
                        var cursor = destination;
                        while (cursor != start) { route.Add(incoming[cursor]); cursor = previous[cursor]; }
                        route.Reverse(); return true;
                    }
                    queue.Enqueue(link.destination);
                }
            }
            return false;
        }
    }
}
