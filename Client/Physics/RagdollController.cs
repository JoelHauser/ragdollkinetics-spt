using System.Collections.Generic;
using BepInEx.Configuration;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using UnityEngine;

namespace RagdollKinetics.Patches
{
    internal sealed class RagdollPoseSampler : MonoBehaviour
    {
        internal readonly struct PoseMotion
        {
            internal readonly Quaternion LocalRotation;
            internal readonly Vector3 LocalAngularVelocity;

            internal PoseMotion(Quaternion rotation, Vector3 velocity)
            {
                LocalRotation = rotation;
                LocalAngularVelocity = velocity;
            }
        }

        internal readonly struct RootMotion
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal readonly Vector3 HorizontalVelocity;
            internal readonly float YawVelocity;
            internal readonly float PoseLevel;

            internal RootMotion(Vector3 position, Quaternion rotation,
                Vector3 velocity, float yawVelocity, float poseLevel)
            {
                Position = position;
                Rotation = rotation;
                HorizontalVelocity = velocity;
                YawVelocity = yawVelocity;
                PoseLevel = poseLevel;
            }
        }

        private readonly Dictionary<string, Quaternion> _previousBoneRotations =
            new Dictionary<string, Quaternion>(20);
        private readonly Dictionary<string, PoseMotion> _boneMotionByName =
            new Dictionary<string, PoseMotion>(20);
        private CharacterJointSpawner[] _spawners;
        private Transform _pelvis;
        private Player _player;
        private Vector3 _previousPelvisPosition;
        private Quaternion _previousPelvisRotation;
        private RootMotion _rootMotion;
        private bool _initialized;
        private bool _hasRootMotion;
        private bool _frozen;

        internal void CapturePoseMotion(float deltaTime)
        {
            if (_frozen || deltaTime <= 0.0001f) return;
            if (!_initialized) InitializePoseCapture();

            float inverseDelta = 1f / deltaTime;
            foreach (CharacterJointSpawner spawner in _spawners)
            {
                if (spawner == null) continue;
                string key = spawner.gameObject.name;
                Quaternion current = spawner.transform.localRotation;
                Vector3 angularVelocity = Vector3.zero;
                if (_previousBoneRotations.TryGetValue(key,
                    out Quaternion previous))
                {
                    Quaternion delta = current * Quaternion.Inverse(previous);
                    delta.ToAngleAxis(out float angle, out Vector3 axis);
                    if (angle > 180f) angle -= 360f;
                    if (IsFinite(angle) && IsFinite(axis) &&
                        axis.sqrMagnitude > 0.0001f)
                    {
                        Vector3 measured = axis.normalized * angle * inverseDelta;
                        if (Mathf.Abs(angle) > 18f) measured = Vector3.zero;
                        if (_boneMotionByName.TryGetValue(key,
                            out PoseMotion prior))
                            angularVelocity = Vector3.Lerp(
                                prior.LocalAngularVelocity, measured, 0.35f);
                        else
                            angularVelocity = measured;
                    }
                }
                angularVelocity = Vector3.ClampMagnitude(angularVelocity, 240f);
                _previousBoneRotations[key] = current;
                _boneMotionByName[key] = new PoseMotion(current, angularVelocity);
            }
            CaptureRootMotion(deltaTime);
        }

        private void InitializePoseCapture()
        {
            _initialized = true;
            _player = GetComponent<Player>();
            _spawners = GetComponentsInChildren<CharacterJointSpawner>(true);

            foreach (RigidbodySpawner body in
                GetComponentsInChildren<RigidbodySpawner>(true))
            {
                if (body == null ||
                    !body.name.ToLowerInvariant().Contains("pelvis")) continue;

                _pelvis = body.transform;
                break;
            }
        }

        private void CaptureRootMotion(float deltaTime)
        {
            if (_pelvis == null) return;
            Vector3 position = _pelvis.position;
            Quaternion rotation = _pelvis.rotation;
            Vector3 horizontalVelocity = Vector3.zero;
            float yawVelocity = 0f;
            if (_hasRootMotion)
            {
                horizontalVelocity = _player != null
                    ? _player.Velocity
                    : (position - _previousPelvisPosition) / deltaTime;
                horizontalVelocity.y = 0f;
                horizontalVelocity = Vector3.ClampMagnitude(horizontalVelocity, 8f);
                Quaternion heading = _player != null
                    ? _player.Transform.rotation : rotation;
                Quaternion previousHeading = _hasRootMotion && _player == null
                    ? _previousPelvisRotation : _rootMotion.Rotation;
                Vector3 oldForward = Vector3.ProjectOnPlane(
                    previousHeading * Vector3.forward, Vector3.up);
                Vector3 newForward = Vector3.ProjectOnPlane(
                    heading * Vector3.forward, Vector3.up);
                if (oldForward.sqrMagnitude > 0.001f &&
                    newForward.sqrMagnitude > 0.001f)
                    yawVelocity = Vector3.SignedAngle(oldForward, newForward,
                        Vector3.up) / deltaTime;
                horizontalVelocity = Vector3.Lerp(
                    _rootMotion.HorizontalVelocity, horizontalVelocity, 0.35f);
                yawVelocity = Mathf.Lerp(_rootMotion.YawVelocity,
                    Mathf.Clamp(yawVelocity, -360f, 360f), 0.35f);
            }
            Quaternion rootRotation = _player != null
                ? _player.Transform.rotation : rotation;
            _rootMotion = new RootMotion(position, rootRotation,
                horizontalVelocity, yawVelocity,
                _player != null ? _player.PoseLevel : 1f);
            _previousPelvisPosition = position;
            _previousPelvisRotation = rotation;
            _hasRootMotion = true;
        }

        internal void FreezePoseCapture() { _frozen = true; }

