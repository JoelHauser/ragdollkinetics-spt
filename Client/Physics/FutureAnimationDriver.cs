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
    // A renderer-free copy of EFT's body skeleton. It is prepared while the
    // player is alive, captures the live fast-animator state at death, then
    // advances only its private playable graph. No Player/AI components or
    // animation-event consumers are cloned.
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

        private readonly Dictionary<string, Transform> _targets =
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

        internal bool Running => _running && _ready;
        internal Vector3 TravelVelocity => _deathVelocity;
        internal Settings.DeathMotion DeathMotion => _deathMotion;
        internal Settings.DeathProfile Profile =>
            Settings.GetDeathProfile(_deathMotion);

        internal void SampleLivingMotion()
        {
            if (_sourceRoot == null || _running) return;
            if (_player != null)
            {
                _lastPoseLevel = _player.PoseLevel;
            }
            float now = Time.time;
            Vector3 position = _sourceRoot.position;
            if (_motionSamples.Count > 0)
            {
                MotionSample previous =
                    _motionSamples[_motionSamples.Count - 1];
                float dt = now - previous.Time;
                // Pool teleports and preview route placement are boundaries,
                // not locomotion. Start a fresh history at the new position.
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

        internal void Prepare(Player player)
        {
            if (_ready)
            {
                // Preview corpses can be returned to EFT's Player pool before
                // the long follow test expires. The same component then wakes
                // up as a new bot while still owning the previous corpse's
                // graph and root. A live owner while Running is an unambiguous
                // pool-reuse boundary, so rebuild everything for this life.
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

            _driverRoot = new GameObject("RagdollKinetics.FutureAnimation");
            _driverRoot.hideFlags = HideFlags.HideAndDontSave;
            Transform root = _driverRoot.transform;
            root.SetPositionAndRotation(_sourceRoot.position,
                _sourceRoot.rotation);
            root.localScale = _sourceRoot.lossyScale;
            _targets[_sourceRoot.name] = root;
            CloneChildren(_sourceRoot, root);

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
                if (Settings.DebugLogging.Value)
                    Plugin.Log.LogInfo("[FutureAnimation] Prepared " + name +
                        " source=UnityAnimator targets=" + _targets.Count +
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
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo("[FutureAnimation] Prepared " + name +
                    " source=" + _source.GetType().Name + " targets=" +
                    _targets.Count + " layers=" + _driver.layerCount);
        }

        internal void CaptureAndRun()
        {
            if (!_ready) return;
            CaptureWorldAnchor();
            if (!_fastMode)
            {
                CaptureUnityAnimator();
                return;
            }
            if (_source == null) return;
            CopyParameters();
            int count = Math.Min(_source.layerCount, _driver.layerCount);
            for (int layer = 0; layer < count; layer++)
            {
                _driver.SetLayerWeight(layer, _source.GetLayerWeight(layer));
                AnimatorStateInfoWrapper state =
                    _source.GetCurrentAnimatorStateInfo(layer);
                float normalizedTime = state.loop
                    ? Mathf.Repeat(state.normalizedTime, 1f)
                    : Mathf.Clamp01(state.normalizedTime);
                FastLayerInfo destination =
                    _driver.FastControllerInfo.GetStateInfo(layer);
                if (_driver.GetParametersCache() is
                    FastAnimatorProcessor.FastAnimatorCache cache &&
                    cache.GetState(state.fullPathHash, out AbstractState mapped) &&
                    mapped is AbstractAnimatorControllerState controllerState)
                {
                    destination.CurrentState = controllerState;
                    destination.CurrentStateAbsoluteTime =
                        controllerState.MotionDuration * normalizedTime;
                    destination.Transition = null;
                    destination.TransitionAbsTime = 0f;
                    destination.DestinationStateAbsoluteTime = 0f;
                    PlayableLayerProcessor processor = _playable
                        .GetLayerProcessor(layer) as PlayableLayerProcessor;
                    if (processor != null)
                    {
                        processor._clipBlender._currentStateNormalizedTime =
                            normalizedTime;
                        processor._clipBlender._nextStateNormalizedTime = 0d;
                    }
                }
            }
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
                    name + " layers=" + count + " targets=" + _targets.Count);
        }

        internal bool TryGetTarget(string boneName, out Transform target)
        {
            target = null;
            return Running && boneName != null &&
                _targets.TryGetValue(boneName, out target) && target != null;
        }

        private void LateUpdate()
        {
            if (!_running || !_ready) return;
            if (_physicsDriven) return;
            // Synchronous death work can make Unity's next deltaTime contain a
            // large interval in which this graph did not actually run.
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
                DestroyDriver();
                return;
            }

            if (!_fastMode)
            {
                _driverUnity.Update(step);
                ApplyWorldAnchor(_simulationElapsed);
                return;
            }

            // Deliberately advance only state time. FastAnimatorProcessor.Update
            // also executes EFT state behaviours and animation events.
            for (int i = 0; i < _driver.layerCount; i++)
            {
                FastLayerInfo layer = _driver.FastControllerInfo.GetStateInfo(i);
                if (layer.CurrentState != null)
                    layer.CurrentStateAbsoluteTime += step;
            }
            _playable.Process(true, step);
            ApplyWorldAnchor(_simulationElapsed);
        }

        private void CopyParameters()
        {
            int count = Math.Min(_source.parameterCount, _driver.parameterCount);
            for (int i = 0; i < count; i++)
            {
                AnimatorParameterInfo parameter = _source.GetParameter(i);
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
        }

        private void CaptureUnityAnimator()
        {
            foreach (AnimatorControllerParameter parameter in
                _sourceUnity.parameters)
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
                    " targets=" + _targets.Count);
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
            _deathVelocity.y = 0f;
            _deathVelocity = Vector3.ClampMagnitude(_deathVelocity, 10f);
            float speed = _deathVelocity.magnitude;
            _deathMotion = _lastPoseLevel < 0.65f
                ? Settings.DeathMotion.Crouching
                : speed < 0.35f
                    ? Settings.DeathMotion.Standing
                    : speed < 2.5f
                        ? Settings.DeathMotion.Walking
                        : Settings.DeathMotion.Running;
            if (_driverRoot != null)
                _driverRoot.transform.SetPositionAndRotation(
                    _deathRootPosition, _deathRootRotation);
            if (Settings.DebugLogging.Value)
                Plugin.Log.LogInfo(string.Format(
                    "[FutureAnimation] Death anchor {0} position={1} rotationY={2:0.0} cachedVelocity={3} pose={4:0.00} profile={5}",
                    name, _deathRootPosition, _deathRootRotation.eulerAngles.y,
                    _deathVelocity, _lastPoseLevel, _deathMotion));
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

            // Least-squares slope over the final 0.2 seconds rejects animation
            // jitter and is not vulnerable to one zeroed death-frame velocity.
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
            result.y = 0f;
            result = Vector3.ClampMagnitude(result, 10f);
            // Prevent animation noise/controller settling from dragging a
            // standing corpse across the ground.
            if (result.magnitude < 0.2f) return Vector3.zero;
            return result;
        }

        private void ApplyWorldAnchor(float elapsed)
        {
            if (_driverRoot == null) return;
            Settings.DeathProfile profile = Profile;
            float duration = Mathf.Max(0.05f,
                profile.MomentumDecay.Value);
            float x = Mathf.Clamp01(Mathf.Max(0f, elapsed) / duration);
            // Same integral of the corpse's smooth momentum decay. The cloned
            // pelvis and physical pelvis now travel along one world path.
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
                // Locomotion clips contain horizontal root-bone travel which
                // resets every loop. Remove it because world travel is already
                // supplied by the captured Player velocity.
                position -= _driverRoot.transform.TransformVector(drift);
            }
            _driverRoot.transform.SetPositionAndRotation(position,
                _deathRootRotation);
        }

        private void CaptureMotionRootReference()
        {
            _motionRoot = null;
            if (!_targets.TryGetValue("Base HumanPelvis", out _motionRoot) ||
                _motionRoot == null)
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
                !_targets.TryGetValue("Base HumanPelvis", out _motionRoot) ||
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

        private void ReportUnavailable(string reason)
        {
            if (_reportedUnavailable || !Settings.DebugLogging.Value) return;
            _reportedUnavailable = true;
            Plugin.Log.LogWarning("[FutureAnimation] Waiting for " + name +
                ": " + reason);
        }

        private void CloneChildren(Transform source, Transform parent)
        {
            for (int i = 0; i < source.childCount; i++)
            {
                Transform child = source.GetChild(i);
                GameObject clone = new GameObject(child.name);
                clone.hideFlags = HideFlags.HideAndDontSave;
                Transform transform = clone.transform;
                transform.SetParent(parent, false);
                transform.localPosition = child.localPosition;
                transform.localRotation = child.localRotation;
                transform.localScale = child.localScale;
                if (!_targets.ContainsKey(child.name))
                    _targets.Add(child.name, transform);
                CloneChildren(child, transform);
            }
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
            _targets.Clear();
            _motionSamples.Clear();
            _motionRoot = null;
            _sourceMotionRoot = null;
            _hasMotionRootReference = false;
        }
    }
}
