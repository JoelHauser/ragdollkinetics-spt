using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using EFT;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.LowLevel;

namespace RagdollKinetics
{
    internal sealed class NativeDeathDiagnostics : MonoBehaviour
    {
        private sealed class LoopBoundaryMarker { }

        private sealed class Marker : IDisposable
        {
            internal readonly string Name;
            internal ProfilerRecorder Recorder;

            internal Marker(ProfilerRecorderHandle handle,
                ProfilerRecorderDescription description)
            {
                Name = description.Category + "/" + description.Name;
                Recorder = new ProfilerRecorder(handle, 1,
                    ProfilerRecorderOptions.SumAllSamplesInFrame);
            }

            public void Dispose()
            {
                if (Recorder.Valid) Recorder.Dispose();
            }
        }

        private static NativeDeathDiagnostics _instance;
        private readonly List<Marker> _markers = new List<Marker>();
        private bool _discovered;
        private int _framesRemaining;
        private int _deathFrame;
        private string _profileId;
        private long _lastBoundaryTimestamp;
        private string _lastBoundary = "none";
        private bool _tracingInstalled;

        private void Awake()
        {
            _instance = this;
            if (Settings.NativeDeathDiagnostics != null &&
                Settings.NativeDeathDiagnostics.Value)
                InstallPlayerLoopTracing();
        }

        private void InstallPlayerLoopTracing()
        {
            _tracingInstalled = true;
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (ContainsLoopBoundary(loop))
            {
                return;
            }
            Type[] phases =
            {
                typeof(UnityEngine.PlayerLoop.Initialization),
                typeof(UnityEngine.PlayerLoop.EarlyUpdate),
                typeof(UnityEngine.PlayerLoop.FixedUpdate),
                typeof(UnityEngine.PlayerLoop.PreUpdate),
                typeof(UnityEngine.PlayerLoop.Update),
                typeof(UnityEngine.PlayerLoop.PreLateUpdate),
                typeof(UnityEngine.PlayerLoop.PostLateUpdate)
            };
            foreach (Type phase in phases)
                WrapPhase(ref loop, phase);
            WrapDirectChildren(ref loop,
                typeof(UnityEngine.PlayerLoop.Update), "Update");
            PlayerLoop.SetPlayerLoop(loop);
            Plugin.Log.LogInfo(
                "[RagdollPreview:PlayerLoop] boundary tracing installed");
        }

        private static bool ContainsLoopBoundary(PlayerLoopSystem system)
        {
            if (system.type == typeof(LoopBoundaryMarker)) return true;
            if (system.subSystemList == null) return false;
            foreach (PlayerLoopSystem child in system.subSystemList)
                if (ContainsLoopBoundary(child)) return true;
            return false;
        }

        private static bool RemoveLoopBoundaries(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return false;
            bool changed = false;
            List<PlayerLoopSystem> children = new List<PlayerLoopSystem>();
            foreach (PlayerLoopSystem original in system.subSystemList)
            {
                if (original.type == typeof(LoopBoundaryMarker))
                {
                    changed = true;
                    continue;
                }
                PlayerLoopSystem child = original;
                if (RemoveLoopBoundaries(ref child)) changed = true;
                children.Add(child);
            }
            if (changed) system.subSystemList = children.ToArray();
            return changed;
        }

        private void UninstallPlayerLoopTracing()
        {
            _tracingInstalled = false;
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!RemoveLoopBoundaries(ref loop)) return;
            PlayerLoop.SetPlayerLoop(loop);
            Plugin.Log.LogInfo(
                "[RagdollPreview:PlayerLoop] boundary tracing removed");
        }

        private static bool WrapPhase(ref PlayerLoopSystem system, Type target)
        {
            if (system.type == target)
            {
                List<PlayerLoopSystem> children = system.subSystemList != null
                    ? new List<PlayerLoopSystem>(system.subSystemList)
                    : new List<PlayerLoopSystem>();
                string name = target.Name;
                children.Insert(0, Boundary(name + ".begin"));
                children.Add(Boundary(name + ".end"));
                system.subSystemList = children.ToArray();
                return true;
            }
            if (system.subSystemList == null) return false;
            for (int i = 0; i < system.subSystemList.Length; i++)
            {
                PlayerLoopSystem child = system.subSystemList[i];
                if (!WrapPhase(ref child, target)) continue;
                system.subSystemList[i] = child;
                return true;
            }
            return false;
        }

        private static bool WrapDirectChildren(ref PlayerLoopSystem system,
            Type target, string phaseName)
        {
            if (system.type == target)
            {
                if (system.subSystemList == null) return true;
                List<PlayerLoopSystem> wrapped = new List<PlayerLoopSystem>();
                foreach (PlayerLoopSystem child in system.subSystemList)
                {
                    if (child.type == typeof(LoopBoundaryMarker))
                    {
                        wrapped.Add(child);
                        continue;
                    }
                    string childName = child.type != null
                        ? child.type.FullName : "native-unknown";
                    wrapped.Add(Boundary(phaseName + "." + childName + ".begin"));
                    wrapped.Add(child);
                    wrapped.Add(Boundary(phaseName + "." + childName + ".end"));
                }
                system.subSystemList = wrapped.ToArray();
                return true;
            }
            if (system.subSystemList == null) return false;
            for (int i = 0; i < system.subSystemList.Length; i++)
            {
                PlayerLoopSystem child = system.subSystemList[i];
                if (!WrapDirectChildren(ref child, target, phaseName)) continue;
                system.subSystemList[i] = child;
                return true;
            }
            return false;
        }

