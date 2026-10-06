using System;
using System.Collections.Generic;
using AnimationSystem;
using AnimationSystem.RootMotionTable;
using Comfort.Common;
using Diz.Resources;
using EFT;
using EFT.AssetsManager;
using FastAnimatorSystem;
using FastAnimatorSystem.JsonSerialization;
using UnityEngine;

namespace RagdollKinetics.Patches
{
    internal sealed class FutureAnimationDriver : MonoBehaviour
    {
        private readonly struct MotionSample
        {
            internal readonly float Time;
            internal readonly Vector3 Position;

            internal MotionSample(float time, Vector3 position)
            {
                Time = time;
                Position = position;
            }
        }

        private readonly Dictionary<string, Transform> _animationTargetsByName =
            new Dictionary<string, Transform>(64);
        private readonly List<MotionSample> _motionSamples =
            new List<MotionSample>(20);
        private GameObject _driverRoot;
        private Player _player;
        private Transform _sourceRoot;
        private FastAnimatorProcessor _source;
        private FastAnimatorProcessor _driver;
        private Animator _sourceUnity;
        private Animator _driverUnity;
        private PlayableAnimator _playable;
        private bool _fastMode;
        private bool _ready;
        private bool _running;
        private bool _reportedUnavailable;
        private float _simulationElapsed;
        private int _captureFrame = -1;
        private bool _discardFirstPostCaptureUpdate;
        private bool _physicsDriven;
        private Vector3 _deathRootPosition;
        private Quaternion _deathRootRotation;
        private Vector3 _deathVelocity;
        private Transform _motionRoot;
        private Transform _sourceMotionRoot;
        private Vector3 _sourceMotionRootDeathPosition;
        private Vector3 _motionRootReference;
        private bool _hasMotionRootReference;
        private float _lastPoseLevel = 1f;
        private Settings.DeathMotion _deathMotion = Settings.DeathMotion.Standing;
        private static int _lastBuildFrame = -1;
        private long _buildStarted;

        internal bool Running => _running && _ready;
        internal Vector3 TravelVelocity => _deathVelocity;
        internal Settings.DeathMotion DeathMotion => _deathMotion;
        internal Settings.DeathProfile Profile =>
            Settings.GetDeathProfile(_deathMotion);

        internal void CaptureLivingMotion()
        {
            if (_sourceRoot == null || _running) return;
            if (_player != null) _lastPoseLevel = _player.PoseLevel;
            float now = Time.time;
            Vector3 position = _sourceRoot.position;
            if (_motionSamples.Count > 0)
            {
                MotionSample previous =
                    _motionSamples[_motionSamples.Count - 1];
                float dt = now - previous.Time;
                if (dt <= 0f || Vector3.Distance(previous.Position, position) >
                    Mathf.Max(1.25f, dt * 12f))
                    _motionSamples.Clear();
            }
            _motionSamples.Add(new MotionSample(now, position));
            while (_motionSamples.Count > 2 &&
                now - _motionSamples[0].Time > 0.35f)
                _motionSamples.RemoveAt(0);
            while (_motionSamples.Count > 20)
                _motionSamples.RemoveAt(0);
        }

