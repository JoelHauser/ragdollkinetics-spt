using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace RagdollKinetics.Patches
{
    [HarmonyPatch]
    internal static class RagdollPatches
    {
        [HarmonyPatch(typeof(Player), nameof(Player.BodyUpdate))]
        [HarmonyPostfix]
        private static void SampleAnimatedPose(Player __instance, float deltaTime)
        {
            if (!Settings.Enabled.Value || __instance == null ||
                !__instance.IsAI ||
                __instance.ActiveHealthController == null ||
                !__instance.ActiveHealthController.IsAlive) return;

            if (Settings.FutureAnimationDriver.Value)
            {
                FutureAnimationDriver future =
                    __instance.GetComponent<FutureAnimationDriver>();
                if (future == null)
                    future = __instance.gameObject
                        .AddComponent<FutureAnimationDriver>();
                try { future.Prepare(__instance); }
                catch (System.Exception exception)
                {
                    Plugin.Log.LogError("[FutureAnimation] Prepare failed: " +
                        exception);
                    Object.Destroy(future);
                    future = null;
                }
                if (future != null) future.SampleLivingMotion();
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnDead))]
        [HarmonyPrefix]
        private static void FreezeAnimatedPose(Player __instance)
        {
            NativeDeathDiagnostics.ObserveDeath(__instance);
            if (!Settings.Enabled.Value || __instance == null ||
                !__instance.IsAI) return;
            if (Settings.FutureAnimationDriver.Value)
            {
                try
                {
                    __instance.GetComponent<FutureAnimationDriver>()
                        ?.CaptureAndRun();
                }
                catch (System.Exception exception)
                {
                    Plugin.Log.LogError("[FutureAnimation] Capture failed: " +
                        exception);
                }
            }
        }

        [HarmonyPatch(typeof(Corpse), nameof(Corpse.TryToCreateRagdoll))]
        [HarmonyPostfix]
        private static void AttachController(Corpse __instance)
        {
            if (!Settings.Enabled.Value || __instance == null ||
                __instance.Ragdoll == null ||
                __instance.GetComponent<RagdollSkeleton>() != null) return;

            FutureAnimationDriver future = Settings.FutureAnimationDriver.Value
                ? __instance.GetComponent<FutureAnimationDriver>() : null;
            RagdollSkeleton controller =
                __instance.gameObject.AddComponent<RagdollSkeleton>();
            try
            {
                controller.Initialize(__instance.Ragdoll, future);
            }
            catch (System.Exception exception)
            {
                Plugin.Log.LogError(exception);
                Object.Destroy(controller);
            }
        }

        [HarmonyPatch(typeof(Corpse), nameof(Corpse.PlayCorpseDropSound))]
        [HarmonyPrefix]
        private static bool SuppressDropSoundDuringAnimation(Corpse __instance)
        {
            if (!Settings.Enabled.Value ||
                !Settings.FutureAnimationDriver.Value || __instance == null)
                return true;
            FutureAnimationDriver driver =
                __instance.GetComponent<FutureAnimationDriver>();
            return driver == null || !driver.Running;
        }

        [HarmonyPatch(typeof(CorpseRagdoll), nameof(CorpseRagdoll.ApplyImpulse),
            new System.Type[] { typeof(Rigidbody), typeof(Vector3), typeof(Vector3),
                typeof(float) })]
        [HarmonyPrefix]
        private static bool ScaleFatalImpulse(CorpseRagdoll __instance,
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

        [HarmonyPatch(typeof(PlayerRigidbodySleepHierarchy),
            nameof(PlayerRigidbodySleepHierarchy.TryPutToSleep))]
        [HarmonyPrefix]
        private static bool KeepRagdollReactive(
            PlayerRigidbodySleepHierarchy __instance, ref bool __result)
        {
            if (!Settings.Enabled.Value) return true;
            RigidbodySpawner spawner = __instance != null
                ? __instance.RigidbodySpawner : null;
            RagdollSkeleton skeleton = spawner != null
                ? spawner.GetComponentInParent<RagdollSkeleton>() : null;
            if (skeleton == null || skeleton.AllowFreeze) return true;

            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(Corpse), nameof(Corpse.CheckCorpseIsStill))]
        [HarmonyPostfix]
        private static void DelayRagdollFreeze(Corpse __instance,
            ref bool __result)
        {
            if (!Settings.Enabled.Value || __instance == null) return;
            RagdollSkeleton skeleton =
                __instance.GetComponent<RagdollSkeleton>();
            if (skeleton != null) __result = skeleton.AllowFreeze;
        }
    }
}
