using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RagdollKinetics.Patches
{
    internal static class RagdollPatches
    {
        internal static List<ModulePatch> CreateAll() =>
            new List<ModulePatch>
            {
                new CaptureAnimatedPosePatch(),
                new CaptureDeathAnimationPatch(),
                new AttachRagdollControllerPatch(),
                new SuppressDropSoundPatch(),
                new ScaleFatalImpulsePatch(),
                new KeepRagdollReactivePatch(),
                new DelayRagdollFreezePatch(),
                new AiHitAimStabilityPatch(),
                new PreviewBotManualUpdatePatch(),
                new PreviewBotFixedUpdatePatch()
            };

        private abstract class RagdollPatch : ModulePatch
        {
            protected static MethodBase Target<T>(string methodName) =>
                AccessTools.Method(typeof(T), methodName);
        }

        private sealed class CaptureAnimatedPosePatch : RagdollPatch
        {
            private static readonly HashSet<int> FailedPlayers =
                new HashSet<int>();

            protected override MethodBase GetTargetMethod() =>
                Target<Player>(nameof(Player.BodyUpdate));

            [PatchPostfix]
            private static void PatchPostfix(Player __instance, float deltaTime)
        {
            if (!Settings.Enabled.Value || __instance == null ||
                !__instance.IsAI ||
                __instance.ActiveHealthController == null ||
                !__instance.ActiveHealthController.IsAlive) return;

            if (Settings.FutureAnimationDriver.Value &&
                !FailedPlayers.Contains(__instance.GetInstanceID()))
            {
                FutureAnimationDriver future =
                    __instance.GetComponent<FutureAnimationDriver>();
                if (future == null)
                    future = __instance.gameObject
                        .AddComponent<FutureAnimationDriver>();
                try { future.PrepareAnimationDriver(__instance); }
                catch (System.Exception exception)
                {
                    Plugin.Log.LogError("[FutureAnimation] Prepare failed " +
                        "for " + __instance.name + "; it will die with a " +
                        "plain ragdoll: " + exception);
                    FailedPlayers.Add(__instance.GetInstanceID());
                    Object.Destroy(future);
                    future = null;
                }
                if (future != null) future.CaptureLivingMotion();
            }
        }

        }

        private sealed class CaptureDeathAnimationPatch : RagdollPatch
        {
            protected override MethodBase GetTargetMethod() =>
                Target<Player>(nameof(Player.OnDead));

            [PatchPrefix]
            private static void PatchPrefix(Player __instance)
        {
            NativeDeathDiagnostics.ObserveDeath(__instance);
            if (!Settings.Enabled.Value || __instance == null ||
                !__instance.IsAI) return;
            if (Settings.FutureAnimationDriver.Value)
            {
                try
                {
                    long started = Perf.Enabled ? Perf.Start() : 0L;
                    FutureAnimationDriver future =
                        __instance.GetComponent<FutureAnimationDriver>();
                    if (future != null) future.CaptureDeathAnimation();
                    if (Perf.Enabled)
                        Perf.Log(string.Format(
                            "death {0}: animation capture {1:0.00} ms{2}",
                            __instance.name, Perf.Milliseconds(started),
                            future != null && future.Running ? ""
                                : " (no animation copy, plain ragdoll)"));
                }
                catch (System.Exception exception)
                {
                    Plugin.Log.LogError("[FutureAnimation] Capture failed: " +
                        exception);
                }
            }
        }

        }

        private sealed class AttachRagdollControllerPatch : RagdollPatch
        {
            protected override MethodBase GetTargetMethod() =>
                Target<Corpse>(nameof(Corpse.TryToCreateRagdoll));

            [PatchPostfix]
            private static void PatchPostfix(Corpse __instance)
        {
            if (!Settings.Enabled.Value || __instance == null ||
                __instance.Ragdoll == null ||
                __instance.GetComponent<RagdollSkeleton>() != null) return;

            FutureAnimationDriver future = Settings.FutureAnimationDriver.Value
                ? __instance.GetComponent<FutureAnimationDriver>() : null;
            long started = Perf.Enabled ? Perf.Start() : 0L;
            RagdollSkeleton controller =
                __instance.gameObject.AddComponent<RagdollSkeleton>();
            try
            {
                controller.InitializeRagdoll(__instance.Ragdoll, future);
                if (Perf.Enabled)
                    Perf.Log(string.Format(
                        "death {0}: ragdoll setup {1:0.00} ms",
                        __instance.name, Perf.Milliseconds(started)));
            }
            catch (System.Exception exception)
            {
                Plugin.Log.LogError(exception);
                Object.Destroy(controller);
            }
        }

        }

        private sealed class SuppressDropSoundPatch : RagdollPatch
        {
            protected override MethodBase GetTargetMethod() =>
                Target<Corpse>(nameof(Corpse.PlayCorpseDropSound));

            [PatchPrefix]
            private static bool PatchPrefix(Corpse __instance)
        {
            if (!Settings.Enabled.Value ||
                !Settings.FutureAnimationDriver.Value || __instance == null)
                return true;
            FutureAnimationDriver driver =
                __instance.GetComponent<FutureAnimationDriver>();
            return driver == null || !driver.Running;
        }

        }

        private sealed class ScaleFatalImpulsePatch : RagdollPatch
        {
            protected override MethodBase GetTargetMethod() =>
                AccessTools.Method(typeof(CorpseRagdoll),
                    nameof(CorpseRagdoll.ApplyImpulse), new[]
                    {
                        typeof(Rigidbody), typeof(Vector3), typeof(Vector3),
                        typeof(float)
                    });

            [PatchPrefix]
            private static bool PatchPrefix(CorpseRagdoll __instance,
            Rigidbody rigidbody, Vector3 direction, Vector3 point,
            ref float thrust)
        {
            PreviewDeathMarker previewMarker = rigidbody != null
                ? rigidbody.GetComponentInParent<PreviewDeathMarker>() : null;
            if (previewMarker != null && previewMarker.SuppressImpulse)
                return false;
            if (!Settings.Enabled.Value) return true;
            RagdollSkeleton skeleton = rigidbody != null
                ? rigidbody.GetComponentInParent<RagdollSkeleton>() : null;
            if (skeleton != null && skeleton.CaptureFatalImpulse(rigidbody,
                direction, point, thrust))
                return false;
            thrust *= Settings.ImpulseScale.Value;
            return true;
        }

        }

        private sealed class KeepRagdollReactivePatch : RagdollPatch
        {
            protected override MethodBase GetTargetMethod() =>
                Target<PlayerRigidbodySleepHierarchy>(
                    nameof(PlayerRigidbodySleepHierarchy.TryPutToSleep));

            [PatchPrefix]
            private static bool PatchPrefix(
            PlayerRigidbodySleepHierarchy __instance, ref bool __result)
        {
            if (!Settings.Enabled.Value) return true;
            RigidbodySpawner spawner = __instance != null
                ? __instance.RigidbodySpawner : null;
            RagdollSkeleton skeleton = spawner != null
                ? spawner.GetComponentInParent<RagdollSkeleton>() : null;
            if (skeleton == null || skeleton.AllowSleep) return true;

            __result = false;
            return false;
        }

        }

        private sealed class DelayRagdollFreezePatch : RagdollPatch
        {
            protected override MethodBase GetTargetMethod() =>
                Target<Corpse>(nameof(Corpse.CheckCorpseIsStill));

            [PatchPostfix]
            private static void PatchPostfix(Corpse __instance, bool __0,
            ref bool __result)
        {
            if (!Settings.Enabled.Value || __instance == null) return;
            RagdollSkeleton skeleton =
                __instance.GetComponent<RagdollSkeleton>();
            if (skeleton != null) __result = skeleton.ShouldFreeze(__0);
        }
        }
    }
}