        internal bool TryGetBoneMotion(string boneName, out PoseMotion motion)
        {
            return _boneMotionByName.TryGetValue(boneName, out motion);
        }

        internal bool TryGetRootMotion(out RootMotion motion)
        {
            motion = _rootMotion;
            return _hasRootMotion;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }

    internal sealed class RagdollSkeleton : MonoBehaviour
    {
        private readonly struct JointProfile
        {
            internal readonly float TwistScale, SwingScale, AngularDrag, Damping;

            internal JointProfile(float twist, float swing, float drag, float damping)
            {
                TwistScale = twist;
                SwingScale = swing;
                AngularDrag = drag;
                Damping = damping;
            }
        }

        private enum BoneRegion
        {
            Leg,
            Arm,
            Spine,
            Head
        }

        private sealed class Bone
        {
            internal string Name;
            internal BoneRegion Region;
            internal Rigidbody Body;
            internal Rigidbody Parent;
            internal CharacterJoint SourceJoint;
            internal ConfigurableJoint Joint;
            internal Quaternion StartLocalRotation;
            internal Quaternion JointSpace;
            internal Transform AnimationTarget;
            internal Transform AnimationParentTarget;
            internal Quaternion AnimationStartLocal;
            internal JointProfile Profile;
            internal float BaseAngularDrag;
            internal float DriveScale;
            internal float LastAngle;
            internal float LastTargetMotion;
            internal Quaternion PreviousAnimatedLocal;
            internal bool HasPreviousAnimatedLocal;
            internal float LastSpring;
            internal float LastMaxForce;
            internal SoftJointLimit CarryLowX, CarryHighX, CarryY, CarryZ;
            internal SoftJointLimit AuthoredLowX, AuthoredHighX, AuthoredY, AuthoredZ;
            internal SoftJointLimit PassiveLowX, PassiveHighX, PassiveY, PassiveZ;
        }

        private sealed class BodyFollower
        {
            internal Rigidbody Body;
            internal Transform Target;
            internal Vector3 LocalOffset;
            internal Vector3 LastTargetPosition;
            internal Vector3 TargetVelocity;
            internal float SampleAge;
        }

        private readonly List<Bone> _bones = new List<Bone>(20);
        private readonly List<BodyFollower> _followers =
            new List<BodyFollower>(20);
        private CorpseRagdoll _ragdoll;
        private FutureAnimationDriver _futureAnimation;
        private Settings.DeathProfile _profile;
        private readonly List<Rigidbody> _bodies = new List<Rigidbody>(20);
        private readonly Dictionary<Rigidbody, Vector3> _deathOffsets =
            new Dictionary<Rigidbody, Vector3>(20);
        private float _nextDebugLog;
        private int _fixedUpdates;
        private float _started;
        private float _physicsElapsed;
        private int _firstPhysicsFrame = -1;
        private Vector3 _remainingInheritedMomentum;
        private Vector3 _initialInheritedMomentum;
        private Rigidbody _pelvisBody;
        private Vector3 _fatalPushVelocity;
        private Vector3 _fatalPushInitialVelocity;
        private float _fatalPushAge;
        private Vector3 _lastPelvisDebugPosition;
        private float _lastPelvisDebugTime;
        private Vector3 _deathPosition;
        private bool _hasDeathPosition;
        private bool _headShot;
        private bool _fallApplied;
        private bool _blastKill;
        private float _shotEnergyFactor = 1f;
        private float _shotPenetrationFactor = 1f;
        private int _lastBlastId;
        private readonly List<Impacts.Blast> _blastBuffer =
            new List<Impacts.Blast>(4);

        private float _hitSeverity = 1f;
        private readonly HashSet<Rigidbody> _limpBodies =
            new HashSet<Rigidbody>();

        // Scales EFT's own corpse impulse; capped so the heavy hitters' extra comes
        // from the fall push and the leg kick, not from one body part flying off.
        internal float ShotEnergyFactor => Mathf.Min(2f, _shotEnergyFactor);
        private readonly Dictionary<Rigidbody, Vector2Int> _solverDefaults =
            new Dictionary<Rigidbody, Vector2Int>(20);
        private float _activeSeconds;
        private bool _released;
        private int _activeSteps;
        private double _activeMilliseconds;
        private double _slowestStepMilliseconds;
        internal bool AllowFreeze
        {
            get
            {
                return Time.time - _started >= Mathf.Max(2f,
                    Settings.FreezeDelay.Value);
            }
        }

        internal bool AllowSleep =>
            Settings.FreezeWhenSettled.Value ? _released : AllowFreeze;

        internal bool ShouldFreeze(bool sleeping) =>
            Settings.FreezeWhenSettled.Value
                ? _released && (sleeping || AllowFreeze)
                : AllowFreeze;

