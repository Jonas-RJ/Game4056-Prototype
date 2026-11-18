using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace AutonomousCompanion.Vision
{
    /// <summary>Camera observations in, bounded horizontal and jump input out.</summary>
    [DefaultExecutionOrder(-105)]
    [DisallowMultipleComponent]
    public sealed partial class VisionAgent : MonoBehaviour
    {
        private const int MaxResponseBytes = 32 * 1024;
        private const int MaxMemory = 8;
        private const int MaxAuditEntries = 512;
        private const int FeedbackDecisions = 3;
        private const int IneffectiveActionLimit = 2;
        private const int WaitDecisionLimit = 3;
        private const int WaitCyclesBeforeEscalation = 2;
        private const int MotionHistoryLength = 6;
        private const float MovementEpsilon = .075f;
        private const float ReturnDistance = .3f;
        private const float WaitInputSeconds = 2f;
        private const float WaitElapsedSeconds = 6f;
        public const float MaxJumpSeconds = 1.25f;
        private const string DefaultGoal = "Help the human player. Both characters should reach their assigned exits alive.";
        private const string SafetyGuidance = "Review the latest image and local safety feedback. Revise the approach before repeating a blocked input. Choose a short, reversible action or wait for cooperation.";
        private const string MovementGuidance = "Compare the latest image with earlier frames. Revise the approach after repeated ineffective movement. Choose a short, reversible action or wait for cooperation.";
        private const string WaitGuidance = "Review what has changed for both actors during the wait. Reassess whether a short, safe action can help. Continue waiting if cooperation still requires it.";
        private const string SustainedWaitGuidance = "Re-evaluate memory from the latest image. Verify the companion's exact visible position and contact before assuming the intended result. With no visible progress from waiting, try a short, safe exploratory move to test a different hypothesis. If waiting is needed, identify the visible evidence and expected change.";
        private const string SustainedWaitEventGuidance = "Verify the companion's visible position and contact against memory. Try a short safe move if waiting shows no progress. If waiting, identify the expected change.";
        private const string ShortReplanGuidance = "Reassess the latest image and recent control outcomes.";
        private const string ProlongedWait = "prolonged_wait";
        private const string MotorNotes = "Control facts: ground input lasts duration_seconds. A directional jump holds horizontal input until landing or 1.25s. Jump requires ground contact. No midair double jump.";
        private const string PlayNotes = "Control tips: use 0.05-0.15s ground steps near edges/targets; jump before losing support. Verify visible contact and effects. Treat remembered mechanism explanations as tentative.";
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        }) { Timeout = Timeout.InfiniteTimeSpan };

        public Camera gameCamera;
        public VisionActorAdapter actor;
        public string endpoint = "http://127.0.0.1:8766/vision-decision";
        [TextArea] public string goal = DefaultGoal;
        [Range(320, 960)] public int captureLongEdge = 768;
        [Range(.2f, 3f)] public float minObservationInterval = .35f;
        [Range(2, 90)] public int requestTimeoutSeconds = 25;

        public string Status { get; private set; } = "disabled";
        public string LastResponseReason { get; private set; } = "";
        public string SessionId { get; private set; }
        public int FramesCaptured { get; private set; }
        public int NetworkRequests { get; private set; }
        public int ModelDecisionsAccepted { get; private set; }
        public int FixtureDecisionsAccepted { get; private set; }
        public int ModelActionsApplied { get; private set; }
        public int FixtureActionsApplied { get; private set; }
        public int LocalVetoes { get; private set; }
        public int LocalCorrections { get; private set; }
        public int LocalReplans { get; private set; }
        public int RejectedResponses { get; private set; }
        public int FallbackResponses { get; private set; }
        public int ResetCount { get; private set; }
        public int LastFrameWidth { get; private set; }
        public int LastFrameHeight { get; private set; }
        public int LastFrameBytes => lastJpeg?.Length ?? 0;
        public bool WaitingForResponse => pending != null;
        public bool ApplyingAction => command != null;
        public bool OwnsCompanionControl => ownsControl;
        public IReadOnlyList<string> Memory => memory;
        public AuditEntry[] GetAuditTrail() => audit.ToArray();

        [Serializable]
        public sealed class AuditEntry
        {
            public string kind, source, session_id, request_id, reason, detail;
            public int frame_id, horizontal;
            public bool jump;
            public float duration_seconds, elapsed_seconds, unscaled_time;
            public Vector3 position;
        }

        internal Func<VisionRequest, VisionResponse> FixtureResponder { get; set; }
        internal float FixtureDelaySeconds { get; set; } = .2f;
        internal VisionRequest PendingRequest => pending;

        private VisionActorAdapter boundActor;
        private bool ownsControl, suspended;
        private int frame, failures;
        private float nextObservation, fixtureReplyAt;
        private double pendingCapturedAt;
        private VisionRequest pending;
        private CancellationTokenSource pendingCancellation;
        private Task<TransportResult> pendingTransport;
        private Func<VisionRequest, VisionResponse> pendingFixture;
        private readonly Queue<string> events = new Queue<string>();
        private readonly Queue<AuditEntry> audit = new Queue<AuditEntry>();
        private readonly Queue<MotionSample> motionHistory = new Queue<MotionSample>();
        private string[] memory = Array.Empty<string>();
        private RenderTexture renderTarget;
        private Texture2D readback;
        private byte[] lastJpeg;
        private VisionAction previous = Neutral();
        private VisionAction command;
        private string commandRequestId;
        private int commandFrame;
        private bool commandStarted, commandFromFixture, airborne;
        private float commandElapsed;
        private Vector3 commandStartPosition;
        private int ineffectiveActions, consecutiveWaits, repeatedSafetyStops;
        private float waitStartedAt, waitInputSeconds;
        private int totalWaits, waitReplans, firstWaitFrame;
        private float waitEpisodeStartedAt;
        private Vector3 waitEpisodePosition;
        private int stoppedHorizontal;
        private bool stoppedJump;
        private string replanReason, replanEvidence, replanGuidance;
        private int feedbackRemaining;

        private struct MotionSample
        {
            public Vector3 start;
            public int direction;
        }

        private void OnEnable()
        {
            boundActor = actor;
            if (boundActor != null) boundActor.WorldReset += ResetSession;
            StartSession("session_started");
        }

        private void OnDisable()
        {
            CancelPending();
            StopPlan("disabled");
            StopAction();
            if (boundActor != null)
            {
                boundActor.WorldReset -= ResetSession;
                if (ownsControl) boundActor.ReleaseControl(this);
            }
            ownsControl = false;
            boundActor = null;
            ReleaseCapture();
            Status = "disabled";
        }

        private void ResetSession()
        {
            ResetCount++;
            bool previousAttemptEnded = suspended;
            StartSession("world_reset", preserveMemory: true);
            if (previousAttemptEnded) AddEvent("previous_attempt_ended");
        }

        public void ClearMemory()
        {
            if (isActiveAndEnabled) StartSession("memory_cleared");
            else memory = Array.Empty<string>();
        }

        private void StartSession(string reason, bool preserveMemory = false)
        {
            ResetPlanSession();
            CancelPending();
            StopAction();
            SessionId = Guid.NewGuid().ToString("N");
            frame = 0;
            failures = 0;
            suspended = false;
            previous = Neutral();
            if (!preserveMemory) memory = Array.Empty<string>();
            LastResponseReason = "";
            events.Clear();
            motionHistory.Clear();
            ineffectiveActions = 0;
            repeatedSafetyStops = 0;
            ResetWaitEpisode();
            feedbackRemaining = 0;
            replanReason = null;
            replanEvidence = null;
            replanGuidance = null;
            AddEvent(reason);
            nextObservation = Time.unscaledTime + .1f;
            Status = reason;
        }

        private void LateUpdate()
        {
            if (boundActor == null || gameCamera == null || !gameCamera.isActiveAndEnabled)
            {
                CancelPending();
                StopAction();
                Status = "scene_references_unavailable";
                return;
            }
            if (!boundActor.CanAct)
            {
                Suspend();
                return;
            }
            if (suspended || Time.timeScale <= 0f) return;
            if (!ownsControl)
            {
                ownsControl = boundActor.TryAcquireControl(this);
                if (!ownsControl) { Status = "companion_control_unavailable"; return; }
            }

            if (pending != null)
            {
                PollPending();
                return;
            }
            if (activePlan != null)
            {
                SampleVisualHold();
                return;
            }
            if (command != null || Time.unscaledTime < nextObservation) return;
            Observe();
        }

        private void FixedUpdate()
        {
            if (!ownsControl || boundActor == null) return;
            if (!boundActor.CanAct) { Suspend(); return; }
            if (useShortPlans) { TickPlan(); return; }
            if (command == null) return;

            bool grounded = boundActor.IsGrounded;
            if (commandStarted)
            {
                airborne |= !grounded;
                // The grounded sensor may stay true for a step after queuing the jump.
                bool inJump = command.jump && commandElapsed < MaxJumpSeconds &&
                    (airborne ? !grounded : commandElapsed < .15f);
                if (commandElapsed >= command.duration_seconds && !inJump)
                {
                    FinishAction(grounded);
                    return;
                }
            }

            bool starting = !commandStarted;
            bool jump = starting && command.jump;
            if (jump && !grounded)
            {
                StopUnsafeAction("Jump unavailable without ground contact.");
                return;
            }
            if (starting) commandStartPosition = boundActor.CameraFocus;
            if (!boundActor.CheckControlSafety(command.horizontal, jump, out string safetyReason))
            {
                StopUnsafeAction(safetyReason);
                return;
            }
            if (!boundActor.ApplyControl(this, command.horizontal, jump))
            {
                Suspend();
                return;
            }
            if (starting)
            {
                previous = new VisionAction
                {
                    horizontal = command.horizontal,
                    jump = jump,
                    duration_seconds = command.duration_seconds
                };
                commandStarted = true;
                if (command.horizontal == 0 && !jump && consecutiveWaits == 0)
                    BeginWaitCycle();
                if (commandFromFixture) FixtureActionsApplied++;
                else ModelActionsApplied++;
                AddEvent("control_applied");
                AddAudit("action_applied");
                Status = commandFromFixture ? "fixture_action_applied" : "model_action_applied";
            }
            commandElapsed += Time.fixedDeltaTime;
        }

        private void StopUnsafeAction(string reason)
        {
            bool interrupted = commandStarted;
            string kind = interrupted ? "local_correction" : "local_veto";
            reason = string.IsNullOrWhiteSpace(reason) ? "Local physics check rejected the input." : Limit(reason.Trim(), 160);
            if (interrupted)
            {
                LocalCorrections++;
                RememberAppliedDuration();
            }
            else LocalVetoes++;

            if (stoppedHorizontal != command.horizontal || stoppedJump != command.jump)
                repeatedSafetyStops = 0;
            stoppedHorizontal = command.horizontal;
            stoppedJump = command.jump;
            repeatedSafetyStops++;

            string input = DescribeControl(command);
            string outcome = interrupted ? $"Stopped {input} during execution." : $"Blocked {input} before execution.";
            string repeat = repeatedSafetyStops > 1 ? $" Same input stopped {repeatedSafetyStops} times." : "";
            string evidence = $"{outcome} {Limit(reason, 96)}{repeat}";
            AddAudit(kind, reason, evidence);
            RequestReplan(kind, evidence, SafetyGuidance, .2f * Mathf.Min(repeatedSafetyStops, 6));
            ResetWaitCycle();
            StopAction();
            Status = kind;
        }

        private void FinishAction(bool grounded)
        {
            RememberAppliedDuration();
            Status = "action_finished";
            if (command.horizontal == 0 && !command.jump) TrackWait();
            else TrackMovement(grounded);
            StopAction();
        }

        private void BeginWaitCycle()
        {
            waitStartedAt = Time.unscaledTime;
            if (totalWaits > 0) return;
            waitEpisodeStartedAt = waitStartedAt;
            waitEpisodePosition = commandStartPosition;
            firstWaitFrame = commandFrame;
        }

        private void TrackWait()
        {
            if (MovedFromWaitPosition(boundActor.CameraFocus))
            {
                ResetWaitEpisode();
                return;
            }

            consecutiveWaits++;
            totalWaits++;
            waitInputSeconds += commandElapsed;
            float elapsed = Time.unscaledTime - waitStartedAt;
            if (consecutiveWaits < WaitDecisionLimit ||
                (waitInputSeconds < WaitInputSeconds && elapsed < WaitElapsedSeconds)) return;

            waitReplans++;
            if (waitReplans < WaitCyclesBeforeEscalation)
            {
                string evidence = FormattableString.Invariant(
                    $"The companion applied {consecutiveWaits} consecutive neutral waits ({waitInputSeconds:F1}s of input across {elapsed:F1}s elapsed).");
                RequestReplan(ProlongedWait, evidence, WaitGuidance);
            }
            else
            {
                float episodeSeconds = Time.unscaledTime - waitEpisodeStartedAt;
                int freshObservations = commandFrame - firstWaitFrame;
                string evidence = FormattableString.Invariant(
                    $"After {totalWaits} neutral waits and {freshObservations} fresh observations over {episodeSeconds:F1}s, the companion is still near the same position. This is wait reassessment {waitReplans}.");
                RequestReplan(ProlongedWait, evidence, SustainedWaitGuidance);
            }
            ResetWaitCycle();
        }

        private bool MovedFromWaitPosition(Vector3 position) =>
            (position - waitEpisodePosition).sqrMagnitude >= MovementEpsilon * MovementEpsilon;

        private void ResetWaitCycle()
        {
            consecutiveWaits = 0;
            waitInputSeconds = 0f;
        }

        private void ResetWaitEpisode()
        {
            ResetWaitCycle();
            totalWaits = 0;
            waitReplans = 0;
            if (replanReason == ProlongedWait) feedbackRemaining = 0;
        }

        private void TrackMovement(bool grounded)
        {
            ResetWaitCycle();
            Vector3 end = boundActor.CameraFocus;
            Vector3 displacement = end - commandStartPosition;
            bool movedHorizontally = displacement.x * command.horizontal >= MovementEpsilon;
            bool landedAtNewHeight = command.jump && grounded && Mathf.Abs(displacement.y) >= MovementEpsilon;
            if (!movedHorizontally && !landedAtNewHeight)
            {
                ineffectiveActions++;
                if (ineffectiveActions >= IneffectiveActionLimit)
                {
                    RequestReplan("ineffective_movement", "Two applied movement inputs produced little change in companion position.", MovementGuidance);
                    ineffectiveActions = 0;
                    motionHistory.Clear();
                }
                return;
            }

            ResetWaitEpisode();
            ineffectiveActions = 0;
            repeatedSafetyStops = 0;
            if (command.jump || !grounded)
            {
                motionHistory.Clear();
                return;
            }

            if (motionHistory.Count >= MotionHistoryLength) motionHistory.Dequeue();
            motionHistory.Enqueue(new MotionSample { start = commandStartPosition, direction = command.horizontal });
            if (motionHistory.Count < 4 || (end - motionHistory.Peek().start).sqrMagnitude > ReturnDistance * ReturnDistance)
                return;

            int previousDirection = 0;
            int reversals = 0;
            foreach (var sample in motionHistory)
            {
                if (previousDirection != 0 && sample.direction != previousDirection) reversals++;
                previousDirection = sample.direction;
            }
            if (reversals < 3) return;

            RequestReplan("repeated_reversal", "Several left and right inputs returned the companion to nearly the same position.", MovementGuidance);
            motionHistory.Clear();
        }

        private void RememberAppliedDuration() => previous.duration_seconds = Mathf.Clamp(commandElapsed, .05f, 1f);

        private void RequestReplan(string reason, string evidence, string guidance, float delay = .1f)
        {
            LocalReplans++;
            replanReason = reason;
            replanEvidence = Limit(evidence, 180);
            replanGuidance = guidance;
            feedbackRemaining = FeedbackDecisions;
            AddEvent(replanEvidence);
            AddAudit("replan", reason, replanEvidence);
            nextObservation = Mathf.Max(nextObservation, Time.unscaledTime + delay);
            Status = "replan_requested";
        }

        private void Suspend()
        {
            if (suspended) return;
            suspended = true;
            StopPlan("actor_unavailable");
            CancelPending();
            StopAction();
            Status = "awaiting_world_reset";
        }

        private void Observe()
        {
            nextObservation = Time.unscaledTime + Mathf.Clamp(minObservationInterval, .2f, 3f);
            var fixture = FixtureResponder;
            if (useShortPlans && !PlanMayObserve()) return;
            if (fixture == null && !PlanMailboxActive && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                !uri.IsLoopback || uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo)))
            {
                FailTransport("invalid_loopback_endpoint");
                return;
            }

            Vector3 position = boundActor.CameraFocus;
            if (totalWaits > 0 && MovedFromWaitPosition(position)) ResetWaitEpisode();
            Vector3 viewport = gameCamera.WorldToViewportPoint(position);
            if (viewport.z <= 0 || viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1)
            {
                AddEvent("companion_outside_camera");
                Status = "companion_outside_camera";
                return;
            }

            pendingCapturedAt = Time.realtimeSinceStartupAsDouble;
            try { lastJpeg = Capture(); }
            catch (Exception exception)
            {
                FailTransport("camera_capture_failed:" + exception.GetType().Name);
                return;
            }
            if (lastJpeg == null || lastJpeg.Length == 0)
            {
                FailTransport("camera_capture_empty");
                return;
            }

            if (useShortPlans && !PlanFrameChanged()) return;
            FramesCaptured++;
            string observedGoal = ObservationGoal(out bool guidanceInGoal);
            pending = useShortPlans ? CreatePlanObservation() : new VisionRequest();
            pending.request_id = Guid.NewGuid().ToString("N");
            pending.session_id = SessionId;
            pending.frame_id = ++frame;
            pending.image_base64 = Convert.ToBase64String(lastJpeg);
            pending.image_mime = "image/jpeg";
            pending.goal = observedGoal;
            pending.actor = new VisionActorObservation
            {
                alive = boundActor.IsAlive,
                grounded = boundActor.IsGrounded,
                screen_x = viewport.x,
                screen_y = 1f - viewport.y
            };
            pending.previous_action = previous;
            pending.recent_events = ObservationEvents(guidanceInGoal);
            pending.memory = useShortPlans ? Array.Empty<string>() : memory;
            if (pending is VisionPlanRequest planRequest)
            {
                ReservePlanRequest();
                planRequest.jump_options = boundActor.ObserveJumpOptions(gameCamera, pendingLeft.x, pendingRight.x);
                planRequest.jump_option_ids = useJumpOptionIds;
                if (planRequest.jump_option_ids)
                    for (int i = 0; i < planRequest.jump_options.Length; i++)
                        planRequest.jump_options[i].option_id = planRequest.request_id + ":" + (i + 1);
            }
            if (useShortPlans && PlanMailboxActive) { ExportPlanRequest(); return; }
            pendingFixture = fixture;
            if (fixture != null) fixtureReplyAt = Time.unscaledTime + Mathf.Clamp(FixtureDelaySeconds, .05f, 5f);
            else
            {
                pendingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Mathf.Clamp(requestTimeoutSeconds, 2, 90)));
                NetworkRequests++;
                pendingTransport = Post(endpoint, JsonUtility.ToJson(pending), pendingCancellation.Token);
            }
            Status = fixture != null ? "awaiting_fixture_response" : "awaiting_model_response";
        }

        private void PollPending()
        {
            var request = pending;
            if (useShortPlans && PlanMailboxActive) { PollPlanMailbox(); return; }
            if (pendingFixture != null)
            {
                if (Time.unscaledTime >= fixtureReplyAt)
                    Accept(request, pendingFixture(request), true);
                return;
            }
            if (pendingTransport == null || !pendingTransport.IsCompleted) return;
            var result = pendingTransport.GetAwaiter().GetResult();
            if (result.Error != null) { CancelPending(); FailTransport(result.Error); return; }
            VisionResponse response = null;
            try { response = JsonUtility.FromJson<VisionResponse>(result.Json); }
            catch (ArgumentException) { }
            Accept(request, response, false);
        }

        internal bool DeliverFixtureResponse(VisionRequest request, VisionResponse response)
        {
            if (FixtureResponder == null) return false;
            return Accept(request, response, true);
        }

        private bool Accept(VisionRequest request, VisionResponse response, bool fixture)
        {
            if (!isActiveAndEnabled || !ownsControl || suspended || boundActor == null ||
                !boundActor.CanAct || request == null || !ReferenceEquals(request, pending) || request.session_id != SessionId)
            {
                RejectedResponses++;
                return false;
            }
            CancelPending();
            if (PendingObservationAge > Mathf.Clamp(requestTimeoutSeconds, 2, 90))
                return Reject("response_observation_expired");
            if (response == null || response.request_id != request.request_id || response.session_id != SessionId ||
                response.frame_id != request.frame_id)
                return Reject("response_correlation_mismatch");

            LastResponseReason = Limit(response.reason ?? "", 240);
            if (response.source == "fallback" && !fixture)
            {
                FallbackResponses++;
                FailTransport("backend_fallback");
                return false;
            }
            if (useShortPlans) return AcceptPlan(request, response, fixture);
            if (response.source != (fixture ? "fixture" : "model") || !ValidAction(response.action))
                return Reject("invalid_action_response");

            command = response.action;
            commandRequestId = request.request_id;
            commandFrame = request.frame_id;
            commandStarted = false;
            commandFromFixture = fixture;
            airborne = false;
            commandElapsed = 0;
            failures = 0;
            memory = BoundedMemory(response.memory);
            if (feedbackRemaining > 0) feedbackRemaining--;
            if (fixture) FixtureDecisionsAccepted++;
            else ModelDecisionsAccepted++;
            AddAudit("decision_accepted", LastResponseReason);
            Status = fixture ? "fixture_action_ready" : "model_action_ready";
            return true;
        }

        private bool Reject(string reason)
        {
            RejectedResponses++;
            FailTransport(reason);
            return false;
        }

        private void FailTransport(string reason)
        {
            if (useShortPlans) PlanTransportFailed(reason);
            failures = Mathf.Min(failures + 1, 5);
            nextObservation = Time.unscaledTime + Mathf.Min(12f, .5f * Mathf.Pow(2f, failures - 1));
            StopAction();
            AddEvent(reason);
            Status = reason;
        }

        private void StopAction()
        {
            command = null;
            commandStarted = false;
            if (ownsControl && boundActor != null) boundActor.StopControl(this);
        }

        private void CancelPending()
        {
            pending = null;
            pendingFixture = null;
            pendingTransport = null;
            if (pendingCancellation == null) return;
            pendingCancellation.Cancel();
            pendingCancellation.Dispose();
            pendingCancellation = null;
        }

        private byte[] Capture()
        {
            CapturePixels();
            return readback.EncodeToJPG(78);
        }

        private void CapturePixels()
        {
            float aspect = Mathf.Clamp(gameCamera.aspect, .25f, 4f);
            int edge = Mathf.Clamp(captureLongEdge, 320, 960);
            int width = aspect >= 1f ? edge : Mathf.RoundToInt(edge * aspect);
            int height = aspect >= 1f ? Mathf.RoundToInt(edge / aspect) : edge;
            if (renderTarget == null || renderTarget.width != width || renderTarget.height != height)
            {
                ReleaseCapture();
                renderTarget = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                { name = "VisionCameraFrame" };
                renderTarget.Create();
                readback = new Texture2D(width, height, TextureFormat.RGB24, false);
            }

            var originalActive = RenderTexture.active;
            var originalTarget = gameCamera.targetTexture;
            try
            {
                if (GraphicsSettings.currentRenderPipeline != null)
                {
                    var request = new RenderPipeline.StandardRequest { destination = renderTarget };
                    if (!RenderPipeline.SupportsRenderRequest(gameCamera, request))
                        throw new NotSupportedException("The active render pipeline cannot capture the game camera.");
                    RenderPipeline.SubmitRenderRequest(gameCamera, request);
                }
                else
                {
                    gameCamera.targetTexture = renderTarget;
                    gameCamera.Render();
                }
                RenderTexture.active = renderTarget;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                LastFrameWidth = width;
                LastFrameHeight = height;
            }
            finally
            {
                gameCamera.targetTexture = originalTarget;
                RenderTexture.active = originalActive;
            }
        }

        private void ReleaseCapture()
        {
            if (renderTarget != null) { renderTarget.Release(); Destroy(renderTarget); }
            if (readback != null) Destroy(readback);
            renderTarget = null;
            readback = null;
        }

        public bool SaveLastFrame(string absolutePath)
        {
            if (lastJpeg == null || string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath)) return false;
            File.WriteAllBytes(absolutePath, lastJpeg);
            return true;
        }

        private void AddEvent(string description)
        {
            if (events.Count >= 8) events.Dequeue();
            events.Enqueue(Limit(description, 180));
        }

        private string[] ObservationEvents(bool guidanceInGoal)
        {
            var result = new List<string>(events);
            string safety = boundActor.LocalSafetyObservation;
            if (!string.IsNullOrWhiteSpace(safety)) result.Add(Limit(safety, 180));
            if (feedbackRemaining > 0)
            {
                if (!result.Contains(replanEvidence)) result.Add(replanEvidence);
                if (!guidanceInGoal)
                    result.Add(replanGuidance == SustainedWaitGuidance ? SustainedWaitEventGuidance : Limit(replanGuidance, 180));
            }
            if (!useShortPlans) { result.Add(MotorNotes); result.Add(PlayNotes); }
            else result.Add($"Local short-plan execution enabled. Remaining request budget: {Mathf.Max(0, maxPlanRequests - PlanRequests)}. Wait completion does not verify mechanisms.");
            while (result.Count > 12) result.RemoveAt(0);
            return result.ToArray();
        }

        private string ObservationGoal(out bool guidanceInGoal)
        {
            string requestedGoal = Limit(string.IsNullOrWhiteSpace(goal) ? DefaultGoal : goal, 400);
            guidanceInGoal = false;
            if (feedbackRemaining <= 0) return requestedGoal;

            // The bridge passes recent events as evidence and this field as the current goal.
            int available = 399 - requestedGoal.Length;
            string guidance = replanGuidance.Length <= available ? replanGuidance : ShortReplanGuidance;
            if (guidance.Length > available) return requestedGoal;
            guidanceInGoal = guidance == replanGuidance;
            return requestedGoal + " " + guidance;
        }

        private void AddAudit(string kind, string reason = "", string detail = "")
        {
            if (audit.Count >= MaxAuditEntries) audit.Dequeue();
            audit.Enqueue(new AuditEntry
            {
                kind = kind,
                source = commandFromFixture ? "fixture" : "model",
                session_id = SessionId,
                request_id = commandRequestId,
                frame_id = commandFrame,
                horizontal = command.horizontal,
                jump = command.jump,
                duration_seconds = command.duration_seconds,
                elapsed_seconds = commandElapsed,
                unscaled_time = Time.unscaledTime,
                position = boundActor.CameraFocus,
                reason = reason,
                detail = detail
            });
        }

        private static string DescribeControl(VisionAction action)
        {
            if (action.horizontal == 0) return action.jump ? "jump in place" : "neutral wait";
            string direction = action.horizontal < 0 ? "left" : "right";
            return action.jump ? $"{direction} jump" : $"{direction} movement";
        }

        private static string[] BoundedMemory(string[] values, int maxItems = MaxMemory)
        {
            if (values == null) return Array.Empty<string>();
            var result = new List<string>(maxItems);
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) result.Add(Limit(value, 160));
                if (result.Count == maxItems) break;
            }
            return result.ToArray();
        }

        private static string Limit(string value, int length)
        {
            if (value.Length <= length) return value;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length);
        }

        private static bool ValidAction(VisionAction action) => action != null &&
            action.horizontal >= -1 && action.horizontal <= 1 &&
            action.duration_seconds >= .05f && action.duration_seconds <= 1f;

        private static VisionAction Neutral() => new VisionAction { horizontal = 0, jump = false, duration_seconds = .15f };

        private sealed class TransportResult
        {
            public string Json, Error;
        }

        private static async Task<TransportResult> Post(string url, string json, CancellationToken cancellation)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return new TransportResult { Error = "backend_http_" + (int)response.StatusCode };
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var body = new byte[MaxResponseBytes + 1];
                int length = 0;
                while (length < body.Length)
                {
                    int read = await stream.ReadAsync(body, length, body.Length - length, cancellation).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
                return length > MaxResponseBytes ? new TransportResult { Error = "backend_response_too_large" } :
                    new TransportResult { Json = Encoding.UTF8.GetString(body, 0, length) };
            }
            catch (OperationCanceledException) { return new TransportResult { Error = "backend_timeout_or_cancellation" }; }
            catch (Exception exception) { return new TransportResult { Error = "backend_transport_error:" + exception.GetType().Name }; }
        }
    }
}
