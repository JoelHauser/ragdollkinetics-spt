using System.Collections.Generic;
using BepInEx.Configuration;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using UnityEngine;

namespace RagdollKinetics.Patches
{
    internal sealed class RagdollSkeleton : MonoBehaviour
    {
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
            internal int Depth;
            internal Rigidbody Body;
            internal Rigidbody Parent;
            internal CharacterJoint SourceJoint;
            internal ConfigurableJoint Joint;
            internal Quaternion StartLocalRotation;
            internal Quaternion JointSpace;
            internal Transform AnimationTarget;
            internal Transform AnimationParentTarget;
            internal Quaternion AnimationStartLocal;
            // targetRotation = DriveLeft * Inverse(target) * parentTarget * DriveRight
            internal Quaternion DriveLeft;
            internal Quaternion DriveRight;
            // Moment of inertia, about the joint, of everything the joint carries, and
            // the torque its weight puts on the joint at full lever.
            internal float Inertia;
            internal float GravityLoad;
            internal bool DriveOff = true;
            internal float LastAngle;
            internal float LastTargetMotion;
            internal Quaternion PreviousAnimatedLocal;
            internal bool HasPreviousAnimatedLocal;
            internal float LastSpring;
            internal float LastMaxForce;
            internal SoftJointLimit AuthoredLowX, AuthoredHighX, AuthoredY, AuthoredZ;
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

        // Muscles: every joint follows the pose at this natural frequency (rad/s) at
        // strength 100 and this damping ratio, whatever it carries. Limits: the
        // stiffness setting times LimitFrequencyScale is their frequency.
        private const float MuscleFrequency = 20f;
        private const float MuscleDamping = 0.7f;
        private const float LimitFrequencyScale = 2.5f;
        private const float LimitDamping = 0.7f;
        private const float MinimumActiveSeconds = 0.25f;
        private const float GlitchCheckInterval = 0.1f;
        private const float MaximumCarriedSpeed = 3f;

        private static readonly Dictionary<PlayerRigidbodySleepHierarchy, RagdollSkeleton>
            SleepParts = new Dictionary<PlayerRigidbodySleepHierarchy, RagdollSkeleton>(256);
        private static Vector3[] _carryPositions = new Vector3[24];
        private static Vector3[] _carryVelocities = new Vector3[24];
        private static Quaternion[] _carryRotations = new Quaternion[24];

        private readonly List<Bone> _bones = new List<Bone>(20);
        private readonly Dictionary<Rigidbody, Bone> _boneByBody =
            new Dictionary<Rigidbody, Bone>(20);
        private readonly List<BodyFollower> _followers =
            new List<BodyFollower>(20);
        private CorpseRagdoll _ragdoll;
        private FutureAnimationDriver _futureAnimation;
        private Settings.DeathProfile _profile;
        private readonly List<Rigidbody> _bodies = new List<Rigidbody>(20);
        private readonly Dictionary<Rigidbody, BoneRegion> _regions =
            new Dictionary<Rigidbody, BoneRegion>(20);
        private readonly Dictionary<Rigidbody, Vector3> _deathOffsets =
            new Dictionary<Rigidbody, Vector3>(20);
        private float _nextDebugLog;
        private float _nextGlitchCheck;
        private int _fixedUpdates;
        private float _started;
        private float _physicsElapsed;
        private int _firstPhysicsFrame = -1;
        private bool _animationAhead;
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
        private float _roundPush = 1f;
        private int _lastBlastId;
        private readonly List<Impacts.Blast> _blastBuffer =
            new List<Impacts.Blast>(4);

        private float _hitSeverity = 1f;
        private readonly HashSet<Rigidbody> _limpBodies =
            new HashSet<Rigidbody>();

        // Scales EFT's own corpse impulse; capped so the heavy hitters' extra comes
        // from the fall push and the leg kick, not from one body part flying off.
        internal float ShotImpulseFactor => Mathf.Min(2f, _roundPush);
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

