using EFT;
using EFT.Ballistics;
using HarmonyLib;

namespace RagdollKinetics.Patches
{
    [HarmonyPatch(typeof(BotAimingData), nameof(BotAimingData.GetHit))]
    internal static class AiHitAimStabilityPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(BotAimingData __instance,
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
