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
        // Sample after EFT updates the body animation.
        [HarmonyPatch(typeof(Player), nameof(Player.BodyUpdate))]
        [HarmonyPostfix]
        private static void SampleAnimatedPose(Player __instance, float deltaTime)
        {
            if (!Settings.Enabled.Value || __instance == null ||
                !__instance.IsAI ||
                __instance.ActiveHealthController == null ||
                !__instance.ActiveHealthController.IsAlive) return;

            RagdollPoseSampler sampler =
                __instance.GetComponent<RagdollPoseSampler>();
            if (sampler == null)
                sampler = __instance.gameObject.AddComponent<RagdollPoseSampler>();
            sampler.Sample(deltaTime);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnDead))]
        [HarmonyPrefix]
        private static void FreezeAnimatedPose(Player __instance)
        {
            if (!Settings.Enabled.Value || __instance == null ||
                !__instance.IsAI) return;
            __instance.GetComponent<RagdollPoseSampler>()?.Freeze();
        }

        [HarmonyPatch(typeof(Corpse), nameof(Corpse.TryToCreateRagdoll))]
        [HarmonyPostfix]
        private static void AttachController(Corpse __instance)
        {
            if (!Settings.Enabled.Value || __instance == null ||
                __instance.Ragdoll == null ||
                __instance.GetComponent<RagdollSkeleton>() != null) return;

            RagdollPoseSampler sampler =
                __instance.GetComponent<RagdollPoseSampler>();
            RagdollSkeleton controller =
                __instance.gameObject.AddComponent<RagdollSkeleton>();
            try
            {
                controller.Initialize(__instance.Ragdoll, sampler);
            }
            catch (System.Exception exception)
            {
                Plugin.Log.LogError(exception);
                Object.Destroy(controller);
            }
            finally
            {
                if (sampler != null) Object.Destroy(sampler);
            }
        }

        [HarmonyPatch(typeof(CorpseRagdoll), nameof(CorpseRagdoll.ApplyImpulse),
            new System.Type[] { typeof(Rigidbody), typeof(Vector3), typeof(Vector3),
                typeof(float) })]
        [HarmonyPrefix]
        private static void ScaleFatalImpulse(ref float thrust)
        {
            if (Settings.Enabled.Value)
                thrust *= Settings.ImpulseScale.Value;
        }

        // Allow natural sleep until the configured freeze delay expires.
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
