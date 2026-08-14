using System.Collections.Generic;
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

        private readonly Dictionary<string, Quaternion> _previous =
            new Dictionary<string, Quaternion>(20);
        private readonly Dictionary<string, PoseMotion> _motion =
            new Dictionary<string, PoseMotion>(20);
        private CharacterJointSpawner[] _spawners;
        private Transform _pelvis;
        private Player _player;
        private Vector3 _previousPelvisPosition;
        private Quaternion _previousPelvisRotation;
        private RootMotion _rootMotion;
        private bool _initialized;
        private bool _hasRootSample;
        private bool _frozen;

        internal void Sample(float deltaTime)
        {
            if (_frozen || deltaTime <= 0.0001f) return;
            if (!_initialized)
            {
                _initialized = true;
                _player = GetComponent<Player>();
                _spawners = GetComponentsInChildren<CharacterJointSpawner>(true);
                RigidbodySpawner[] bodies =
                    GetComponentsInChildren<RigidbodySpawner>(true);
                foreach (RigidbodySpawner body in bodies)
                    if (body != null && body.name.ToLowerInvariant().Contains("pelvis"))
                    {
                        _pelvis = body.transform;
                        break;
                    }
            }

            float inverseDelta = 1f / deltaTime;
            foreach (CharacterJointSpawner spawner in _spawners)
            {
                if (spawner == null) continue;
                string key = spawner.gameObject.name;
                Quaternion current = spawner.transform.localRotation;
                Vector3 angularVelocity = Vector3.zero;
                if (_previous.TryGetValue(key, out Quaternion previous))
                {
                    Quaternion delta = current * Quaternion.Inverse(previous);
                    delta.ToAngleAxis(out float angle, out Vector3 axis);
                    if (angle > 180f) angle -= 360f;
                    if (IsFinite(angle) && IsFinite(axis) &&
                        axis.sqrMagnitude > 0.0001f)
                    {
                        Vector3 measured = axis.normalized * angle * inverseDelta;
                        if (Mathf.Abs(angle) > 18f) measured = Vector3.zero;
                        if (_motion.TryGetValue(key, out PoseMotion prior))
                            angularVelocity = Vector3.Lerp(
                                prior.LocalAngularVelocity, measured, 0.35f);
                        else
                            angularVelocity = measured;
                    }
                }
                angularVelocity = Vector3.ClampMagnitude(angularVelocity, 240f);
                _previous[key] = current;
                _motion[key] = new PoseMotion(current, angularVelocity);
            }
            SampleRoot(deltaTime);
        }

        private void SampleRoot(float deltaTime)
        {
            if (_pelvis == null) return;
            Vector3 position = _pelvis.position;
            Quaternion rotation = _pelvis.rotation;
            Vector3 horizontalVelocity = Vector3.zero;
            float yawVelocity = 0f;
            if (_hasRootSample)
            {
                horizontalVelocity = _player != null
                    ? _player.Velocity
                    : (position - _previousPelvisPosition) / deltaTime;
                horizontalVelocity.y = 0f;
                horizontalVelocity = Vector3.ClampMagnitude(horizontalVelocity, 8f);
                Quaternion heading = _player != null
                    ? _player.Transform.rotation : rotation;
                Quaternion previousHeading = _hasRootSample && _player == null
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
            _hasRootSample = true;
        }

        internal void Freeze() { _frozen = true; }

        internal bool TryGet(string name, out PoseMotion motion)
        {
            return _motion.TryGetValue(name, out motion);
        }

        internal bool TryGetRoot(out RootMotion motion)
        {
            motion = _rootMotion;
            return _hasRootSample;
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

        private sealed class Bone
        {
            internal string Name;
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
        internal bool AllowFreeze
        {
            get
            {
                return Time.time - _started >= Mathf.Max(2f,
                    Settings.FreezeDelay.Value);
            }
        }

        internal void Initialize(CorpseRagdoll ragdoll,
            FutureAnimationDriver futureAnimation = null)
        {
            _ragdoll = ragdoll;
            _futureAnimation = futureAnimation != null && futureAnimation.Running
                ? futureAnimation : null;
            _profile = _futureAnimation != null
                ? _futureAnimation.Profile : Settings.StandingDeath;
            _futureAnimation?.UsePhysicsClock();
            _started = Time.time;

            foreach (CharacterJointSpawner spawner in ragdoll._jointSpawners)
            {
                CharacterJoint source = spawner.Create() as CharacterJoint;
                if (source == null || source.connectedBody == null) continue;
                Rigidbody body = source.GetComponent<Rigidbody>();
                if (body == null) continue;

                JointProfile profile = GetProfile(body.name);
                ConfigurableJoint joint = ConvertJoint(source, profile, 0f, 0f);
                Bone bone = new Bone
                {
                    Name = body.name,
                    Body = body,
                    Parent = joint.connectedBody,
                    SourceJoint = source,
                    Joint = joint,
                    Profile = profile,
                    BaseAngularDrag = body.angularDrag
                };
                _bones.Add(bone);
                ConfigureBody(body);
                Quaternion startLocal = Quaternion.Inverse(
                    joint.connectedBody.rotation) * body.rotation;
                Quaternion jointSpace = BuildJointSpace(joint.axis,
                    joint.secondaryAxis);
                bone.StartLocalRotation = startLocal;
                bone.JointSpace = jointSpace;
                if (_futureAnimation != null &&
                    _futureAnimation.TryGetTarget(body.name,
                        out Transform animationTarget))
                {
                    bone.AnimationTarget = animationTarget;
                    _futureAnimation.TryGetTarget(joint.connectedBody.name,
                        out bone.AnimationParentTarget);
                    if (bone.AnimationParentTarget != null)
                        bone.AnimationStartLocal = Quaternion.Inverse(
                            bone.AnimationParentTarget.rotation) *
                            bone.AnimationTarget.rotation;
                }
                bone.CarryLowX = CopyLimit(source.lowTwistLimit);
                bone.CarryHighX = CopyLimit(source.highTwistLimit);
                bone.CarryY = CopyLimit(source.swing1Limit);
                bone.CarryZ = CopyLimit(source.swing2Limit);
                bone.AuthoredLowX = bone.CarryLowX;
                bone.AuthoredHighX = bone.CarryHighX;
                bone.AuthoredY = bone.CarryY;
                bone.AuthoredZ = bone.CarryZ;
                ConfigureJointLimits(bone);
                UpdatePassiveProperties(bone, 0f, 0f);
                bone.DriveScale = Mathf.Clamp(profile.Damping / 14f, 0.65f, 1.4f);
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
                body.solverIterations = Mathf.Max(body.solverIterations, 12);
                body.solverVelocityIterations = Mathf.Max(
                    body.solverVelocityIterations, 6);
                body.maxAngularVelocity = Mathf.Min(
                    body.maxAngularVelocity, 12f);
                body.mass *= Settings.RagdollMassScale.Value;
                body.drag = Mathf.Max(body.drag, 0.08f);
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.isKinematic = false;
                body.detectCollisions = true;
                _bodies.Add(body);
                string lowerName = body.name.ToLowerInvariant();
                if (_pelvisBody == null && lowerName.Contains("pelvis"))
                    _pelvisBody = body;
                body.velocity = new Vector3(_remainingInheritedMomentum.x,
                    body.velocity.y, _remainingInheritedMomentum.z);
                body.angularVelocity = Vector3.zero;

            }
            _bones.Sort((left, right) => GetBoneDepth(left).CompareTo(
                GetBoneDepth(right)));
            InitializeBodyFollowers();
            ragdoll.WakeUp();
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

        private void OnDestroy()
        {
            foreach (Bone bone in _bones)
                if (bone.Joint != null)
                    Object.Destroy(bone.Joint);
            _bones.Clear();
            _followers.Clear();
            _bodies.Clear();
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

            SoftJointLimit low = source.lowTwistLimit;
            low.limit = Mathf.Min(-0.1f, low.limit * twistScale);
            low.bounciness = 0f;
            joint.lowAngularXLimit = low;
            SoftJointLimit high = source.highTwistLimit;
            high.limit = Mathf.Max(0.1f, high.limit * twistScale);
            high.bounciness = 0f;
            joint.highAngularXLimit = high;
            SoftJointLimit swing1 = source.swing1Limit;
            swing1.limit = Mathf.Max(0.1f, swing1.limit * swingScale);
            swing1.bounciness = 0f;
            joint.angularYLimit = swing1;
            SoftJointLimit swing2 = source.swing2Limit;
            swing2.limit = Mathf.Max(0.1f, swing2.limit * swingScale);
            swing2.bounciness = 0f;
            joint.angularZLimit = swing2;
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.configuredInWorldSpace = false;
            joint.swapBodies = false;
            return joint;
        }

        private static Quaternion BuildJointSpace(Vector3 axis,
            Vector3 secondaryAxis)
        {
            // ConfigurableJoint.axis is joint-space right, not forward.
            // LookRotation(axis, secondaryAxis) therefore rotates every target
            // into the wrong basis. Construct Unity's documented orthonormal
            // joint frame explicitly.
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

        private static void ConfigureBody(Rigidbody body)
        {
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

            // Discard the backlog Unity accumulated while synchronous death
            // creation blocked the frame. The ragdoll did not exist then.
            if (_firstPhysicsFrame < 0)
                _firstPhysicsFrame = Time.frameCount;
            if (Time.frameCount == _firstPhysicsFrame) return;

            _futureAnimation?.StepPhysics(Time.fixedDeltaTime);
            _physicsElapsed += Time.fixedDeltaTime;
            float elapsed = _physicsElapsed;
            DecayInheritedMomentum(elapsed);
            float worldFollowStrength = GetWorldFollowStrength(elapsed);
            float boneReplayStrength = GetBoneReplayStrength(elapsed);
            bool futureActive = _futureAnimation != null &&
                _futureAnimation.Running;
            UpdateFatalPush();
            ApplyBodyFollowing(futureActive ? worldFollowStrength : 0f);
            foreach (Bone bone in _bones)
            {
                if (bone.Body == null || bone.Parent == null || bone.Joint == null)
                    continue;
                bool futureTarget = futureActive &&
                    bone.AnimationTarget != null &&
                    bone.AnimationParentTarget != null;
                float followStrength = futureTarget
                    ? boneReplayStrength : 0f;
                // Measure between the two animated rigidbody bones, not the
                // target Transform's immediate parent. EFT inserts clavicle,
                // twist and helper bones which do not exist in its ragdoll.
                // This relative rotation folds that entire helper chain into
                // the one physical joint and remains independent of world pose.
                Quaternion animatedLocal = futureTarget
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
                Quaternion error = animatedLocal *
                    Quaternion.Inverse(currentLocal);
                error.ToAngleAxis(out float angle, out _);
                if (angle > 180f) angle -= 360f;
                bone.LastAngle = Mathf.Abs(angle);

                ApplyAnimationDrive(bone, animatedLocal, followStrength);
            }

            if (Settings.DebugLogging.Value && Time.unscaledTime >= _nextDebugLog)
            {
                _nextDebugLog = Time.unscaledTime + 1f;
                LogJointState("live");
            }
        }

        private void ApplyAnimationDrive(Bone bone,
            Quaternion animationLocal, float strength)
        {
            bone.Joint.angularXMotion = ConfigurableJointMotion.Limited;
            bone.Joint.angularYMotion = ConfigurableJointMotion.Limited;
            bone.Joint.angularZMotion = ConfigurableJointMotion.Limited;
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
            bone.Joint.lowAngularXLimit = ScaleLimit(
                bone.AuthoredLowX, range, true);
            bone.Joint.highAngularXLimit = ScaleLimit(
                bone.AuthoredHighX, range, false);
            bone.Joint.angularYLimit = ScaleLimit(
                bone.AuthoredY, range, false);
            bone.Joint.angularZLimit = ScaleLimit(
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
            return Curve(_profile.WorldFollowStrength.Value, 0f,
                _profile.WorldFollowDecay.Value, elapsed);
        }

        private float GetBoneReplayStrength(float elapsed)
        {
            return Curve(_profile.BoneReplayStrength.Value, 0f,
                _profile.BoneReplayDecay.Value, elapsed);
        }

        private static float Curve(float start, float end, float duration,
            float elapsed)
        {
            float progress = Mathf.Clamp01(elapsed /
                Mathf.Max(0.05f, duration));
            return Mathf.Lerp(start, end, SmoothStep(progress));
        }

        private void DecayInheritedMomentum(float elapsed)
        {
            float duration = Mathf.Max(0.05f,
                _profile.MomentumDecay.Value);
            float progress = Mathf.Clamp01(elapsed / duration);
            float retained = 1f - SmoothStep(progress);
            _remainingInheritedMomentum = _initialInheritedMomentum * retained;
        }

        private void InitializeBodyFollowers()
        {
            if (_futureAnimation == null) return;
            foreach (Rigidbody body in _bodies)
            {
                if (body == null || !_futureAnimation.TryGetTarget(body.name,
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

        private void ApplyBodyFollowing(float strength)
        {
            float driveScale = Mathf.Max(0f, strength / 100f);
            if (driveScale <= 0f) return;
            float gravityInfluence = Mathf.Clamp01(driveScale);
            float spring = 80f * driveScale;
            float damper = 2f * Mathf.Sqrt(spring);
            float maximumAcceleration = 60f * driveScale;
            foreach (BodyFollower follower in _followers)
            {
                if (follower.Body == null || follower.Target == null) continue;
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
                // Gravity compensation is essential: without it the body must
                // sag substantially before a position spring can support it.
                Vector3 acceleration = -Physics.gravity * gravityInfluence +
                    (targetPosition - follower.Body.worldCenterOfMass) * spring +
                    (follower.TargetVelocity + _fatalPushVelocity -
                        follower.Body.velocity) * damper;
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
                (1f - SmoothStep(progress));
            if (progress >= 1f) _fatalPushVelocity = Vector3.zero;
        }

        internal bool CaptureFatalImpulse(Rigidbody hitBody,
            Vector3 direction, Vector3 point, float thrust)
        {
            if (direction.sqrMagnitude < 0.0001f || thrust <= 0f) return false;
            // EFT's thrust is an instantaneous per-bone impulse. Converting it
            // directly into root velocity is far too energetic, so retain only
            // a small fraction for the animation-owned pelvis displacement.
            Vector3 push = direction.normalized * thrust *
                Settings.ImpulseScale.Value * 0.2f;
            _fatalPushVelocity = Vector3.ClampMagnitude(
                _fatalPushVelocity + push, 1.25f);
            _fatalPushInitialVelocity = _fatalPushVelocity;
            _fatalPushAge = 0f;
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] Cached fatal push body={0} direction={1} thrust={2:0.00} velocity={3}",
                    hitBody != null ? hitBody.name : "NULL", direction,
                    thrust, _fatalPushVelocity));
            return true;
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

            bone.PassiveLowX = ScaleLimit(bone.AuthoredLowX, twistScale, true);
            bone.PassiveHighX = ScaleLimit(bone.AuthoredHighX, twistScale, false);
            bone.PassiveY = ScaleLimit(bone.AuthoredY, swingScale, false);
            bone.PassiveZ = ScaleLimit(bone.AuthoredZ, swingScale, false);
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

        private static JointProfile GetProfile(string boneName)
        {
            string name = (boneName ?? string.Empty).ToLowerInvariant();
            if (Has(name, "calf", "shin", "lowerleg"))
                return new JointProfile(0.28f, 0.22f, 2.8f, 18f); // knee
            if (Has(name, "forearm", "lowerarm"))
                return new JointProfile(0.35f, 0.28f, 2.3f, 15f); // elbow
            if (Has(name, "thigh", "upleg"))
                return new JointProfile(0.55f, 0.58f, 2.2f, 14f); // hip
            if (Has(name, "upperarm", "shoulder"))
                return new JointProfile(0.58f, 0.62f, 1.8f, 12f); // shoulder
            if (Has(name, "hand", "wrist"))
                return new JointProfile(0.38f, 0.35f, 1.7f, 10f); // wrist
            if (Has(name, "foot", "ankle"))
                return new JointProfile(0.32f, 0.28f, 2.0f, 13f); // ankle
            if (Has(name, "head", "neck"))
                return new JointProfile(0.42f, 0.45f, 1.6f, 11f); // neck
            if (Has(name, "spine", "chest", "rib", "pelvis"))
                return new JointProfile(0.38f, 0.42f, 3.0f, 20f); // torso
            return new JointProfile(0.50f, 0.50f, 2.0f, 12f);
        }

        private static SoftJointLimit CopyLimit(SoftJointLimit source)
        {
            source.bounciness = 0f;
            return source;
        }

        private static SoftJointLimit ScaleLimit(SoftJointLimit source,
            float scale, bool negative)
        {
            source.limit = negative
                ? Mathf.Min(-0.1f, source.limit * scale)
                : Mathf.Max(0.1f, source.limit * scale);
            source.bounciness = 0f;
            return source;
        }

        private static SoftJointLimit LerpLimit(SoftJointLimit from,
            SoftJointLimit to, float t)
        {
            SoftJointLimit result = to;
            result.limit = Mathf.Lerp(from.limit, to.limit, t);
            result.contactDistance = Mathf.Lerp(from.contactDistance,
                to.contactDistance, t);
            result.bounciness = 0f;
            return result;
        }

        private static float SmoothStep(float value)
        {
            return value * value * (3f - 2f * value);
        }

        private static bool Has(string value, params string[] fragments)
        {
            for (int i = 0; i < fragments.Length; i++)
                if (value.Contains(fragments[i])) return true;
            return false;
        }
    }
}
