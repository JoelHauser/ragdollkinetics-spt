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
                new PreviewBotFixedUpdatePatch(),
                new RecordShotPatch(),
                new RecordBlastPatch()
            };

        private abstract class RagdollPatch : ModulePatch
        {
            protected static MethodBase Target<T>(string methodName) =>
                AccessTools.Method(typeof(T), methodName);

            protected static System.Func<object, object> Getter(
                System.Type type, string name)
            {
                FieldInfo field = AccessTools.Field(type, name);
                if (field != null) return field.GetValue;
                PropertyInfo property = AccessTools.Property(type, name);
                if (property != null && property.CanRead)
                    return instance => property.GetValue(instance, null);
                Plugin.Log.LogWarning("[Impacts] " + type.Name + "." + name +
                    " not found; bullet type will count for less.");
                return null;
            }
        }

        // Every bullet that hits a player passes through GameWorld.ShotDelegate with
        // its mass and impact velocity; remember them on the player it hit.
        private sealed class RecordShotPatch : RagdollPatch
        {
            private static System.Func<object, object> _hitCollider, _mass,
                _velocity, _initialSpeed, _penetration, _fireIndex;
            private static bool _failed;

            protected override MethodBase GetTargetMethod()
            {
                MethodInfo method = AccessTools.Method(typeof(GameWorld),
                    "ShotDelegate");
                System.Type shot = method.GetParameters()[0].ParameterType;
                _hitCollider = Getter(shot, "HitCollider");
                _mass = Getter(shot, "BulletMassGram");
                _velocity = Getter(shot, "CurrentVelocity");
                _initialSpeed = Getter(shot, "InitialSpeed");
                _penetration = Getter(shot, "PenetrationPower");
                _fireIndex = Getter(shot, "FireIndex");
                return method;
            }

            [PatchPrefix]
            private static void PatchPrefix(object __0)
            {
                if (_failed || !Settings.Enabled.Value || __0 == null ||
                    _hitCollider == null || _mass == null) return;
                try
                {
                    Collider hit = _hitCollider(__0) as Collider;
                    Player target = hit != null
                        ? hit.GetComponentInParent<Player>() : null;
                    if (target == null) return;
                    float speed = _velocity != null
                        ? ((Vector3)_velocity(__0)).magnitude : 0f;
                    if (speed <= 0f && _initialSpeed != null)
                        speed = (float)_initialSpeed(__0);
                    float mass = (float)_mass(__0) / 1000f;
                    Impacts.RecordShot(target, 0.5f * mass * speed * speed,
                        _penetration != null ? (float)_penetration(__0) : 0f,
                        _fireIndex != null ? (int)_fireIndex(__0) : 0);
                }
                catch (System.Exception exception)
                {
                    _failed = true;
                    Plugin.Log.LogError("[Impacts] Recording shots failed; " +
                        "every round now pushes the same: " + exception);
                }
            }
        }

        // Every explosion (thrown grenades, launcher rounds, mines) goes through one
        // static Explosion(IExplosiveItem, Vector3, ...) on an obfuscated class.
        private sealed class RecordBlastPatch : RagdollPatch
        {
            private static bool _failed;

            protected override MethodBase GetTargetMethod()
            {
                MethodBase shared = FindSharedExplosion();
                if (shared != null) return shared;
                Plugin.Log.LogWarning("[Impacts] Shared explosion method not " +
                    "found; only thrown grenades and launcher rounds will " +
                    "push ragdolls.");
                return AccessTools.Method(typeof(Grenade), "Explosion");
            }

            private static MethodBase FindSharedExplosion()
            {
                System.Type[] types;
                try { types = typeof(IExplosiveItem).Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                foreach (System.Type type in types)
                {
                    if (type == null || !type.IsAbstract || !type.IsSealed)
                        continue;
                    foreach (MethodInfo method in type.GetMethods(
                        BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name != "Explosion") continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length >= 2 &&
                            parameters[0].ParameterType == typeof(IExplosiveItem) &&
                            parameters[1].ParameterType == typeof(Vector3))
                            return method;
                    }
                }
                return null;
            }

            [PatchPostfix]
            private static void PatchPostfix(object[] __args)
            {
                if (_failed || !Settings.Enabled.Value || __args == null) return;
                try
                {
                    IExplosiveItem item = null;
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (item == null)
                        {
                            item = __args[i] as IExplosiveItem;
                            continue;
                        }
                        if (!(__args[i] is Vector3 position)) continue;
                        if (!item.IsDummy && item.MaxExplosionDistance > 0f)
                            Impacts.RecordBlast(position,
                                item.MaxExplosionDistance, item.GetStrength);
                        return;
                    }
                }
                catch (System.Exception exception)
                {
                    _failed = true;
                    Plugin.Log.LogError("[Impacts] Recording explosions " +
                        "failed; ragdolls will ignore them: " + exception);
                }
            }
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
            if (skeleton != null)
                skeleton.CaptureFatalImpulse(rigidbody, direction, point,
                    thrust);
            thrust *= Settings.ImpulseScale.Value *
                (skeleton != null ? skeleton.ShotEnergyFactor : 1f);
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