        internal void PrepareAnimationDriver(Player player)
        {
            if (_ready)
            {
                bool alive = player != null &&
                    player.ActiveHealthController != null &&
                    player.ActiveHealthController.IsAlive;
                if (!_running || !alive) return;
                if (Settings.DebugLogging.Value)
                    Plugin.Log.LogInfo("[FutureAnimation] Refreshing pooled " +
                        "player for new life old=" + name + " new=" +
                        player.name);
                DestroyDriver();
                _source = null;
                _driver = null;
                _sourceUnity = null;
                _driverUnity = null;
                _playable = null;
            }
            if (player == null || player.PlayerBones == null ||
                player.PlayerBones.PlayableAnimator == null)
            {
                ReportUnavailable("player bones/playable animator not ready");
                return;
            }

            _player = player;
            PlayableAnimator sourcePlayable =
                player.PlayerBones.PlayableAnimator;
            _source = sourcePlayable.FastAnimator as FastAnimatorProcessor;
            _sourceUnity = FindBodyAnimator(player, sourcePlayable);
            _sourceRoot = player.PlayerBones.BodyTransform.Original;
            _sourceMotionRoot = FindDescendant(_sourceRoot,
                "Base HumanPelvis");
            _fastMode = sourcePlayable.Initialized && _source != null;
            if (!_fastMode && (_sourceUnity == null ||
                _sourceUnity.runtimeAnimatorController == null))
            {
                ReportUnavailable("playable initialized=" +
                    sourcePlayable.Initialized + " animator=" +
                    (sourcePlayable.FastAnimator != null
                        ? sourcePlayable.FastAnimator.GetType().FullName
                        : "null") + " unity=" +
                    (_sourceUnity != null ? _sourceUnity.name : "null") +
                    " controller=" + (_sourceUnity != null &&
                    _sourceUnity.runtimeAnimatorController != null) +
                    " root=" + (_sourceRoot != null));
                return;
            }
            if (_sourceRoot == null) return;
            if (Time.frameCount == _lastBuildFrame) return;
            _lastBuildFrame = Time.frameCount;
            _buildStarted = Perf.Enabled ? Perf.Start() : 0L;

            _driverRoot = new GameObject("RagdollKinetics.FutureAnimation");
            _driverRoot.hideFlags = HideFlags.HideAndDontSave;
            Transform root = _driverRoot.transform;
            root.SetPositionAndRotation(_sourceRoot.position,
                _sourceRoot.rotation);
            root.localScale = _sourceRoot.lossyScale;
            _animationTargetsByName[_sourceRoot.name] = root;
            CloneTransformHierarchy(_sourceRoot, root);

            _driverUnity = _driverRoot.AddComponent<Animator>();
            Animator avatarSource = _fastMode
                ? player.PlayerBones.PlayableAnimator.GetComponent<Animator>()
                : _sourceUnity;
            _driverUnity.avatar = avatarSource != null
                ? avatarSource.avatar : null;
            _driverUnity.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _driverUnity.applyRootMotion = false;
            _driverUnity.fireEvents = false;

            if (!_fastMode)
            {
                _driverUnity.runtimeAnimatorController =
                    _sourceUnity.runtimeAnimatorController;
                _driverUnity.enabled = false;
                _ready = true;
                ReportBuilt("UnityAnimator");
                if (Settings.DebugLogging.Value)
                    Plugin.Log.LogInfo("[FutureAnimation] Prepared " + name +
                        " source=UnityAnimator targets=" +
                        _animationTargetsByName.Count +
                        " layers=" + _driverUnity.layerCount);
                return;
            }

            _playable = _driverRoot.AddComponent<PlayableAnimator>();
            InitialLayerInfo[] sourceLayers =
                player.PlayerBones.PlayableAnimator.initialLayerInfo;
            _playable.initialLayerInfo = sourceLayers != null
                ? (InitialLayerInfo[])sourceLayers.Clone()
                : Array.Empty<InitialLayerInfo>();

            bool simplified = player.UsedSimplifiedSkeleton;
            TextAsset controllerAsset = Singleton<IEasyAssets>.Instance
                .GetAsset<TextAsset>(simplified
                    ? InGameBundles.ZOMBIE_FAST_ANIMATOR_CONTROLLER
                    : InGameBundles.PLAYER_FAST_ANIMATOR_CONTROLLER);
            RootMotionBlendTable rootTable = Singleton<IEasyAssets>.Instance
                .GetAsset<RootMotionBlendTable>(simplified
                    ? InGameBundles.ZOMBIE_ROOTMOTION_TABLE
                    : InGameBundles.PLAYER_ROOTMOTION_TABLE);
            CharacterClipsKeeper clips = Singleton<IEasyAssets>.Instance
                .GetAsset<CharacterClipsKeeper>(simplified
                    ? InGameBundles.ZOMBIE_ANIMATION_CLIPS_KEEPER
                    : InGameBundles.PLAYER_ANIMATION_CLIPS_KEEPER);
            rootTable.LoadNodes();
            FastAnimatorController controller =
                FastAnimatorControllerJsonSerializator.Deserialize(
                    controllerAsset.bytes);
            _driver = AnimatorFactory.CreateAnimator(controller,
                rootTable._loadedNodes, root, _playable) as FastAnimatorProcessor;
            _playable.Init(_driver, _driver.GetParametersCache(), rootTable,
                clips, manualUpdate: true);
            _playable.SetCuller(new PlayableAnimatorCuller(_playable));
            for (int i = 0; i < _playable.initialLayerInfo.Length &&
                i < _driver.layerCount; i++)
                _driver.SetLayerWeight(i, _playable.initialLayerInfo[i].weight);
            _ready = true;
            ReportBuilt("FastAnimator");
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo("[FutureAnimation] Prepared " + name +
                    " source=" + _source.GetType().Name + " targets=" +
                    _animationTargetsByName.Count +
                    " layers=" + _driver.layerCount);
        }

