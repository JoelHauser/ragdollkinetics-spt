using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RagdollKinetics.Patches
{
    internal sealed class AiHitAimStabilityPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotAimingData), nameof(BotAimingData.GetHit));

        [PatchPrefix]
        private static bool PatchPrefix(BotAimingData __instance,
            DamageInfo damageInfo)
        {
            if (!Settings.StabilizeAiAimWhenHit.Value ||
                __instance == null || damageInfo.Player == null ||
                damageInfo.Player.iPlayer == null)
                return true;

            BotOwner owner = __instance._owner;
            EnemyInfo enemy = owner != null && owner.Memory != null
                ? owner.Memory.GoalEnemy : null;
            if (enemy == null || !enemy.IsVisible || enemy.Person == null)
                return true;

            return enemy.Person.ProfileId !=
                damageInfo.Player.iPlayer.ProfileId;
        }
    }
}