        // The game asks every body part of every settling corpse, every frame,
        // whether it may sleep; a lookup instead of a walk up the bone hierarchy.
        internal static RagdollSkeleton ForSleepPart(
            PlayerRigidbodySleepHierarchy part) =>
            part != null && SleepParts.TryGetValue(part,
                out RagdollSkeleton skeleton) ? skeleton : null;

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
            // Only an upper bound: the animation phase normally ends as soon as the
            // last region has gone limp (see FixedUpdate).
            _activeSeconds = (_futureAnimation != null
                ? Mathf.Max(_profile.WorldFollowDecay.Value,
                    Mathf.Max(_profile.BoneReplayDecay.Value,
                        _profile.MomentumDecay.Value))
                : 0f) + 0.25f;

            foreach (CharacterJointSpawner spawner in ragdoll._jointSpawners)
            {
                Bone bone = CreateBone(spawner);
                if (bone == null) continue;
                _bones.Add(bone);
                _boneByBody[bone.Body] = bone;
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
            foreach (Bone bone in _bones) bone.Depth = GetBoneDepth(bone);
            _bones.Sort((left, right) => left.Depth.CompareTo(right.Depth));
            MeasureJointInertia();
            InitializeBodyFollowers();
            CarryAnimatedMotion();
            ragdoll.WakeUp();
            ReadKillingShot();
            ApplyBlasts(true);
            if (_pelvisBody != null)
            {
                _lastPelvisDebugPosition = _pelvisBody.position;
                _lastPelvisDebugTime = Time.time;
            }
            if (ragdoll._rigidbodySleepHierarchy != null)
                foreach (PlayerRigidbodySleepHierarchy part in
                    ragdoll._rigidbodySleepHierarchy)
                    if (part != null) SleepParts[part] = this;
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

            ConfigurableJoint joint = ConvertJoint(source);
            Bone bone = new Bone
            {
                Name = body.name,
                Region = GetBoneRegion(body.name),
                Body = body,
                Parent = joint.connectedBody,
                SourceJoint = source,
                Joint = joint,
                StartLocalRotation = Quaternion.Inverse(
                    joint.connectedBody.rotation) * body.rotation,
                JointSpace = BuildJointSpace(joint.axis, joint.secondaryAxis),
                AuthoredLowX = CreateNonBouncingLimit(source.lowTwistLimit),
                AuthoredHighX = CreateNonBouncingLimit(source.highTwistLimit),
                AuthoredY = CreateNonBouncingLimit(source.swing1Limit),
                AuthoredZ = CreateNonBouncingLimit(source.swing2Limit)
            };

            ConfigureBody(body);
            ConfigureAnimationTargets(bone);
            ConfigureJointLimits(bone);
            return bone;
        }

        private void ConfigureAnimationTargets(Bone bone)
        {
            if (_futureAnimation == null ||
                !_futureAnimation.TryGetAnimationTarget(bone.Name,
                    out Transform animationTarget)) return;

            _futureAnimation.TryGetAnimationTarget(bone.Parent.name,
                out Transform parentTarget);
            if (parentTarget == null) return;

            bone.AnimationTarget = animationTarget;
            bone.AnimationParentTarget = parentTarget;
            // The joint is driven toward its start pose turned by however far the
            // animation has turned the bone since death.
            bone.AnimationStartLocal = Quaternion.Inverse(
                parentTarget.rotation) * animationTarget.rotation;
            bone.DriveLeft = Quaternion.Inverse(bone.JointSpace) *
                Quaternion.Inverse(bone.StartLocalRotation) *
                bone.AnimationStartLocal;
            bone.DriveRight = bone.StartLocalRotation * bone.JointSpace;
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
            _regions[body] = GetBoneRegion(body.name);

            if (_pelvisBody == null &&
                body.name.ToLowerInvariant().Contains("pelvis"))
                _pelvisBody = body;

            body.velocity = new Vector3(_remainingInheritedMomentum.x,
                body.velocity.y, _remainingInheritedMomentum.z);
            body.angularVelocity = Vector3.zero;
        }