        internal void CaptureDeathAnimation()
        {
            if (!_ready) return;
            CaptureWorldAnchor();
            if (!_fastMode)
            {
                CaptureUnityAnimator();
                return;
            }
            if (_source == null) return;
            CopyAnimatorParameters();
            int count = Math.Min(_source.layerCount, _driver.layerCount);
            for (int layer = 0; layer < count; layer++)
                CopyAnimatorLayerState(layer);
            _simulationElapsed = 0f;
            _captureFrame = Time.frameCount;
            _discardFirstPostCaptureUpdate = true;
            _running = true;
            _playable.Process(true, 0f);
            ApplyWorldAnchor(0f);
            AlignMotionRootAtDeath();
            CaptureMotionRootReference();
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo("[FutureAnimation] Captured and running " +
                    name + " layers=" + count + " targets=" +
                    _animationTargetsByName.Count);
        }

        internal bool TryGetAnimationTarget(string boneName,
            out Transform target)
        {
            target = null;
            return Running && boneName != null &&
                _animationTargetsByName.TryGetValue(boneName, out target) &&
                target != null;
        }

        private void LateUpdate()
        {
            if (!_running || !_ready) return;
            if (_physicsDriven) return;
            if (Time.frameCount == _captureFrame) return;
            if (_discardFirstPostCaptureUpdate)
            {
                _discardFirstPostCaptureUpdate = false;
                return;
            }
            float step = Mathf.Max(0f, Time.deltaTime);
            Advance(step);
        }

        internal void UsePhysicsClock()
        {
            _physicsDriven = true;
        }

        internal void StepPhysics(float step)
        {
            if (!_physicsDriven || !_running || !_ready) return;
            Advance(Mathf.Max(0f, step));
        }

        private void Advance(float step)
        {
            _simulationElapsed += step;
            Settings.DeathProfile profile = Profile;
            float maximumLifetime = Mathf.Max(
                profile.WorldFollowDecay.Value,
                Mathf.Max(profile.BoneReplayDecay.Value,
                    profile.MomentumDecay.Value)) + 0.25f;
            if (_simulationElapsed >= maximumLifetime)
            {
                _running = false;
                long started = Perf.Enabled ? Perf.Start() : 0L;
                DestroyDriver();
                if (Perf.Enabled)
                    Perf.Log(string.Format(
                        "corpse {0}: animation copy removed in {1:0.00} ms",
                        name, Perf.Milliseconds(started)));
                return;
            }

            if (!_fastMode)
            {
                _driverUnity.Update(step);
                ApplyWorldAnchor(_simulationElapsed);
                return;
            }

            for (int i = 0; i < _driver.layerCount; i++)
            {
                FastLayerInfo layer = _driver.FastControllerInfo.GetStateInfo(i);
                if (layer.CurrentState != null)
                    layer.CurrentStateAbsoluteTime += step;
            }
            _playable.Process(true, step);
            ApplyWorldAnchor(_simulationElapsed);
        }