        internal void InitializeRagdoll(CorpseRagdoll ragdoll,
            FutureAnimationDriver futureAnimation = null)
        {
            _ragdoll = ragdoll;
            _futureAnimation = futureAnimation != null && futureAnimation.Running
                ? futureAnimation : null;
            _profile = _futureAnimation != null
                ? _futureAnimation.Profile : Settings.StandingDeath;
            _futureAnimation?.UsePhysicsClock();
            _started = Time.time;
            _activeSeconds = (_futureAnimation != null
                ? Mathf.Max(_profile.WorldFollowDecay.Value,
                    Mathf.Max(_profile.BoneReplayDecay.Value,
                        _profile.MomentumDecay.Value))
                : 0f) + 0.25f;

            foreach (CharacterJointSpawner spawner in ragdoll._jointSpawners)
            {
                Bone bone = CreateBone(spawner);
                if (bone != null) _bones.Add(bone);
            }
            _remainingInheritedMomentum = _futureAnimation != null
                ? _futureAnimation.TravelVelocity * _profile.MomentumScale.Value
                : Vector3.zero;
            _remainingInheritedMomentum.y = 0f;
            _initialInheritedMomentum = _remainingInheritedMomentum;
            foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
            {
                Rigidbody body = spawner.Rigidbody;
                if (body == null) continue;
                InitializeBody(body);
            }
            CaptureDeathPosition();
            _bones.Sort((left, right) => GetBoneDepth(left).CompareTo(
                GetBoneDepth(right)));
            InitializeBodyFollowers();
            ragdoll.WakeUp();
            ReadKillingShot();
            ApplyBlasts(true);
            if (_pelvisBody != null)
            {
                _lastPelvisDebugPosition = _pelvisBody.position;
                _lastPelvisDebugTime = Time.time;
            }
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] Initialized corpse={0} bones={1} spawners={2} future={3} physicsDone={4} mode=DistributedPhysicalAnimation",
                    name, _bones.Count, ragdoll._jointSpawners.Length,
                    _futureAnimation != null,
                    ragdoll._isPhysicsDone));
            LogJointState("initial");
            foreach (Bone bone in _bones)
            {
                Object.Destroy(bone.SourceJoint);
                bone.SourceJoint = null;
            }
        }

        private Bone CreateBone(CharacterJointSpawner spawner)
        {
            CharacterJoint source = spawner.Create() as CharacterJoint;
            if (source == null || source.connectedBody == null) return null;

            Rigidbody body = source.GetComponent<Rigidbody>();
            if (body == null) return null;

            JointProfile profile = GetJointProfile(body.name);
            ConfigurableJoint joint = ConvertJoint(source, profile, 0f, 0f);
            Bone bone = new Bone
            {
                Name = body.name,
                Region = GetBoneRegion(body.name),
                Body = body,
                Parent = joint.connectedBody,
                SourceJoint = source,
                Joint = joint,
                Profile = profile,
                BaseAngularDrag = body.angularDrag,
                StartLocalRotation = Quaternion.Inverse(
                    joint.connectedBody.rotation) * body.rotation,
                JointSpace = BuildJointSpace(joint.axis, joint.secondaryAxis),
                CarryLowX = CreateNonBouncingLimit(source.lowTwistLimit),
                CarryHighX = CreateNonBouncingLimit(source.highTwistLimit),
                CarryY = CreateNonBouncingLimit(source.swing1Limit),
                CarryZ = CreateNonBouncingLimit(source.swing2Limit),
                DriveScale = Mathf.Clamp(profile.Damping / 14f, 0.65f, 1.4f)
            };

            ConfigureBody(body);
            ConfigureAnimationTargets(bone);
            CopyAuthoredLimits(bone);
            ConfigureJointLimits(bone);
            UpdatePassiveProperties(bone, 0f, 0f);
            return bone;
        }

        private void ConfigureAnimationTargets(Bone bone)
        {
            if (_futureAnimation == null ||
                !_futureAnimation.TryGetAnimationTarget(bone.Name,
                    out Transform animationTarget)) return;

            bone.AnimationTarget = animationTarget;
            _futureAnimation.TryGetAnimationTarget(bone.Parent.name,
                out bone.AnimationParentTarget);
            if (bone.AnimationParentTarget == null) return;

            bone.AnimationStartLocal = Quaternion.Inverse(
                bone.AnimationParentTarget.rotation) *
                bone.AnimationTarget.rotation;
        }

        private static void CopyAuthoredLimits(Bone bone)
        {
            bone.AuthoredLowX = bone.CarryLowX;
            bone.AuthoredHighX = bone.CarryHighX;
            bone.AuthoredY = bone.CarryY;
            bone.AuthoredZ = bone.CarryZ;
        }

        private void InitializeBody(Rigidbody body)
        {
            ConfigureBody(body);
            body.mass *= Settings.RagdollMassScale.Value;
            body.drag = Mathf.Max(body.drag, 0.08f);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.isKinematic = false;
            body.detectCollisions = true;
            _bodies.Add(body);

            if (_pelvisBody == null &&
                body.name.ToLowerInvariant().Contains("pelvis"))
                _pelvisBody = body;

            body.velocity = new Vector3(_remainingInheritedMomentum.x,
                body.velocity.y, _remainingInheritedMomentum.z);
            body.angularVelocity = Vector3.zero;
        }

        private void OnDestroy()
        {
            foreach (Bone bone in _bones)
                if (bone.Joint != null)
                    Object.Destroy(bone.Joint);
            _bones.Clear();
            _followers.Clear();
            _bodies.Clear();
            _deathOffsets.Clear();
        }

        private static ConfigurableJoint ConvertJoint(CharacterJoint source,
            JointProfile profile, float bend, float forceScale)
        {
            ConfigurableJoint joint = source.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = source.connectedBody;
            joint.anchor = source.anchor;
            joint.axis = source.axis;
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedAnchor = source.connectedAnchor;
            joint.secondaryAxis = source.swingAxis;
            joint.enableCollision = source.enableCollision;
            joint.enablePreprocessing = false;
            joint.breakForce = source.breakForce;
            joint.breakTorque = source.breakTorque;
            joint.massScale = source.massScale;
            joint.connectedMassScale = source.connectedMassScale;
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = 0.01f;
            joint.projectionAngle = 2f;

            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Limited;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Limited;

            float extendedLimit = 1f / Mathf.Sqrt(Mathf.Max(1f, forceScale));
            float twistScale = Mathf.Lerp(1f,
                profile.TwistScale * extendedLimit, bend);
            float swingScale = Mathf.Lerp(1f,
                profile.SwingScale * extendedLimit, bend);

            joint.lowAngularXLimit = CreateScaledLimit(
                source.lowTwistLimit, twistScale, true);
            joint.highAngularXLimit = CreateScaledLimit(
                source.highTwistLimit, twistScale, false);
            joint.angularYLimit = CreateScaledLimit(
                source.swing1Limit, swingScale, false);
            joint.angularZLimit = CreateScaledLimit(
                source.swing2Limit, swingScale, false);
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.configuredInWorldSpace = false;
            joint.swapBodies = false;
            return joint;
        }

        private static Quaternion BuildJointSpace(Vector3 axis,
            Vector3 secondaryAxis)
        {
            Vector3 right = axis.sqrMagnitude > 0.0001f
                ? axis.normalized : Vector3.right;
            Vector3 forward = Vector3.Cross(right, secondaryAxis);
            if (forward.sqrMagnitude <= 0.0001f)
                forward = Vector3.Cross(right, Vector3.up);
            if (forward.sqrMagnitude <= 0.0001f)
                forward = Vector3.Cross(right, Vector3.forward);
            forward.Normalize();
            Vector3 up = Vector3.Cross(forward, right).normalized;
            return Quaternion.LookRotation(forward, up);
        }

        private void ConfigureBody(Rigidbody body)
        {
            if (!_solverDefaults.ContainsKey(body))
                _solverDefaults[body] = new Vector2Int(body.solverIterations,
                    body.solverVelocityIterations);
            body.solverIterations = Mathf.Max(body.solverIterations, 12);
            body.solverVelocityIterations = Mathf.Max(body.solverVelocityIterations, 6);
            body.maxAngularVelocity = Mathf.Min(body.maxAngularVelocity, 12f);
        }

        private void FixedUpdate()
        {
            _fixedUpdates++;
            if (_ragdoll == null || _ragdoll._isPhysicsDone)
            {
                if (Settings.DebugLogging.Value)
                    Plugin.Log.LogWarning(string.Format(
                        "[RagdollDebug] Controller stopping corpse={0} ragdollNull={1} physicsDone={2} fixedUpdates={3}",
                        name, _ragdoll == null,
                        _ragdoll != null && _ragdoll._isPhysicsDone, _fixedUpdates));
                Destroy(this);
                return;
            }

            if (_firstPhysicsFrame < 0)
                _firstPhysicsFrame = Time.frameCount;
            if (Time.frameCount == _firstPhysicsFrame) return;

            RecoverGlitchedRagdoll();
            // A blast in the corpse's first half-second is what killed it: blast
            // damage lands inside the explosion call, before the blast is recorded.
            ApplyBlasts(Time.time - _started < 0.5f);
            if (_released) return;
            long started = Perf.Enabled ? Perf.Start() : 0L;

            _futureAnimation?.StepPhysics(Time.fixedDeltaTime);
            _physicsElapsed += Time.fixedDeltaTime;
            float elapsed = _physicsElapsed;
            DecayInheritedMomentum(elapsed);
            float worldFollowStrength = GetWorldFollowStrength(elapsed);
            bool futureActive = _futureAnimation != null &&
                _futureAnimation.Running;
            UpdateFatalPush();
            // A blast overrides the walk: carrying the gait's momentum would cancel it.
            DriveBodiesTowardAnimation(
                futureActive && !_blastKill ? worldFollowStrength : 0f);
            foreach (Bone bone in _bones)
            {
                UpdateBoneDrive(bone, futureActive,
                    _limpBodies.Contains(bone.Body) ? 0f
                        : GetBoneReplayStrength(elapsed, bone.Region));
            }

            if (Settings.DebugLogging.Value && Time.unscaledTime >= _nextDebugLog)
            {
                _nextDebugLog = Time.unscaledTime + 1f;
                LogJointState("live");
            }

            if (Perf.Enabled)
            {
                double step = Perf.Milliseconds(started);
                _activeSteps++;
                _activeMilliseconds += step;
                _slowestStepMilliseconds = System.Math.Max(
                    _slowestStepMilliseconds, step);
            }
            if (!futureActive && elapsed >= _activeSeconds)
                ReleaseDrives();
        }

        private void ReleaseDrives()
        {
            _released = true;
            foreach (Bone bone in _bones)
                if (bone.Joint != null)
                    bone.Joint.slerpDrive = new JointDrive();
            foreach (KeyValuePair<Rigidbody, Vector2Int> body in
                _solverDefaults)
            {
                if (body.Key == null) continue;
                body.Key.solverIterations = body.Value.x;
                body.Key.solverVelocityIterations = body.Value.y;
            }
            _followers.Clear();
            if (Perf.Enabled)
                Perf.Log(string.Format(
                    "corpse {0}: animation phase done after {1:0.00} s, {2} physics steps, {3:0.000} ms average, {4:0.000} ms slowest",
                    name, _physicsElapsed, _activeSteps,
                    _activeSteps > 0 ? _activeMilliseconds / _activeSteps : 0d,
                    _slowestStepMilliseconds));
        }

        private void UpdateBoneDrive(Bone bone, bool futureActive,
            float replayStrength)
        {
            if (bone.Body == null || bone.Parent == null || bone.Joint == null)
                return;

            bool hasFutureTarget = futureActive &&
                bone.AnimationTarget != null &&
                bone.AnimationParentTarget != null;
            Quaternion animatedLocal = hasFutureTarget
                ? Quaternion.Inverse(bone.AnimationParentTarget.rotation) *
                    bone.AnimationTarget.rotation
                : bone.StartLocalRotation;

            if (bone.HasPreviousAnimatedLocal)
                bone.LastTargetMotion = Quaternion.Angle(
                    bone.PreviousAnimatedLocal, animatedLocal);
            bone.PreviousAnimatedLocal = animatedLocal;
            bone.HasPreviousAnimatedLocal = true;

            Quaternion currentLocal = Quaternion.Inverse(bone.Parent.rotation) *
                bone.Body.rotation;
            Quaternion error = animatedLocal * Quaternion.Inverse(currentLocal);
            error.ToAngleAxis(out float angle, out _);
            if (angle > 180f) angle -= 360f;
            bone.LastAngle = Mathf.Abs(angle);

            ApplyAnimationDrive(bone, animatedLocal,
                hasFutureTarget ? replayStrength : 0f);
        }

        private void ApplyAnimationDrive(Bone bone,
            Quaternion animationLocal, float strength)
        {
            Quaternion animationDelta = animationLocal *
                Quaternion.Inverse(bone.AnimationStartLocal);
            Quaternion desiredLocal = animationDelta *
                bone.StartLocalRotation;

            float scale = bone.DriveScale;
            float normalized = Mathf.Clamp01(strength / 100f);
            float spring = strength * 8f * scale;
            JointDrive drive = bone.Joint.slerpDrive;
            drive.positionSpring = spring;
            drive.positionDamper = 1.6f * Mathf.Sqrt(spring) * scale;
            drive.maximumForce = Mathf.Min(
                _profile.BoneReplayMaximumTorque.Value * normalized,
                strength * 35f * scale * Mathf.Sqrt(normalized));
            bone.Joint.slerpDrive = drive;
            bone.Joint.targetAngularVelocity = Vector3.zero;
            bone.Joint.targetRotation = Quaternion.Inverse(bone.JointSpace) *
                (Quaternion.Inverse(desiredLocal) *
                bone.StartLocalRotation) * bone.JointSpace;
            bone.LastSpring = drive.positionSpring;
            bone.LastMaxForce = drive.maximumForce;
        }

        private void ConfigureJointLimits(Bone bone)
        {
            float range = _profile.JointLimitRange.Value;
            bone.Joint.lowAngularXLimit = CreateScaledLimit(
                bone.AuthoredLowX, range, true);
            bone.Joint.highAngularXLimit = CreateScaledLimit(
                bone.AuthoredHighX, range, false);
            bone.Joint.angularYLimit = CreateScaledLimit(
                bone.AuthoredY, range, false);
            bone.Joint.angularZLimit = CreateScaledLimit(
                bone.AuthoredZ, range, false);

            float spring = _profile.JointLimitStiffness.Value * 10f;
            SoftJointLimitSpring limitSpring =
                bone.Joint.angularXLimitSpring;
            limitSpring.spring = spring;
            limitSpring.damper = 2f * Mathf.Sqrt(spring);
            bone.Joint.angularXLimitSpring = limitSpring;
            bone.Joint.angularYZLimitSpring = limitSpring;
        }

        private float GetWorldFollowStrength(float elapsed)
        {
            return EvaluateDecayCurve(_profile.WorldFollowStrength.Value, 0f,
                _profile.WorldFollowDecay.Value, elapsed);
        }

        private float GetBoneReplayStrength(float elapsed, BoneRegion region)
        {
            float duration = Mathf.Min(_profile.BoneReplayDecay.Value,
                GetToneLoss(region).Value);
            if ((_headShot && Settings.HeadshotLightsOut.Value) || _blastKill)
                duration *= 0.25f;
            else
                duration /= _hitSeverity; // a heavy round cuts tone faster
            return EvaluateDecayCurve(_profile.BoneReplayStrength.Value, 0f,
                duration, elapsed);
        }

        private static ConfigEntry<float> GetToneLoss(BoneRegion region)
        {
            switch (region)
            {
                case BoneRegion.Leg: return Settings.LegToneLoss;
                case BoneRegion.Arm: return Settings.ArmToneLoss;
                case BoneRegion.Head: return Settings.HeadToneLoss;
                default: return Settings.SpineToneLoss;
            }
        }

        private static BoneRegion GetBoneRegion(string boneName)
        {
            string name = (boneName ?? string.Empty).ToLowerInvariant();
            if (ContainsAnyFragment(name, "calf", "shin", "lowerleg", "thigh",
                "upleg", "foot", "ankle"))
                return BoneRegion.Leg;
            if (ContainsAnyFragment(name, "forearm", "lowerarm", "upperarm",
                "shoulder", "hand", "wrist"))
                return BoneRegion.Arm;
            if (ContainsAnyFragment(name, "head", "neck"))
                return BoneRegion.Head;
            return BoneRegion.Spine;
        }

        private static float EvaluateDecayCurve(float start, float end,
            float duration,
            float elapsed)
        {
            float progress = Mathf.Clamp01(elapsed /
                Mathf.Max(0.05f, duration));
            return Mathf.Lerp(start, end, ApplySmoothStep(progress));
        }

        private void DecayInheritedMomentum(float elapsed)
        {
            float duration = Mathf.Max(0.05f,
                _profile.MomentumDecay.Value);
            float progress = Mathf.Clamp01(elapsed / duration);
            float retained = 1f - ApplySmoothStep(progress);
            _remainingInheritedMomentum = _initialInheritedMomentum * retained;
        }

        private void InitializeBodyFollowers()
        {
            if (_futureAnimation == null) return;
            foreach (Rigidbody body in _bodies)
            {
                if (body == null ||
                    !_futureAnimation.TryGetAnimationTarget(body.name,
                        out Transform target) || target == null) continue;
                Vector3 offset = body.worldCenterOfMass - target.position;
                _followers.Add(new BodyFollower
                {
                    Body = body,
                    Target = target,
                    LocalOffset = target.InverseTransformVector(offset),
                    LastTargetPosition = body.worldCenterOfMass,
                    TargetVelocity = _initialInheritedMomentum,
                    SampleAge = Time.fixedDeltaTime
                });
            }
        }

        private void CaptureDeathPosition()
        {
            Rigidbody anchor = _pelvisBody;
            if (anchor == null && _bodies.Count > 0) anchor = _bodies[0];
            if (anchor == null || !IsFinite(anchor.position)) return;

            _deathPosition = anchor.position;
            _hasDeathPosition = true;
            foreach (Rigidbody body in _bodies)
                if (body != null && IsFinite(body.position))
                    _deathOffsets[body] = body.position - _deathPosition;
        }

        private void RecoverGlitchedRagdoll()
        {
            if (!Settings.TeleportRagdollOnGlitch.Value || !_hasDeathPosition)
                return;

            const float maximumDistanceSquared = 35f * 35f;
            bool glitched = false;
            foreach (Rigidbody body in _bodies)
            {
                if (body == null) continue;
                Vector3 position = body.position;
                if (!IsFinite(position) ||
                    (position - _deathPosition).sqrMagnitude > maximumDistanceSquared)
                {
                    glitched = true;
                    break;
                }
            }
            if (!glitched) return;

            foreach (Rigidbody body in _bodies)
            {
                if (body == null || !_deathOffsets.TryGetValue(body,
                    out Vector3 offset)) continue;
                body.position = _deathPosition + offset;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.WakeUp();
            }
            _remainingInheritedMomentum = Vector3.zero;
            _initialInheritedMomentum = Vector3.zero;
            _fatalPushVelocity = Vector3.zero;
            _fatalPushInitialVelocity = Vector3.zero;
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogWarning("[RagdollDebug] Recovered glitched ragdoll " +
                    name + " at " + _deathPosition);
        }

        private void DriveBodiesTowardAnimation(float strength)
        {
            float driveScale = Mathf.Max(0f, strength / 100f);
            if (driveScale <= 0f) return;
            float spring = 80f * driveScale;
            float damper = 2f * Mathf.Sqrt(spring);
            float maximumAcceleration = 60f * driveScale;
            foreach (BodyFollower follower in _followers)
            {
                if (follower.Body == null || follower.Target == null ||
                    _limpBodies.Contains(follower.Body)) continue;
                Vector3 targetPosition = follower.Target.position +
                    follower.Target.TransformVector(follower.LocalOffset);
                follower.SampleAge += Time.fixedDeltaTime;
                Vector3 step = targetPosition - follower.LastTargetPosition;
                if (step.sqrMagnitude > 0.0000001f)
                {
                    Vector3 measured = Vector3.ClampMagnitude(step /
                        Mathf.Max(0.001f, follower.SampleAge), 10f);
                    follower.TargetVelocity = Vector3.Lerp(
                        follower.TargetVelocity, measured, 0.4f);
                    follower.LastTargetPosition = targetPosition;
                    follower.SampleAge = 0f;
                }
                Vector3 positionError =
                    targetPosition - follower.Body.worldCenterOfMass;
                Vector3 velocityError = follower.TargetVelocity +
                    _fatalPushVelocity - follower.Body.velocity;
                positionError.y = 0f;
                velocityError.y = 0f;
                Vector3 acceleration = positionError * spring +
                    velocityError * damper;
                follower.Body.AddForce(Vector3.ClampMagnitude(acceleration,
                    maximumAcceleration), ForceMode.Acceleration);
            }
        }

        private void UpdateFatalPush()
        {
            if (_fatalPushVelocity.sqrMagnitude < 0.0001f) return;
            _fatalPushAge += Time.fixedDeltaTime;
            float progress = Mathf.Clamp01(_fatalPushAge /
                Mathf.Max(0.05f, Settings.FatalPushDecay.Value));
            _fatalPushVelocity = _fatalPushInitialVelocity *
                (1f - ApplySmoothStep(progress));
            if (progress >= 1f) _fatalPushVelocity = Vector3.zero;
        }

        internal void CaptureFatalImpulse(Rigidbody hitBody,
            Vector3 direction, Vector3 point, float thrust)
        {
            if (hitBody != null &&
                GetBoneRegion(hitBody.name) == BoneRegion.Head)
                _headShot = true;
            if (direction.sqrMagnitude < 0.0001f || thrust <= 0f) return;
            Vector3 push = direction.normalized * thrust *
                Settings.ImpulseScale.Value * 0.2f;
            _fatalPushVelocity = Vector3.ClampMagnitude(
                _fatalPushVelocity + push, 1.25f);
            _fatalPushInitialVelocity = _fatalPushVelocity;
            _fatalPushAge = 0f;
            MarkStruckLimb(hitBody);
            ApplyFallWithShot(hitBody, direction);
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] Cached fatal push body={0} direction={1} thrust={2:0.00} velocity={3} headShot={4}",
                    hitBody != null ? hitBody.name : "NULL", direction,
                    thrust, _fatalPushVelocity, _headShot));
        }

        private void ApplyFallWithShot(Rigidbody hitBody, Vector3 direction)
        {
            Vector3 push = new Vector3(direction.x, 0f, direction.z);
            float speed = Settings.FallWithShot.Value;
            if (_fallApplied || speed <= 0f || push.sqrMagnitude < 0.0001f)
                return;
            _fallApplied = true;
            BoneRegion hitRegion = hitBody != null
                ? GetBoneRegion(hitBody.name) : BoneRegion.Spine;
            float roundPush = _shotEnergyFactor * _shotPenetrationFactor;
            Vector3 along = push.normalized;
            push = along * speed * roundPush *
                (hitRegion == BoneRegion.Head ? 1.5f : 1f);
            bool legHit = hitRegion == BoneRegion.Leg;
            foreach (Rigidbody body in _bodies)
            {
                if (body == null || (legHit && _limpBodies.Contains(body)))
                    continue;
                float share = GetFallShare(body.name);
                if (legHit) share *= 0.3f;
                if (share > 0f)
                    body.AddForce(push * share, ForceMode.VelocityChange);
            }
            if (legHit) KickLeg(hitBody, along, push.magnitude, roundPush);
        }

        // The struck leg swings back about its joint: each part moves in proportion to
        // its distance from the pivot, so the foot travels about twice as fast as the
        // knee and lifts a little off the ground. Heavy rounds kick harder.
        private void KickLeg(Rigidbody hitBody, Vector3 along, float speed,
            float roundPush)
        {
            Bone hitBone = _bones.Find(bone => bone.Body == hitBody);
            if (hitBone == null || hitBone.Parent == null) return;
            Vector3 pivot = hitBone.Parent.worldCenterOfMass;
            float baseReach = Mathf.Max(0.05f,
                Vector3.Distance(hitBody.worldCenterOfMass, pivot));
            float kick = speed * 1.5f * Mathf.Sqrt(roundPush);
            foreach (Rigidbody body in _limpBodies)
            {
                if (body == null) continue;
                float reach = Vector3.Distance(body.worldCenterOfMass, pivot) /
                    baseReach;
                Vector3 velocity = along * kick * reach;
                if (body != hitBody) velocity += Vector3.up * 0.3f * kick;
                body.AddForce(velocity, ForceMode.VelocityChange);
            }
        }

        // A struck leg, arm or head goes limp at once, from the hit down the limb.
        private void MarkStruckLimb(Rigidbody hitBody)
        {
            if (hitBody == null || _limpBodies.Count > 0) return;
            BoneRegion region = GetBoneRegion(hitBody.name);
            if (region == BoneRegion.Spine) return;
            _limpBodies.Add(hitBody);
            foreach (Bone bone in _bones) // sorted parent-first
                if (bone.Parent != null && _limpBodies.Contains(bone.Parent))
                    _limpBodies.Add(bone.Body);
        }

        private void ReadKillingShot()
        {
            if (!Settings.ScaleByBullet.Value ||
                !Impacts.TryGetShot(gameObject, out Impacts.ShotRecord shot))
                return;
            _shotEnergyFactor = Impacts.EnergyFactor(shot);
            _shotPenetrationFactor = Impacts.PenetrationFactor(shot);
            _hitSeverity = Mathf.Max(1f,
                _shotEnergyFactor * _shotPenetrationFactor);
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] Killing shot {0}: {1:0} J, penetration {2:0}, push x{3:0.00}",
                    name, shot.Energy, shot.Penetration,
                    _shotEnergyFactor * _shotPenetrationFactor));
        }

        private void ApplyBlasts(bool killedByIt)
        {
            _lastBlastId = Impacts.CollectBlasts(_lastBlastId, _blastBuffer);
            float push = Settings.ExplosionPush.Value;
            if (push <= 0f || _blastBuffer.Count == 0) return;
            Vector3 centre = _pelvisBody != null
                ? _pelvisBody.position : transform.position;
            foreach (Impacts.Blast blast in _blastBuffer)
            {
                if (Vector3.Distance(centre, blast.Position) > blast.Radius)
                    continue;
                if (killedByIt)
                {
                    _blastKill = true;
                    _fallApplied = true;
                }
                // Away from a point just below the blast, falling off with the square
                // of the distance. Each part gets its own share (light, exposed limbs
                // more than the torso, +-25%, a few degrees off line, a little spin) so
                // the body folds and flails instead of sliding as one block. The body
                // keeps its own momentum; the blast only adds to it.
                float strength = push *
                    Mathf.Clamp(blast.Strength / 100f, 0.3f, 2f);
                Vector3 source = blast.Position - Vector3.up * 0.3f;
                foreach (Rigidbody body in _bodies)
                {
                    if (body == null) continue;
                    Vector3 part = body.worldCenterOfMass;
                    float reach = Vector3.Distance(part, blast.Position);
                    if (reach >= blast.Radius) continue;
                    float falloff = 1f - reach / blast.Radius;
                    Vector3 away = part - source;
                    away = away.sqrMagnitude > 0.0001f ? away.normalized
                        : Vector3.up;
                    away = Quaternion.Euler(Random.Range(-10f, 10f),
                        Random.Range(-10f, 10f), 0f) * away;
                    float speed = strength * falloff * falloff *
                        GetBlastExposure(body.name) * Random.Range(0.75f, 1.25f);
                    body.AddForce(away * speed, ForceMode.VelocityChange);
                    body.AddTorque(Random.onUnitSphere * speed * 0.8f,
                        ForceMode.VelocityChange);
                }
                if (Settings.DebugLogging.Value)
                    Plugin.Log.LogInfo(string.Format(
                        "[RagdollDebug] Blast {0} pushed {1}: {2:0.0} m away, strength {3:0}, killed by it {4}",
                        blast.Id, name,
                        Vector3.Distance(centre, blast.Position),
                        blast.Strength, killedByIt));
            }
        }

        private static float GetBlastExposure(string boneName)
        {
            switch (GetBoneRegion(boneName))
            {
                case BoneRegion.Head: return 1.1f;
                case BoneRegion.Arm: return 1.4f;
                case BoneRegion.Leg: return 1.25f;
                default: return 0.8f;
            }
        }

        private static float GetFallShare(string boneName)
        {
            if ((boneName ?? string.Empty).ToLowerInvariant()
                .Contains("pelvis"))
                return 0.2f;
            switch (GetBoneRegion(boneName))
            {
                case BoneRegion.Head: return 1f;
                case BoneRegion.Spine: return 0.8f;
                case BoneRegion.Arm: return 0.6f;
                default: return 0f;
            }
        }

        private int GetBoneDepth(Bone bone)
        {
            int depth = 0;
            Rigidbody parent = bone.Parent;
            for (int guard = 0; parent != null && guard < _bones.Count; guard++)
            {
                Bone parentBone = _bones.Find(candidate =>
                    candidate.Body == parent);
                if (parentBone == null) break;
                depth++;
                parent = parentBone.Parent;
            }
            return depth;
        }

        private void UpdatePassiveProperties(Bone bone, float bend,
            float forceScale)
        {
            float extendedLimit = 1f / Mathf.Sqrt(Mathf.Max(1f, forceScale));
            float twistScale = Mathf.Lerp(1f,
                bone.Profile.TwistScale * extendedLimit, bend);
            float swingScale = Mathf.Lerp(1f,
                bone.Profile.SwingScale * extendedLimit, bend);

            bone.PassiveLowX = CreateScaledLimit(
                bone.AuthoredLowX, twistScale, true);
            bone.PassiveHighX = CreateScaledLimit(
                bone.AuthoredHighX, twistScale, false);
            bone.PassiveY = CreateScaledLimit(
                bone.AuthoredY, swingScale, false);
            bone.PassiveZ = CreateScaledLimit(
                bone.AuthoredZ, swingScale, false);
            bone.Body.angularDrag = Mathf.Lerp(bone.BaseAngularDrag,
                Mathf.Max(bone.BaseAngularDrag, bone.Profile.AngularDrag), bend);
        }

        private void LogJointState(string phase)
        {
            if (!Settings.DebugLogging.Value) return;
            Plugin.Log.LogInfo(string.Format(
                "[RagdollDebug] {0} corpse={1} fixed={2} bones={3} physicsDone={4}",
                phase, name, _fixedUpdates, _bones.Count,
                _ragdoll != null && _ragdoll._isPhysicsDone));
            if (_pelvisBody == null)
            {
                Plugin.Log.LogWarning("[RagdollDebug] pelvis=NULL momentum=" +
                    _remainingInheritedMomentum);
            }
            else
            {
                float dt = Mathf.Max(0.001f,
                    Time.time - _lastPelvisDebugTime);
                Vector3 measured = (_pelvisBody.position -
                    _lastPelvisDebugPosition) / dt;
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] pelvis={0} pos={1} velocity={2} measured={3} momentum={4} root={5} kin={6} sleep={7}",
                    _pelvisBody.name, _pelvisBody.position,
                    _pelvisBody.velocity, measured,
                    _remainingInheritedMomentum, transform.position,
                    _pelvisBody.isKinematic, _pelvisBody.IsSleeping()));
                _lastPelvisDebugPosition = _pelvisBody.position;
                _lastPelvisDebugTime = Time.time;
            }
            foreach (Bone bone in _bones)
            {
                if (bone.Body == null || bone.Joint == null)
                {
                    Plugin.Log.LogWarning("[RagdollDebug] missing bone/joint " + bone.Name);
                    continue;
                }
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] joint={0} parent={1} error={2:0.0} targetStep={3:0.00} spring={4:0} maxForce={5:0} limits=({6:0.0},{7:0.0}) swing=({8:0.0},{9:0.0}) vel={10:0.0} parentVel={11:0.0} kin={12}/{13} sleep={14}/{15} active={16}",
                    bone.Name, bone.Parent != null ? bone.Parent.name : "NULL",
                    bone.LastAngle, bone.LastTargetMotion, bone.LastSpring,
                    bone.LastMaxForce,
                    bone.Joint.lowAngularXLimit.limit,
                    bone.Joint.highAngularXLimit.limit,
                    bone.Joint.angularYLimit.limit,
                    bone.Joint.angularZLimit.limit,
                    bone.Body.angularVelocity.magnitude,
                    bone.Parent != null ? bone.Parent.angularVelocity.magnitude : -1f,
                    bone.Body.isKinematic,
                    bone.Parent != null && bone.Parent.isKinematic,
                    bone.Body.IsSleeping(),
                    bone.Parent != null && bone.Parent.IsSleeping(),
                    bone.Joint.gameObject.activeInHierarchy));
            }
        }

        private static JointProfile GetJointProfile(string boneName)
        {
            string name = (boneName ?? string.Empty).ToLowerInvariant();
            if (ContainsAnyFragment(name, "calf", "shin", "lowerleg"))
                return new JointProfile(0.28f, 0.22f, 2.8f, 18f);
            if (ContainsAnyFragment(name, "forearm", "lowerarm"))
                return new JointProfile(0.35f, 0.28f, 2.3f, 15f);
            if (ContainsAnyFragment(name, "thigh", "upleg"))
                return new JointProfile(0.55f, 0.58f, 2.2f, 14f);
            if (ContainsAnyFragment(name, "upperarm", "shoulder"))
                return new JointProfile(0.58f, 0.62f, 1.8f, 12f);
            if (ContainsAnyFragment(name, "hand", "wrist"))
                return new JointProfile(0.38f, 0.35f, 1.7f, 10f);
            if (ContainsAnyFragment(name, "foot", "ankle"))
                return new JointProfile(0.32f, 0.28f, 2.0f, 13f);
            if (ContainsAnyFragment(name, "head", "neck"))
                return new JointProfile(0.42f, 0.45f, 1.6f, 11f);
            if (ContainsAnyFragment(name, "spine", "chest", "rib", "pelvis"))
                return new JointProfile(0.38f, 0.42f, 3.0f, 20f);
            return new JointProfile(0.50f, 0.50f, 2.0f, 12f);
        }

        private static SoftJointLimit CreateNonBouncingLimit(
            SoftJointLimit source)
        {
            source.bounciness = 0f;
            return source;
        }

        private static SoftJointLimit CreateScaledLimit(SoftJointLimit source,
            float scale, bool negative)
        {
            source.limit = negative
                ? Mathf.Min(-0.1f, source.limit * scale)
                : Mathf.Max(0.1f, source.limit * scale);
            source.bounciness = 0f;
            return source;
        }

        private static float ApplySmoothStep(float value)
        {
            return value * value * (3f - 2f * value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static bool ContainsAnyFragment(string value,
            params string[] fragments)
        {
            for (int i = 0; i < fragments.Length; i++)
                if (value.Contains(fragments[i])) return true;
            return false;
        }
    }
}