        private void OnDestroy()
        {
            if (_ragdoll != null && _ragdoll._rigidbodySleepHierarchy != null)
                foreach (PlayerRigidbodySleepHierarchy part in
                    _ragdoll._rigidbodySleepHierarchy)
                    if (part != null &&
                        SleepParts.TryGetValue(part, out RagdollSkeleton owner) &&
                        ReferenceEquals(owner, this))
                        SleepParts.Remove(part);
            foreach (Bone bone in _bones)
                if (bone.Joint != null)
                    Object.Destroy(bone.Joint);
            _bones.Clear();
            _boneByBody.Clear();
            _followers.Clear();
            _bodies.Clear();
            _regions.Clear();
            _deathOffsets.Clear();
        }

        // Limits are set right after, from the authored ones, by ConfigureJointLimits.
        private static ConfigurableJoint ConvertJoint(CharacterJoint source)
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
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.targetAngularVelocity = Vector3.zero;
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
            body.maxAngularVelocity = Settings.LimbSpeedLimit.Value;
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

            if (Time.fixedTime >= _nextGlitchCheck)
            {
                _nextGlitchCheck = Time.fixedTime + GlitchCheckInterval;
                RecoverGlitchedRagdoll();
            }
            // A blast in the corpse's first half-second is what killed it: blast
            // damage lands inside the explosion call, before the blast is recorded.
            ApplyBlasts(Time.time - _started < 0.5f);
            if (_released) return;
            long started = Perf.Enabled ? Perf.Start() : 0L;

            float step = Time.fixedDeltaTime;
            // CarryAnimatedMotion already stepped the copy once at setup.
            if (_animationAhead) _animationAhead = false;
            else _futureAnimation?.StepPhysics(step);
            _physicsElapsed += step;
            float elapsed = _physicsElapsed;
            bool futureActive = _futureAnimation != null &&
                _futureAnimation.Running;
            // A blast overrides the walk: carrying the gait's momentum would cancel it.
            float follow = futureActive && !_blastKill
                ? GetWorldFollowStrength(elapsed) : 0f;
            UpdateFatalPush(step);
            DriveBodiesTowardAnimation(follow, step);
            bool debug = Settings.DebugLogging.Value;
            bool driving = follow > 0f;
            foreach (Bone bone in _bones)
            {
                if (UpdateBoneDrive(bone, futureActive,
                    _limpBodies.Contains(bone.Body) ? 0f
                        : GetBoneReplayStrength(elapsed, bone.Region), debug))
                    driving = true;
            }

            if (debug && Time.unscaledTime >= _nextDebugLog)
            {
                _nextDebugLog = Time.unscaledTime + 1f;
                LogJointState("live");
            }

            if (Perf.Enabled)
            {
                double stepMilliseconds = Perf.Milliseconds(started);
                _activeSteps++;
                _activeMilliseconds += stepMilliseconds;
                _slowestStepMilliseconds = System.Math.Max(
                    _slowestStepMilliseconds, stepMilliseconds);
            }
            // The animation phase is over once every region has gone limp and the
            // gait no longer carries the body; the animation copy stops with it.
            if ((!driving && elapsed >= MinimumActiveSeconds) ||
                elapsed >= _activeSeconds)
                ReleaseDrives();
        }

        private void ReleaseDrives()
        {
            _released = true;
            foreach (Bone bone in _bones)
                if (bone.Joint != null && !bone.DriveOff)
                {
                    bone.Joint.slerpDrive = new JointDrive();
                    bone.DriveOff = true;
                }
            foreach (KeyValuePair<Rigidbody, Vector2Int> body in
                _solverDefaults)
            {
                if (body.Key == null) continue;
                body.Key.solverIterations = body.Value.x;
                body.Key.solverVelocityIterations = body.Value.y;
            }
            _followers.Clear();
            if (_futureAnimation != null) _futureAnimation.Stop();
            if (Perf.Enabled)
                Perf.Log(string.Format(
                    "corpse {0}: animation phase done after {1:0.00} s, {2} physics steps, {3:0.000} ms average, {4:0.000} ms slowest",
                    name, _physicsElapsed, _activeSteps,
                    _activeSteps > 0 ? _activeMilliseconds / _activeSteps : 0d,
                    _slowestStepMilliseconds));
        }

