using System;

namespace AutonomousCompanion
{
    [Serializable] public sealed class ActorState
    {
        public string id; public float x; public float y; public bool grounded; public bool alive;
    }
    [Serializable] public sealed class WorldObjectState
    {
        public string id; public string kind; public float x; public float y;
        public bool active; public bool reachable; public string[] controls;
    }
    [Serializable] public sealed class ActionOption
    {
        public string action; public string target_id;
        public ActionOption(string action, string targetId) { this.action = action; target_id = targetId; }
    }
    [Serializable] public sealed class TaskState
    {
        public string action = "wait"; public string target_id = "";
        public string status = "running"; public string reason = "initial";
    }
    [Serializable] public sealed class DecisionRequest
    {
        public string request_id; public long state_version; public string goal;
        public ActorState player; public ActorState companion; public WorldObjectState[] objects;
        public string[] events; public TaskState current_task; public ActionOption[] allowed_actions;
    }
    [Serializable] public sealed class DecisionResponse
    {
        public string request_id; public long state_version; public string action;
        public string target_id; public string reason; public string source;
    }
    public enum TraversalResult { Running, Succeeded, Failed }
}