        private static PlayerLoopSystem Boundary(string name)
        {
            return new PlayerLoopSystem
            {
                type = typeof(LoopBoundaryMarker),
                updateDelegate = () => RecordBoundary(name)
            };
        }

        private static void RecordBoundary(string boundary)
        {
            NativeDeathDiagnostics instance = _instance;
            if (instance == null || instance._framesRemaining <= 0) return;
            long now = Stopwatch.GetTimestamp();
            double gap = instance._lastBoundaryTimestamp == 0L ? 0d :
                (now - instance._lastBoundaryTimestamp) * 1000d /
                Stopwatch.Frequency;
            Plugin.Log.LogInfo(string.Format(
                "[RagdollPreview:PlayerLoop] deathFrame={0} frame={1} offset={2} from={3} to={4} gap={5:0.000}ms",
                instance._deathFrame, Time.frameCount,
                Time.frameCount - instance._deathFrame,
                instance._lastBoundary, boundary, gap));
            instance._lastBoundaryTimestamp = Stopwatch.GetTimestamp();
            instance._lastBoundary = boundary;
        }

        private void Update()
        {
            bool enabled = Settings.NativeDeathDiagnostics != null &&
                           Settings.NativeDeathDiagnostics.Value;
            if (!enabled)
            {
                if (_framesRemaining > 0)
                {
                    _framesRemaining = 0;
                    foreach (Marker marker in _markers)
                        if (marker.Recorder.Valid && marker.Recorder.IsRunning)
                            marker.Recorder.Stop();
                }
                if (_tracingInstalled) UninstallPlayerLoopTracing();
                return;
            }
            if (!_discovered)
                DiscoverMarkers();
            if (!_tracingInstalled) InstallPlayerLoopTracing();
        }

        private void DiscoverMarkers()
        {
            _discovered = true;
            List<ProfilerRecorderHandle> handles =
                new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            HashSet<string> names = new HashSet<string>();
            foreach (ProfilerRecorderHandle handle in handles)
            {
                try
                {
                    ProfilerRecorderDescription description =
                        ProfilerRecorderHandle.GetDescription(handle);
                    if (description.UnitType !=
                        ProfilerMarkerDataUnit.TimeNanoseconds) continue;
                    string key = description.Category + "/" + description.Name;
                    if (!names.Add(key)) continue;
                    Marker marker = new Marker(handle, description);
                    if (marker.Recorder.Valid) _markers.Add(marker);
                    else marker.Dispose();
                }
                catch (Exception exception)
                {
                    if (Settings.DebugLogging.Value)
                        Plugin.Log.LogWarning(
                            "[RagdollPreview:NativeDeath] marker skipped: " +
                            exception.Message);
                }
            }
            Plugin.Log.LogInfo(string.Format(
                "[RagdollPreview:NativeDeath] discovered handles={0} timeMarkers={1}",
                handles.Count, _markers.Count));
        }

        internal static void ObserveDeath(Player player)
        {
            if (_instance == null || player == null || !player.IsAI ||
                Settings.NativeDeathDiagnostics == null ||
                !Settings.NativeDeathDiagnostics.Value) return;
            if (!_instance._discovered) _instance.DiscoverMarkers();
            _instance.Begin(player);
        }

        private void Begin(Player player)
        {
            _deathFrame = Time.frameCount;
            _framesRemaining = 12;
            _profileId = player.ProfileId;
            _lastBoundaryTimestamp = Stopwatch.GetTimestamp();
            _lastBoundary = "Player.OnDead.prefix";
            foreach (Marker marker in _markers)
            {
                marker.Recorder.Reset();
                marker.Recorder.Start();
            }
            Plugin.Log.LogInfo(string.Format(
                "[RagdollPreview:NativeDeath] begin frame={0} profile={1} recording={2}",
                _deathFrame, _profileId, _markers.Count));
        }

        private void LateUpdate()
        {
            if (_framesRemaining <= 0) return;

            var costly = _markers
                .Select(marker => new
                {
                    marker.Name,
                    Milliseconds = marker.Recorder.LastValue / 1000000d,
                    Samples = marker.Recorder.Count
                })
                .Where(value => value.Milliseconds >= 0.005d)
                .OrderByDescending(value => value.Milliseconds)
                .Take(50)
                .ToArray();
            double recorded = costly.Sum(value => value.Milliseconds);
            string timings = string.Join(" | ", costly.Select(value =>
                string.Format("{0}={1:0.000}ms/{2}", value.Name,
                    value.Milliseconds, value.Samples)).ToArray());

            Plugin.Log.LogInfo(string.Format(
                "[RagdollPreview:NativeDeath:All] deathFrame={0} frame={1} offset={2} delta={3:0.000}ms nonzero={4} top50Sum={5:0.000}ms markers=[{6}]",
                _deathFrame, Time.frameCount, Time.frameCount - _deathFrame,
                Time.unscaledDeltaTime * 1000f, costly.Length, recorded, timings));

            _framesRemaining--;
            if (_framesRemaining != 0) return;
            foreach (Marker marker in _markers)
                marker.Recorder.Stop();
        }

        private void OnDestroy()
        {
            if (_tracingInstalled) UninstallPlayerLoopTracing();
            foreach (Marker marker in _markers) marker.Dispose();
            _markers.Clear();
            if (_instance == this) _instance = null;
        }
    }
}