        // Returns whether the joint's muscles still act. Each joint's spring and
        // damper come from what it carries, so every joint follows the pose with the
        // same speed and damping; a joint is written only while it is driven, plus
        // once more when it lets go.
        private bool UpdateBoneDrive(Bone bone, bool futureActive,
            float strength, bool debug)
        {
            if (bone.Body == null || bone.Parent == null || bone.Joint == null)
                return false;

            bool hasFutureTarget = futureActive &&
                bone.AnimationTarget != null &&
                bone.AnimationParentTarget != null;
            if (!hasFutureTarget || strength <= 0f)
            {
                if (!bone.DriveOff)
                {
                    bone.Joint.slerpDrive = new JointDrive();
                    bone.DriveOff = true;
                    bone.LastSpring = 0f;
                    bone.LastMaxForce = 0f;
                }
                return false;
            }

            Quaternion target = bone.AnimationTarget.rotation;
            Quaternion parentTarget = bone.AnimationParentTarget.rotation;
            float frequency = MuscleFrequency * Mathf.Sqrt(strength / 100f);
            JointDrive drive = new JointDrive
            {
                positionSpring = bone.Inertia * frequency * frequency,
                positionDamper = 2f * MuscleDamping * bone.Inertia * frequency,
                maximumForce = _profile.BoneReplayMaximumTorque.Value *
                    Mathf.Clamp01(strength / 100f)
            };
            bone.Joint.slerpDrive = drive;
            bone.Joint.targetRotation = bone.DriveLeft *
                Quaternion.Inverse(target) * parentTarget * bone.DriveRight;
            bone.DriveOff = false;
            bone.LastSpring = drive.positionSpring;
            bone.LastMaxForce = drive.maximumForce;
            if (debug)
                MeasureDriveError(bone, Quaternion.Inverse(parentTarget) * target);
            return true;
        }

        // Debug logging only: how far the joint is from the pose, and how fast the
        // pose moves.
        private static void MeasureDriveError(Bone bone, Quaternion animatedLocal)
        {
            if (bone.HasPreviousAnimatedLocal)
                bone.LastTargetMotion = Quaternion.Angle(
                    bone.PreviousAnimatedLocal, animatedLocal);
            bone.PreviousAnimatedLocal = animatedLocal;
            bone.HasPreviousAnimatedLocal = true;

            Quaternion currentLocal = Quaternion.Inverse(bone.Parent.rotation) *
                bone.Body.rotation;
            Quaternion desiredLocal = animatedLocal *
                Quaternion.Inverse(bone.AnimationStartLocal) *
                bone.StartLocalRotation;
            (desiredLocal * Quaternion.Inverse(currentLocal))
                .ToAngleAxis(out float angle, out _);
            if (angle > 180f) angle -= 360f;
            bone.LastAngle = Mathf.Abs(angle);
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
        }

        // A limit is a spring sized by what the joint carries, so a hip and a wrist
        // are stopped equally firmly; never softer than holding the joint's own
        // weight within 1/stiffness radians of the limit (0.1 rad at 10), which is
        // what binds for short, light joints like the wrist and neck. Stiffness 0
        // makes the limit hard.
        private void ConfigureLimitSpring(Bone bone)
        {
            float stiffness = _profile.JointLimitStiffness.Value;
            float frequency = stiffness * LimitFrequencyScale;
            float spring = Mathf.Max(bone.Inertia * frequency * frequency,
                bone.GravityLoad * stiffness);
            SoftJointLimitSpring limitSpring = new SoftJointLimitSpring
            {
                spring = spring,
                damper = 2f * LimitDamping * Mathf.Sqrt(spring * bone.Inertia)
            };
            bone.Joint.angularXLimitSpring = limitSpring;
            bone.Joint.angularYZLimitSpring = limitSpring;
        }

        // Each joint carries the bodies below it: a wrist the hand, a hip the whole
        // leg, the spine the upper body and arms. Summed about the joint, that is
        // what its muscles and limits have to move.
        private void MeasureJointInertia()
        {
            float gravity = Physics.gravity.magnitude;
            foreach (Bone bone in _bones)
            {
                if (bone.Body == null || bone.Joint == null) continue;
                Vector3 pivot = bone.Body.transform.TransformPoint(
                    bone.Joint.anchor);
                float inertia = 0f;
                float load = 0f;
                foreach (Rigidbody body in _bodies)
                {
                    if (body == null || !Carries(bone.Body, body)) continue;
                    inertia += InertiaAbout(body, pivot);
                    load += body.mass *
                        Vector3.Distance(body.worldCenterOfMass, pivot);
                }
                bone.Inertia = Mathf.Max(0.001f, inertia);
                bone.GravityLoad = load * gravity;
                ConfigureLimitSpring(bone);
            }
        }

