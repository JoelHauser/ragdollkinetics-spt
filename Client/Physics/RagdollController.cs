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
        private enum ReactionType
        {
            Leg,
            Hip,
            Arm
        }

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
            internal Vector3 AnimationAngularVelocity;
            internal JointProfile Profile;
            internal float BaseAngularDrag;
            internal float ReleaseScale;
            internal float DriveScale;
            internal float ToneReleaseDelay;
            internal float LastAngle;
            internal float LastSpring;
            internal float LastMaxForce;
            internal SoftJointLimit CarryLowX, CarryHighX, CarryY, CarryZ;
            internal SoftJointLimit AuthoredLowX, AuthoredHighX, AuthoredY, AuthoredZ;
            internal SoftJointLimit PassiveLowX, PassiveHighX, PassiveY, PassiveZ;
            internal bool IsStiff;
            internal float StiffIntensity;
        }

        private readonly List<Bone> _bones = new List<Bone>(20);
        private readonly RaycastHit[] _groundHits = new RaycastHit[16];
        private CorpseRagdoll _ragdoll;
        private float _bend;
        private float _forceScale;
        private float _bendForce = float.NaN;
        private float _spring;
        private float _maximumForce;
        private float _nextDebugLog;
        private int _fixedUpdates;
        private float _started;
        private float _carryDuration;
        private float _settleDuration;
        private Rigidbody _pelvisBody;
        private RagdollPoseSampler.RootMotion _rootMotion;
        private bool _hasRootMotion;
        internal bool AllowFreeze
        {
            get
            {
                return Time.time - _started >= Mathf.Max(2f,
                    Settings.FreezeDelay.Value);
            }
        }

        internal void Initialize(CorpseRagdoll ragdoll, RagdollPoseSampler sampler)
        {
            _ragdoll = ragdoll;
            _started = Time.time;
            _carryDuration = Mathf.Max(0.1f,
                Settings.AnimationCarryDuration.Value);
            _settleDuration = Mathf.Max(0.1f,
                Settings.AnimationSettleDuration.Value);
            _hasRootMotion = sampler != null && sampler.TryGetRoot(out _rootMotion);
            RefreshDriveSettings(0f);

            foreach (CharacterJointSpawner spawner in ragdoll._jointSpawners)
            {
                CharacterJoint source = spawner.Create() as CharacterJoint;
                if (source == null || source.connectedBody == null) continue;
                Rigidbody body = source.GetComponent<Rigidbody>();
                if (body == null) continue;

                JointProfile profile = GetProfile(body.name);
                ConfigurableJoint joint = ConvertJoint(source, profile, _bend,
                    _forceScale);
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
                Quaternion jointSpace = Quaternion.LookRotation(joint.axis,
                    joint.secondaryAxis);
                Vector3 animationVelocity = Vector3.zero;
                if (sampler != null && sampler.TryGet(body.name,
                    out RagdollPoseSampler.PoseMotion pose))
                    animationVelocity = pose.LocalAngularVelocity;
                bone.StartLocalRotation = startLocal;
                bone.JointSpace = jointSpace;
                bone.AnimationAngularVelocity = animationVelocity;
                bone.ReleaseScale = GetReleaseScale(body.name);
                bone.CarryLowX = CopyLimit(source.lowTwistLimit);
                bone.CarryHighX = CopyLimit(source.highTwistLimit);
                bone.CarryY = CopyLimit(source.swing1Limit);
                bone.CarryZ = CopyLimit(source.swing2Limit);
                bone.AuthoredLowX = bone.CarryLowX;
                bone.AuthoredHighX = bone.CarryHighX;
                bone.AuthoredY = bone.CarryY;
                bone.AuthoredZ = bone.CarryZ;
                UpdatePassiveProperties(bone);
                bone.DriveScale = Mathf.Clamp(profile.Damping / 14f, 0.65f, 1.4f);
            }
            ConfigureToneRelease();
            ConfigureStiffReactions();

            foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
            {
                Rigidbody body = spawner.Rigidbody;
                if (body == null) continue;
                if (body.name.ToLowerInvariant().Contains("pelvis"))
                    _pelvisBody = body;
                body.solverIterations = Mathf.Max(body.solverIterations, 12);
                body.solverVelocityIterations = Mathf.Max(
                    body.solverVelocityIterations, 6);
                body.maxAngularVelocity = Mathf.Min(body.maxAngularVelocity, 12f);
                body.drag = Mathf.Max(body.drag, 0.08f);
            }
            ragdoll.WakeUp();
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] Initialized corpse={0} bones={1} spawners={2} force={3:0} scale={4:0.00} physicsDone={5}",
                    name, _bones.Count, ragdoll._jointSpawners.Length,
                    _bendForce, _forceScale,
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
            return joint;
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

            float elapsed = Time.time - _started;
            RefreshDriveSettings(elapsed);

            float carryStrength = Mathf.Max(0.1f,
                Settings.AnimationCarryStrength.Value);
            if (_bend > 0.001f)
                DrivePelvis(elapsed, carryStrength);
            foreach (Bone bone in _bones)
            {
                if (bone.Body == null || bone.Parent == null || bone.Joint == null)
                    continue;
                float duration = _carryDuration * bone.ReleaseScale +
                    bone.ToneReleaseDelay;
                float progress = Mathf.Clamp01(elapsed / duration);
                float influence = 1f - SmoothStep(progress);
                if (bone.IsStiff)
                    influence = Mathf.Max(influence, bone.StiffIntensity);
                float settleProgress = Mathf.Clamp01(
                    (elapsed - duration) / _settleDuration);
                float settleInfluence = elapsed <= duration ? 1f :
                    1f - SmoothStep(settleProgress);
                if (bone.IsStiff)
                    settleInfluence = Mathf.Max(settleInfluence,
                        bone.StiffIntensity);
                float passiveBlend = SmoothStep(progress);
                bone.Joint.lowAngularXLimit = LerpLimit(
                    bone.CarryLowX, bone.PassiveLowX, passiveBlend);
                bone.Joint.highAngularXLimit = LerpLimit(
                    bone.CarryHighX, bone.PassiveHighX, passiveBlend);
                bone.Joint.angularYLimit = LerpLimit(
                    bone.CarryY, bone.PassiveY, passiveBlend);
                bone.Joint.angularZLimit = LerpLimit(
                    bone.CarryZ, bone.PassiveZ, passiveBlend);
                float motionTime = duration * (progress - 0.5f * progress * progress);
                Vector3 animation = bone.AnimationAngularVelocity;
                if (bone.IsStiff) animation = Vector3.zero;
                float animationAngle = animation.magnitude * motionTime;
                Quaternion animatedLocal = animation.sqrMagnitude > 0.0001f
                    ? Quaternion.AngleAxis(animationAngle, animation.normalized) *
                        bone.StartLocalRotation
                    : bone.StartLocalRotation;

                Quaternion currentLocal = Quaternion.Inverse(bone.Parent.rotation) *
                    bone.Body.rotation;
                Quaternion error = animatedLocal *
                    Quaternion.Inverse(currentLocal);
                error.ToAngleAxis(out float angle, out _);
                if (angle > 180f) angle -= 360f;
                bone.LastAngle = Mathf.Abs(angle);

                JointDrive drive = bone.Joint.slerpDrive;
                float animationSpring = _spring * carryStrength;
                if (bone.IsStiff)
                {
                    float poweredExtensionSpring = Mathf.Lerp(2500f, 24000f,
                        bone.StiffIntensity * bone.StiffIntensity);
                    animationSpring = Mathf.Max(animationSpring,
                        poweredExtensionSpring);
                }
                float animationDamper = 2.5f * Mathf.Sqrt(animationSpring);
                drive.positionSpring = animationSpring * bone.DriveScale * influence;
                drive.positionDamper = animationDamper * bone.DriveScale *
                    Mathf.Max(Mathf.Sqrt(influence), settleInfluence);
                float driveForce = _maximumForce * carryStrength;
                if (bone.IsStiff)
                    driveForce = Mathf.Max(driveForce,
                        Mathf.Lerp(12000f, 90000f, bone.StiffIntensity));
                drive.maximumForce = driveForce * bone.DriveScale *
                    Mathf.Max(Mathf.Sqrt(influence), settleInfluence);
                bone.Joint.slerpDrive = drive;
                bone.Joint.targetAngularVelocity = Vector3.zero;
                bone.Joint.targetRotation = Quaternion.Inverse(bone.JointSpace) *
                    (Quaternion.Inverse(animatedLocal) *
                    bone.StartLocalRotation) * bone.JointSpace;
                bone.LastSpring = drive.positionSpring;
                bone.LastMaxForce = drive.maximumForce;
            }

            if (Settings.DebugLogging.Value && Time.unscaledTime >= _nextDebugLog)
            {
                _nextDebugLog = Time.unscaledTime + 1f;
                LogJointState("live");
            }
        }

        private void DrivePelvis(float elapsed, float carryStrength)
        {
            if (!_hasRootMotion || _pelvisBody == null) return;
            float progress = Mathf.Clamp01(elapsed / _carryDuration);
            float influence = 1f - SmoothStep(progress);
            float settleProgress = Mathf.Clamp01(
                (elapsed - _carryDuration) / _settleDuration);
            float settleInfluence = elapsed <= _carryDuration ? 1f :
                1f - SmoothStep(settleProgress);
            if (influence <= 0.001f && settleInfluence <= 0.001f) return;

            float carriedTime = Mathf.Min(elapsed, _carryDuration);
            Vector3 target = _rootMotion.Position +
                _rootMotion.HorizontalVelocity * carriedTime;
            Vector3 positionError = target - _pelvisBody.position;
            positionError.y = 0f;
            Vector3 desiredVelocity = _rootMotion.HorizontalVelocity * influence;
            Vector3 velocityError = desiredVelocity -
                Vector3.ProjectOnPlane(_pelvisBody.velocity, Vector3.up);
            Vector3 acceleration = positionError * (18f * carryStrength * influence) +
                velocityError * (7f * Mathf.Sqrt(carryStrength) * settleInfluence);
            _pelvisBody.AddForce(Vector3.ClampMagnitude(acceleration, 35f) *
                settleInfluence, ForceMode.Acceleration);

            Vector3 targetForward = Quaternion.AngleAxis(
                _rootMotion.YawVelocity * elapsed, Vector3.up) *
                (_rootMotion.Rotation * Vector3.forward);
            targetForward = Vector3.ProjectOnPlane(targetForward, Vector3.up);
            Vector3 currentForward = Vector3.ProjectOnPlane(
                _pelvisBody.rotation * Vector3.forward, Vector3.up);
            float yawError = 0f;
            if (targetForward.sqrMagnitude > 0.001f &&
                currentForward.sqrMagnitude > 0.001f)
                yawError = Vector3.SignedAngle(currentForward,
                    targetForward, Vector3.up) * Mathf.Deg2Rad;
            Vector3 angular = _pelvisBody.angularVelocity;
            float desiredYaw = _rootMotion.YawVelocity * Mathf.Deg2Rad * influence;
            float yawTorque = yawError * (35f * carryStrength * influence) -
                (angular.y - desiredYaw) * (9f * Mathf.Sqrt(carryStrength));
            Vector3 tiltVelocity = new Vector3(angular.x, 0f, angular.z);
            Vector3 torque = Vector3.up * yawTorque - tiltVelocity *
                (5f * Mathf.Sqrt(carryStrength));
            _pelvisBody.AddTorque(Vector3.ClampMagnitude(torque, 60f) *
                settleInfluence, ForceMode.Acceleration);
        }

        private void RefreshDriveSettings(float elapsed)
        {
            float duration = Mathf.Max(0.1f,
                Settings.BendForceDecayDuration.Value);
            float progress = Mathf.Clamp01(elapsed / duration);
            switch (Settings.BendForceDecayCurve.Value)
            {
                case Settings.BendDecayCurve.SlowStart:
                    progress *= progress;
                    break;
                case Settings.BendDecayCurve.SlowEnd:
                    progress = 1f - (1f - progress) * (1f - progress);
                    break;
            }
            float bendForce = Mathf.Lerp(Settings.StartBendForce.Value,
                Settings.EndBendForce.Value, progress);
            if (bendForce == _bendForce) return;

            _bendForce = bendForce;
            _forceScale = Mathf.Max(0f, bendForce / 100f);
            _bend = Mathf.Clamp01(_forceScale);
            float extendedScale = Mathf.Max(1f, _forceScale);
            _spring = 1200f * _bend * _bend * _bend * extendedScale;
            _maximumForce = 2500f * extendedScale * extendedScale;
            foreach (Bone bone in _bones)
                UpdatePassiveProperties(bone);
        }

        private void UpdatePassiveProperties(Bone bone)
        {
            float extendedLimit = 1f / Mathf.Sqrt(Mathf.Max(1f, _forceScale));
            float twistScale = Mathf.Lerp(1f,
                bone.Profile.TwistScale * extendedLimit, _bend);
            float swingScale = Mathf.Lerp(1f,
                bone.Profile.SwingScale * extendedLimit, _bend);

            bone.PassiveLowX = ScaleLimit(bone.AuthoredLowX, twistScale, true);
            bone.PassiveHighX = ScaleLimit(bone.AuthoredHighX, twistScale, false);
            bone.PassiveY = ScaleLimit(bone.AuthoredY, swingScale, false);
            bone.PassiveZ = ScaleLimit(bone.AuthoredZ, swingScale, false);
            bone.Body.angularDrag = Mathf.Lerp(bone.BaseAngularDrag,
                Mathf.Max(bone.BaseAngularDrag, bone.Profile.AngularDrag), _bend);
        }

        private void ConfigureStiffReactions()
        {
            if (!_hasRootMotion || _rootMotion.PoseLevel < 0.55f) return;
            TryApplyStiffReaction(Settings.StiffLeg.Value,
                Settings.StiffLegChance.Value, Settings.StiffLegStrengthMin.Value,
                Settings.StiffLegStrengthMax.Value, ReactionType.Leg);
            TryApplyStiffReaction(Settings.StiffHip.Value,
                Settings.StiffHipChance.Value, Settings.StiffHipStrengthMin.Value,
                Settings.StiffHipStrengthMax.Value, ReactionType.Hip);
            TryApplyStiffReaction(Settings.StiffArm.Value,
                Settings.StiffArmChance.Value, Settings.StiffArmStrengthMin.Value,
                Settings.StiffArmStrengthMax.Value, ReactionType.Arm);
        }

        private void TryApplyStiffReaction(bool enabled, float chance,
            float minimumStrength, float maximumStrength, ReactionType type)
        {
            float normalizedChance = Mathf.Clamp01(chance / 100f);
            if (!enabled || normalizedChance <= 0f ||
                (normalizedChance < 1f &&
                    UnityEngine.Random.value >= normalizedChance)) return;
            bool selectLeft = UnityEngine.Random.value < 0.5f;
            float low = Mathf.Min(minimumStrength, maximumStrength);
            float high = Mathf.Max(minimumStrength, maximumStrength);
            float intensity = Mathf.Clamp01(UnityEngine.Random.Range(low, high) / 100f);
            if (intensity <= 0f) return;
            if (type == ReactionType.Leg && Settings.SupportAndTone.Value &&
                !SelectedSideHasGroundSupport(selectLeft))
                intensity *= Mathf.Clamp01(
                    Settings.UnsupportedLegStrength.Value / 100f);
            if (intensity <= 0f) return;
            foreach (Bone bone in _bones)
            {
                string lower = (bone.Name ?? string.Empty).ToLowerInvariant();
                bool match = type == ReactionType.Leg
                    ? Has(lower, "calf", "shin", "lowerleg")
                    : type == ReactionType.Hip
                        ? Has(lower, "thigh", "upleg")
                        : Has(lower, "upperarm", "forearm", "lowerarm");
                bool left = lower.Contains("left") || lower.Contains("humanl");
                if (!match || left != selectLeft) continue;
                bone.IsStiff = true;
                bone.StiffIntensity = Mathf.Max(bone.StiffIntensity, intensity);
                bone.AnimationAngularVelocity = Vector3.Lerp(
                    bone.AnimationAngularVelocity, Vector3.zero, intensity);
                bone.CarryLowX = LerpLimit(bone.CarryLowX,
                    bone.PassiveLowX, intensity);
                bone.CarryHighX = LerpLimit(bone.CarryHighX,
                    bone.PassiveHighX, intensity);
                bone.CarryY = LerpLimit(bone.CarryY,
                    bone.PassiveY, intensity);
                bone.CarryZ = LerpLimit(bone.CarryZ,
                    bone.PassiveZ, intensity);
            }
        }

        private void ConfigureToneRelease()
        {
            if (!Settings.SupportAndTone.Value) return;
            float spread = Mathf.Max(0f,
                Settings.ToneReleaseSpread.Value);
            foreach (Bone bone in _bones)
            {
                string lower = (bone.Name ?? string.Empty).ToLowerInvariant();
                float delay = UnityEngine.Random.Range(0f, spread);
                if (Has(lower, "spine", "chest", "rib", "pelvis"))
                    delay += spread * 0.6f;
                else if (Has(lower, "head", "neck", "hand", "wrist", "foot"))
                    delay *= 0.35f;
                bone.ToneReleaseDelay = delay;
            }
        }

        private bool SelectedSideHasGroundSupport(bool selectLeft)
        {
            foreach (Bone bone in _bones)
            {
                string lower = (bone.Name ?? string.Empty).ToLowerInvariant();
                bool left = lower.Contains("left") || lower.Contains("humanl");
                if (left != selectLeft ||
                    !Has(lower, "calf", "shin", "lowerleg", "foot")) continue;
                Vector3 origin = bone.Body.worldCenterOfMass + Vector3.up * 0.08f;
                int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down,
                    _groundHits, 1.15f, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hitCount; i++)
                {
                    RaycastHit hit = _groundHits[i];
                    if (hit.collider == null) continue;
                    Rigidbody hitBody = hit.collider.attachedRigidbody;
                    if (hitBody == bone.Body || hit.collider.transform.IsChildOf(transform))
                        continue;
                    return true;
                }
            }
            return false;
        }

        private void LogJointState(string phase)
        {
            if (!Settings.DebugLogging.Value) return;
            Plugin.Log.LogInfo(string.Format(
                "[RagdollDebug] {0} corpse={1} fixed={2} force={3:0} scale={4:0.00} bones={5} physicsDone={6}",
                phase, name, _fixedUpdates, _bendForce,
                _forceScale, _bones.Count, _ragdoll != null && _ragdoll._isPhysicsDone));
            foreach (Bone bone in _bones)
            {
                if (bone.Body == null || bone.Joint == null)
                {
                    Plugin.Log.LogWarning("[RagdollDebug] missing bone/joint " + bone.Name);
                    continue;
                }
                Plugin.Log.LogInfo(string.Format(
                    "[RagdollDebug] joint={0} parent={1} error={2:0.0} spring={3:0} maxForce={4:0} limits=({5:0.0},{6:0.0}) swing=({7:0.0},{8:0.0}) vel={9:0.0} parentVel={10:0.0} kin={11}/{12} sleep={13}/{14} active={15}",
                    bone.Name, bone.Parent != null ? bone.Parent.name : "NULL",
                    bone.LastAngle, bone.LastSpring, bone.LastMaxForce,
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

        private static float GetReleaseScale(string boneName)
        {
            string name = (boneName ?? string.Empty).ToLowerInvariant();
            if (Has(name, "head", "neck")) return 0.45f;
            if (Has(name, "upperarm", "forearm", "hand")) return 0.65f;
            if (Has(name, "spine", "chest", "rib")) return 0.85f;
            if (Has(name, "thigh", "calf", "foot")) return 1.15f;
            return 1f;
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