        private void CopyAnimatorParameters()
        {
            int count = Math.Min(_source.parameterCount, _driver.parameterCount);
            for (int i = 0; i < count; i++)
                CopyParameter(_source.GetParameter(i));
        }

        private void CopyParameter(AnimatorParameterInfo parameter)
        {
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float:
                    _driver.SetFloat(parameter.nameHash,
                        _source.GetFloat(parameter.nameHash));
                    break;
                case AnimatorControllerParameterType.Int:
                    _driver.SetInteger(parameter.nameHash,
                        _source.GetInteger(parameter.nameHash));
                    break;
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    _driver.SetBool(parameter.nameHash,
                        _source.GetBool(parameter.nameHash));
                    break;
            }
        }

        private void CopyAnimatorLayerState(int layer)
        {
            _driver.SetLayerWeight(layer, _source.GetLayerWeight(layer));
            AnimatorStateInfoWrapper state =
                _source.GetCurrentAnimatorStateInfo(layer);
            float normalizedTime = state.loop
                ? Mathf.Repeat(state.normalizedTime, 1f)
                : Mathf.Clamp01(state.normalizedTime);

            if (!TryGetControllerState(state.fullPathHash,
                out AbstractAnimatorControllerState controllerState)) return;

            FastLayerInfo destination =
                _driver.FastControllerInfo.GetStateInfo(layer);
            destination.CurrentState = controllerState;
            destination.CurrentStateAbsoluteTime =
                controllerState.MotionDuration * normalizedTime;
            destination.Transition = null;
            destination.TransitionAbsTime = 0f;
            destination.DestinationStateAbsoluteTime = 0f;

            PlayableLayerProcessor processor = _playable
                .GetLayerProcessor(layer) as PlayableLayerProcessor;
            if (processor == null) return;

            processor._clipBlender._currentStateNormalizedTime = normalizedTime;
            processor._clipBlender._nextStateNormalizedTime = 0d;
        }

        private bool TryGetControllerState(int stateHash,
            out AbstractAnimatorControllerState controllerState)
        {
            controllerState = null;
            if (!(_driver.GetParametersCache() is
                FastAnimatorProcessor.FastAnimatorCache cache)) return false;
            if (!cache.GetState(stateHash, out AbstractState state)) return false;

            controllerState = state as AbstractAnimatorControllerState;
            return controllerState != null;
        }

        private void CaptureUnityAnimator()
        {
            foreach (AnimatorControllerParameter parameter in
                _sourceUnity.parameters)
                CopyParameter(parameter);
            int count = Math.Min(_sourceUnity.layerCount,
                _driverUnity.layerCount);
            for (int layer = 0; layer < count; layer++)
            {
                _driverUnity.SetLayerWeight(layer,
                    _sourceUnity.GetLayerWeight(layer));
                AnimatorStateInfo state =
                    _sourceUnity.GetCurrentAnimatorStateInfo(layer);
                float normalizedTime = state.loop
                    ? Mathf.Repeat(state.normalizedTime, 1f)
                    : Mathf.Clamp01(state.normalizedTime);
                _driverUnity.Play(state.fullPathHash, layer,
                    normalizedTime);
            }
            _driverUnity.Update(0f);
            _simulationElapsed = 0f;
            _captureFrame = Time.frameCount;
            _discardFirstPostCaptureUpdate = true;
            _running = true;
            ApplyWorldAnchor(0f);
            AlignMotionRootAtDeath();
            CaptureMotionRootReference();
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo("[FutureAnimation] Captured and running " +
                    name + " mode=UnityAnimator layers=" + count +
                    " targets=" + _animationTargetsByName.Count);
        }