        private bool Carries(Rigidbody joint, Rigidbody body)
        {
            for (int guard = 0; body != null && guard <= _bones.Count; guard++)
            {
                if (body == joint) return true;
                if (!_boneByBody.TryGetValue(body, out Bone bone)) return false;
                body = bone.Parent;
            }
            return false;
        }

        // The body's own inertia (the mean of its principal moments) plus its mass
        // at its distance from the pivot, averaged over the three axes.
        private static float InertiaAbout(Rigidbody body, Vector3 pivot)
        {
            Vector3 tensor = body.inertiaTensor;
            float own = (tensor.x + tensor.y + tensor.z) / 3f;
            if (!(own >= 0f) || float.IsInfinity(own)) own = 0f;
            return own + body.mass * (2f / 3f) *
                (body.worldCenterOfMass - pivot).sqrMagnitude;
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

        private BoneRegion RegionOf(Rigidbody body) =>
            _regions.TryGetValue(body, out BoneRegion region)
                ? region : GetBoneRegion(body.name);

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

        // Only read by the debug log.
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

        // Every part starts the fall moving as its bone was moving in the animation,
        // not all at the body's speed with no spin: a running bot's legs and arms
        // keep their swing. The copy is stepped once here and measured; the body's
        // own momentum (MomentumScale) stays separate, only each part's motion
        // relative to the whole body is added.
        private void CarryAnimatedMotion()
        {
            float carry = Settings.CarryLimbMotion.Value;
            int count = _followers.Count;
            if (_futureAnimation == null || carry <= 0f || count == 0) return;
            if (_carryPositions.Length < count)
            {
                _carryPositions = new Vector3[count];
                _carryVelocities = new Vector3[count];
                _carryRotations = new Quaternion[count];
            }
            for (int i = 0; i < count; i++)
            {
                _carryPositions[i] = FollowerTarget(_followers[i]);
                _carryRotations[i] = _followers[i].Target.rotation;
            }
            float step = Time.fixedDeltaTime;
            _futureAnimation.StepPhysics(step);
            if (!_futureAnimation.Running) return;
            _animationAhead = true;

            Vector3 momentum = Vector3.zero;
            float mass = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3 position = FollowerTarget(_followers[i]);
                Vector3 velocity = (position - _carryPositions[i]) / step;
                _carryVelocities[i] = velocity;
                momentum += velocity * _followers[i].Body.mass;
                mass += _followers[i].Body.mass;
                _followers[i].LastTargetPosition = position;
                _followers[i].SampleAge = 0f;
            }
            if (mass <= 0f || !IsFinite(momentum)) return;
            Vector3 whole = momentum / mass;
            float spinLimit = Settings.LimbSpeedLimit.Value;
            for (int i = 0; i < count; i++)
            {
                BodyFollower follower = _followers[i];
                Vector3 relative = Vector3.ClampMagnitude(
                    _carryVelocities[i] - whole, MaximumCarriedSpeed);
                if (!IsFinite(relative)) continue;
                follower.Body.velocity += relative * carry;
                follower.TargetVelocity = Vector3.ClampMagnitude(
                    whole + relative, 10f);

                (follower.Target.rotation * Quaternion.Inverse(_carryRotations[i]))
                    .ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f;
                Vector3 spin = axis * (angle * Mathf.Deg2Rad / step);
                if (Mathf.Abs(angle) > 0.01f && IsFinite(spin))
                    follower.Body.angularVelocity +=
                        Vector3.ClampMagnitude(spin, spinLimit) * carry;
            }
        }

        private static Vector3 FollowerTarget(BodyFollower follower) =>
            follower.Target.position +
            follower.Target.TransformVector(follower.LocalOffset);

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

