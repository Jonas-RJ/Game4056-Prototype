Autonomous Companion - Runtime Integration Kit

This folder is an importable Unity runtime module, not a finished scene or trained movement model.
It selects its own tasks without player task assignment. The model decides; local adapters execute.
No secrets are stored here. DecisionHttpClient calls YOUR backend /decision endpoint.

SETUP IN AN EXISTING UNITY PROJECT
1. Copy this folder into Assets/AutonomousCompanion/Runtime (preserve file names).
2. Implement CompanionActorAdapter for both player observation and companion control. The player
   adapter's traversal methods can reject calls: this kit never commands the player adapter.
   Bind companion BeginTraversal/TickTraversal to your existing motor. Own precise jump/landing
   control there. StopLocomotion must preserve gravity and safe landing behavior.
3. Author NavigationNode directed links for verified walk, jump and drop transitions. Set gate
   conditions for doors/moving platforms. CanTraverse must check character abilities and hazards.
   CurrentNode is non-null only when actually at that node. Do not map nearest nodes through walls.
4. Add CompanionTarget components for observable objects. Include non-interactive doors and hazards
   so the model receives their states; set their kind, controls and live Active override correctly.
   Create a target subclass for pressure switches: SupportsHold=true, implement CanHold/BeginHold,
   IsHolding/MaintainHold/ReleaseHold against real physical occupancy. A hold requires confirmation.
   Assign a LocalCondition subclass to safeReleaseCondition that confirms safe release from live
   state, e.g. player fully beyond the door collision region AND an alternate path/latched door
   lets the companion leave. Missing release condition means never release a committed hold.
5. Add CompanionWorld, assign player/companion and initialTargets (unique nonempty IDs). Dynamic
   spawns call Register/Unregister. NotifyChanged on important state changes (not every frame):
   player reversal/approaching a barrier, death, door change, route invalidation or platform arrival.
6. Add CompanionExecutor, DecisionHttpClient and AutonomousCompanionBrain; assign references in
   Inspector before Play, and make executor.world equal brain.world. Enable/disable the module as
   a group. Client backend default is http://127.0.0.1:8765/decision. Use HTTPS for deployed service;
   provide backend authentication in your integration if needed, never an upstream DeepSeek key.
7. Start the backend separately. Brain begins autonomous decisions after debounce, and rechecks
   periodically. All HTTP work is asynchronous; local traversal runs in FixedUpdate.

IMPORTANT GUARANTEES AND INTEGRATION RESPONSIBILITIES
- Only authored graph routes are exposed; there is no inferred straight-line navigation fallback.
- The model can select only current valid action/target pairs. wait/empty is always advertised.
- Response request_id/state_version match the dispatched snapshot; event generation and current
  action preconditions are checked again at application. Important events obsolete old responses.
- One HTTP call is active at a time. Failure/fallback triggers exponential backoff; a valid current
  task keeps running. Fallback never replaces a task and never releases a switch.
- wait never releases a held switch. Release requires an explicit different model task AND a live
  safe release predicate AND adapter confirmation. The model cannot override local protection.
- A missing model response cannot guarantee task completion; the kit preserves local safe behavior.
- If a target/node is disabled/destroyed, call Unregister / NotifyChanged and update link gates.
- Hooks must be called on Unity main thread. Model API is not allowed to run arbitrary C#.
- Autonomous choices are not proven correct merely by schema validity: test actual puzzle variants.
- This scaffold has no ready-made motor, switch collider, scene, or platform-jump physics. These are
  deliberately integration adapters to avoid conflicting with an existing game's controller.

DIAGNOSTICS
Brain.LastRequest / LastDiagnostic / Diagnostic, Executor.CurrentTask / LastDiagnostic /
HasProtectedHold / TaskChanged, and Client.IsBusy / LastError / NextAttemptTime can feed a test
harness or your existing debug UI. No player command UI is introduced.

OFFLINE CHECKS TO RUN AFTER SCENE INTEGRATION
Use a local stub backend. Inject delayed responses, invalid action/target pairs, stale request IDs,
model fallback, and network failure. Check the occupied switch never releases under those cases.
Test player reversal, missing routes, changed doors, death, jump failure and reset. Observe BOTH
characters completing the puzzle without task commands. Do not count an offline rule fixture as
proof that the live model chose correct actions.

DEATH / RESPAWN / RESET
If either actor dies, Brain cancels pending requests, invalidates decision generation and suspends
travel. It does not resume just because an actor becomes alive again. Your game's reset sequence
must first reset physical actors, switches, doors and occupancy, then call
AutonomousCompanionBrain.ResetAfterWorldReset(). This clears stale task and switch references,
clears historical events and network backoff, and starts a fresh observation cycle. Do not use
this API to bypass a live protected switch; it is a lifecycle hook for an already-reset world.
CompanionActorAdapter.CanTraverse checks route/ability constraints even while traversing; put
"must be grounded before starting" in CanStartTraversal, not CanTraverse.