        private void CaptureWorldAnchor()
        {
            _deathRootPosition = _sourceRoot != null
                ? _sourceRoot.position : transform.position;
            _deathRootRotation = _sourceRoot != null
                ? _sourceRoot.rotation : transform.rotation;
            _sourceMotionRootDeathPosition = _sourceMotionRoot != null
                ? _sourceMotionRoot.position : _deathRootPosition;
            _deathVelocity = EstimateCachedVelocity();
            _deathVelocity = Vector3.ClampMagnitude(_deathVelocity, 10f);
            _deathMotion = ClassifyDeathMotion(_deathVelocity, _lastPoseLevel);
            if (_driverRoot != null)
                _driverRoot.transform.SetPositionAndRotation(
                    _deathRootPosition, _deathRootRotation);
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[FutureAnimation] Death anchor {0} position={1} rotationY={2:0.0} cachedVelocity={3} pose={4:0.00} profile={5}",
                    name, _deathRootPosition, _deathRootRotation.eulerAngles.y,
                    _deathVelocity, _lastPoseLevel, _deathMotion));
        }

        private void CopyParameter(AnimatorControllerParameter parameter)
        {
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float:
                    _driverUnity.SetFloat(parameter.nameHash,
                        _sourceUnity.GetFloat(parameter.nameHash));
                    break;
                case AnimatorControllerParameterType.Int:
                    _driverUnity.SetInteger(parameter.nameHash,
                        _sourceUnity.GetInteger(parameter.nameHash));
                    break;
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    _driverUnity.SetBool(parameter.nameHash,
                        _sourceUnity.GetBool(parameter.nameHash));
                    break;
            }
        }

        private static Settings.DeathMotion ClassifyDeathMotion(
            Vector3 velocity, float poseLevel)
        {
            if (poseLevel < 0.65f) return Settings.DeathMotion.Crouching;

            float speed = new Vector2(velocity.x, velocity.z).magnitude;
            if (speed < 0.35f) return Settings.DeathMotion.Standing;
            if (speed < 2.5f) return Settings.DeathMotion.Walking;
            return Settings.DeathMotion.Running;
        }

        private Vector3 EstimateCachedVelocity()
        {
            if (_motionSamples.Count < 2) return Vector3.zero;
            float newest = _motionSamples[_motionSamples.Count - 1].Time;
            int first = _motionSamples.Count - 1;
            while (first > 0 && newest - _motionSamples[first - 1].Time <= 0.2f)
                first--;
            if (_motionSamples.Count - first < 2) first = Mathf.Max(0,
                _motionSamples.Count - 2);

            float meanTime = 0f;
            Vector3 meanPosition = Vector3.zero;
            int sampleCount = _motionSamples.Count - first;
            for (int i = first; i < _motionSamples.Count; i++)
            {
                meanTime += _motionSamples[i].Time;
                meanPosition += _motionSamples[i].Position;
            }
            meanTime /= sampleCount;
            meanPosition /= sampleCount;
            float denominator = 0f;
            Vector3 numerator = Vector3.zero;
            for (int i = first; i < _motionSamples.Count; i++)
            {
                float time = _motionSamples[i].Time - meanTime;
                numerator += time * (_motionSamples[i].Position - meanPosition);
                denominator += time * time;
            }
            if (denominator <= 0.000001f) return Vector3.zero;
            Vector3 result = numerator / denominator;
            float horizontalSpeed = new Vector2(result.x, result.z).magnitude;
            result.y = horizontalSpeed >= 0.2f
                ? Mathf.Clamp(result.y,
                    -Mathf.Min(3f, horizontalSpeed),
                    Mathf.Min(3f, horizontalSpeed))
                : 0f;
            result = Vector3.ClampMagnitude(result, 10f);
            if (horizontalSpeed < 0.2f) return Vector3.zero;
            return result;
        }

        private void ApplyWorldAnchor(float elapsed)
        {
            if (_driverRoot == null) return;
            Settings.DeathProfile profile = Profile;
            float duration = Mathf.Max(0.05f,
                profile.MomentumDecay.Value);
            float x = Mathf.Clamp01(Mathf.Max(0f, elapsed) / duration);
            float carriedTime = duration * (x - x * x * x +
                0.5f * x * x * x * x);
            Vector3 position = _deathRootPosition + _deathVelocity *
                profile.MomentumScale.Value * carriedTime;
            if (_hasMotionRootReference && _motionRoot != null)
            {
                Vector3 current = _driverRoot.transform
                    .InverseTransformPoint(_motionRoot.position);
                Vector3 drift = current - _motionRootReference;
                drift.y = 0f;
                position -= _driverRoot.transform.TransformVector(drift);
            }
            _driverRoot.transform.SetPositionAndRotation(position,
                _deathRootRotation);
        }

        private void CaptureMotionRootReference()
        {
            _motionRoot = null;
            if (!_animationTargetsByName.TryGetValue(
                "Base HumanPelvis", out _motionRoot) || _motionRoot == null)
            {
                _hasMotionRootReference = false;
                return;
            }
            _motionRootReference = _driverRoot.transform
                .InverseTransformPoint(_motionRoot.position);
            _hasMotionRootReference = true;
        }

        private void AlignMotionRootAtDeath()
        {
            if (_driverRoot == null ||
                !_animationTargetsByName.TryGetValue(
                    "Base HumanPelvis", out _motionRoot) ||
                _motionRoot == null) return;
            Vector3 correction = _sourceMotionRootDeathPosition -
                _motionRoot.position;
            _driverRoot.transform.position += correction;
            _deathRootPosition = _driverRoot.transform.position;
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[FutureAnimation] Pelvis handoff correction {0} delta={1} magnitude={2:0.000}m",
                    name, correction, correction.magnitude));
        }

        private static Animator FindBodyAnimator(Player player,
            PlayableAnimator sourcePlayable)
        {
            Animator animator = sourcePlayable.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
                return animator;
            Animator[] candidates = player.GetComponentsInChildren<Animator>(true);
            foreach (Animator candidate in candidates)
                if (candidate != null &&
                    candidate.runtimeAnimatorController != null)
                    return candidate;
            return animator;
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDescendant(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private void ReportBuilt(string source)
        {
            if (!Perf.Enabled) return;
            Perf.Log(string.Format(
                "spawn {0}: animation copy built in {1:0.00} ms ({2}, {3} transforms cloned)",
                name, Perf.Milliseconds(_buildStarted), source,
                _animationTargetsByName.Count));
        }

        private void ReportUnavailable(string reason)
        {
            if (_reportedUnavailable || !Settings.DebugLogging.Value) return;
            _reportedUnavailable = true;
            Plugin.Log.LogWarning("[FutureAnimation] Waiting for " + name +
                ": " + reason);
        }

        private void CloneTransformHierarchy(Transform source,
            Transform parent)
        {
            for (int i = 0; i < source.childCount; i++)
            {
                Transform child = source.GetChild(i);
                Transform clone = CloneTransform(child, parent);
                if (!_animationTargetsByName.ContainsKey(child.name))
                    _animationTargetsByName.Add(child.name, clone);
                CloneTransformHierarchy(child, clone);
            }
        }

        private static Transform CloneTransform(Transform source,
            Transform parent)
        {
            GameObject clone = new GameObject(source.name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            Transform transform = clone.transform;
            transform.SetParent(parent, false);
            transform.localPosition = source.localPosition;
            transform.localRotation = source.localRotation;
            transform.localScale = source.localScale;
            return transform;
        }

        private void OnDestroy()
        {
            DestroyDriver();
        }

        private void DestroyDriver()
        {
            _ready = false;
            _running = false;
            if (_driverRoot != null)
            {
                Destroy(_driverRoot);
                _driverRoot = null;
            }
            _animationTargetsByName.Clear();
            _motionSamples.Clear();
            _motionRoot = null;
            _sourceMotionRoot = null;
            _hasMotionRootReference = false;
        }
    }
}