        private void DriveBodiesTowardAnimation(float strength, float step)
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
                Vector3 targetPosition = FollowerTarget(follower);
                follower.SampleAge += step;
                Vector3 moved = targetPosition - follower.LastTargetPosition;
                if (moved.sqrMagnitude > 0.0000001f)
                {
                    Vector3 measured = Vector3.ClampMagnitude(moved /
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

        private void UpdateFatalPush(float step)
        {
            if (_fatalPushVelocity.sqrMagnitude < 0.0001f) return;
            _fatalPushAge += step;
            float progress = Mathf.Clamp01(_fatalPushAge /
                Mathf.Max(0.05f, Settings.FatalPushDecay.Value));
            _fatalPushVelocity = _fatalPushInitialVelocity *
                (1f - ApplySmoothStep(progress));
            if (progress >= 1f) _fatalPushVelocity = Vector3.zero;
        }

        internal void CaptureFatalImpulse(Rigidbody hitBody,
            Vector3 direction, Vector3 point, float thrust)
        {
            if (hitBody != null && RegionOf(hitBody) == BoneRegion.Head)
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
                ? RegionOf(hitBody) : BoneRegion.Spine;
            Vector3 along = push.normalized;
            push = along * speed * _roundPush *
                (hitRegion == BoneRegion.Head ? 1.5f : 1f);
            bool legHit = hitRegion == BoneRegion.Leg;
            foreach (Rigidbody body in _bodies)
            {
                if (body == null || (legHit && _limpBodies.Contains(body)))
                    continue;
                float share = GetFallShare(body);
                if (legHit) share *= 0.3f;
                if (share > 0f)
                    body.AddForce(push * share, ForceMode.VelocityChange);
            }
            if (legHit) KickLeg(hitBody, along, push.magnitude, _roundPush);
        }

        // The struck leg swings back about its joint: each part moves in proportion to
        // its distance from the pivot, so the foot travels about twice as fast as the
        // knee and lifts a little off the ground. Heavy rounds kick harder.
        private void KickLeg(Rigidbody hitBody, Vector3 along, float speed,
            float roundPush)
        {
            if (!_boneByBody.TryGetValue(hitBody, out Bone hitBone) ||
                hitBone.Parent == null) return;
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
            if (RegionOf(hitBody) == BoneRegion.Spine) return;
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
            _roundPush = Impacts.RoundPush(shot);
            _hitSeverity = Mathf.Max(1f, _roundPush);
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] Killing shot {0}: {1}, {2:0} J, push x{3:0.00}",
                    name, shot.Caliber ?? "unknown caliber", shot.Energy,
                    _roundPush));
        }

        private void ApplyBlasts(bool killedByIt)
        {
            if (Impacts.LatestBlastId == _lastBlastId) return;
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
                        GetBlastExposure(RegionOf(body)) *
                        Random.Range(0.75f, 1.25f);
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

        private static float GetBlastExposure(BoneRegion region)
        {
            switch (region)
            {
                case BoneRegion.Head: return 1.1f;
                case BoneRegion.Arm: return 1.4f;
                case BoneRegion.Leg: return 1.25f;
                default: return 0.8f;
            }
        }

        private float GetFallShare(Rigidbody body)
        {
            if (body == _pelvisBody) return 0.2f;
            switch (RegionOf(body))
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
                if (!_boneByBody.TryGetValue(parent, out Bone parentBone)) break;
                depth++;
                parent = parentBone.Parent;
            }
            return depth;
        }

        private void LogJointState(string phase)
        {
            if (!Settings.DebugLogging.Value) return;
            DecayInheritedMomentum(_physicsElapsed);
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
                    "[RagdollDebug] joint={0} parent={1} error={2:0.0} targetStep={3:0.00} spring={4:0.0} maxForce={5:0} inertia={6:0.000} load={7:0.0} limits=({8:0.0},{9:0.0}) swing=({10:0.0},{11:0.0}) vel={12:0.0} parentVel={13:0.0} kin={14}/{15} sleep={16}/{17} active={18}",
                    bone.Name, bone.Parent != null ? bone.Parent.name : "NULL",
                    bone.LastAngle, bone.LastTargetMotion, bone.LastSpring,
                    bone.LastMaxForce, bone.Inertia, bone.GravityLoad,
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
