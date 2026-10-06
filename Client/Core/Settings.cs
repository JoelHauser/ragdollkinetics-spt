using BepInEx.Configuration;
using UnityEngine;

namespace RagdollKinetics
{
    internal static class Settings
    {
        internal enum PreviewMotion
        {
            Standing,
            Crouching,
            Walking,
            Running
        }

        internal enum DeathMotion
        {
            Standing,
            Crouching,
            Walking,
            Running
        }

        internal sealed class DeathProfile
        {
            internal ConfigEntry<float> MomentumScale;
            internal ConfigEntry<float> MomentumDecay;
            internal ConfigEntry<float> WorldFollowStrength;
            internal ConfigEntry<float> WorldFollowDecay;
            internal ConfigEntry<float> BoneReplayStrength;
            internal ConfigEntry<float> BoneReplayDecay;
            internal ConfigEntry<float> BoneReplayMaximumTorque;
            internal ConfigEntry<float> JointLimitRange;
            internal ConfigEntry<float> JointLimitStiffness;
        }

        private const string Ragdolls = "Ragdolls";
        private const string Preview = "Ragdoll Preview (Debug)";
        private const string Collapse = "Collapse";

        internal static ConfigEntry<bool> Enabled { get; private set; }
        internal static ConfigEntry<bool> FutureAnimationDriver { get; private set; }
        internal static DeathProfile StandingDeath { get; private set; }
        internal static DeathProfile CrouchingDeath { get; private set; }
        internal static DeathProfile WalkingDeath { get; private set; }
        internal static DeathProfile RunningDeath { get; private set; }
        internal static ConfigEntry<bool> DebugLogging { get; private set; }
        internal static ConfigEntry<bool> StabilizeAiAimWhenHit { get; private set; }
        internal static ConfigEntry<float> ImpulseScale { get; private set; }
        internal static ConfigEntry<float> FatalPushDecay { get; private set; }
        internal static ConfigEntry<float> RagdollMassScale { get; private set; }
        internal static ConfigEntry<bool> TeleportRagdollOnGlitch { get; private set; }
        internal static ConfigEntry<float> FreezeDelay { get; private set; }
        internal static ConfigEntry<bool> FreezeWhenSettled { get; private set; }
        internal static ConfigEntry<float> LegToneLoss { get; private set; }
        internal static ConfigEntry<float> ArmToneLoss { get; private set; }
        internal static ConfigEntry<float> SpineToneLoss { get; private set; }
        internal static ConfigEntry<float> HeadToneLoss { get; private set; }
        internal static ConfigEntry<bool> HeadshotLightsOut { get; private set; }
        internal static ConfigEntry<float> FallWithShot { get; private set; }
        internal static ConfigEntry<bool> ScaleByBullet { get; private set; }
        internal static ConfigEntry<float> ExplosionPush { get; private set; }
        internal static ConfigEntry<bool> PerformanceLogging { get; private set; }
        internal static ConfigEntry<bool> PreviewEnabled { get; private set; }
        internal static ConfigEntry<PreviewMotion> PreviewMode { get; private set; }
        internal static ConfigEntry<float> PreviewRouteLength { get; private set; }
        internal static ConfigEntry<float> PreviewCorpseTime { get; private set; }
        internal static ConfigEntry<float> PreviewPathTime { get; private set; }
        internal static ConfigEntry<float> PreviewKillTime { get; private set; }
        internal static ConfigEntry<bool> PreviewDiagnostics { get; private set; }
        internal static ConfigEntry<bool> NativeDeathDiagnostics { get; private set; }

        internal static void Bind(ConfigFile config)
        {
            FutureAnimationDriver = Toggle(config, Ragdolls,
                "FutureAnimationDriver", true,
                "Continue EFT's locomotion on an invisible skeleton after death and use it as the only animation-follow target for the corpse.",
                "Future Animation Skeleton", 1200);
            StandingDeath = BindDeathProfile(config, "Death Profile - Standing",
                0f, 0.5f, 0f, 0.05f, 45f, 2f, 900f, 1f, 10f);
            CrouchingDeath = BindDeathProfile(config, "Death Profile - Crouching",
                0.5f, 0.75f, 0f, 0.05f, 50f, 2.5f, 1000f, 1f, 10f);
            WalkingDeath = BindDeathProfile(config, "Death Profile - Walking",
                1f, 4.02f, 115.49f, 0.68f, 100f, 4.04f,
                1200f, 1f, 10f);
            RunningDeath = BindDeathProfile(config, "Death Profile - Running",
                0.55f, 2.73f, 107.04f, 0.83f, 65f, 2.56f,
                1400f, 1f, 10f);
            Enabled = Toggle(config, Ragdolls, "Enabled", true, "Enable Ragdoll Kinetics.", "Enabled", 1220);
            BindSeparator(config, "CorpsePhysicsSeparator", "Corpse Physics", 750);
            FreezeDelay = Range(config, Ragdolls, "FreezeDelay", 15f, "Seconds before EFT freezes corpse physics.", 2f, 60f, "Corpse Freeze Delay", 700);
            FreezeWhenSettled = Toggle(config, Ragdolls,
                "FreezeWhenSettled", true,
                "Once the death animation has played out, let the corpse sleep and freeze as soon as it comes to rest, as EFT does, instead of simulating it for the full freeze delay. Off keeps every corpse awake and shootable until the delay.",
                "Freeze Corpses Once Settled", 695);
            ImpulseScale = Range(config, Ragdolls, "ImpulseScale", 0.5f, "Scale applied to EFT's fatal-shot impulse and cached puppet push.", 0f, 1f, "Fatal Impulse Scale", 690);
            FatalPushDecay = Range(config, Ragdolls, "FatalPushDecay", 0.65f,
                "Seconds for a cached fatal-hit push to lose its velocity.",
                0.05f, 3f, "Fatal Push Decay", 680);
            RagdollMassScale = Range(config, Ragdolls,
                "RagdollMassScale", 1.16f,
                "Multiplier applied to every ragdoll rigidbody mass while preserving EFT's original per-bone mass ratios.",
                0.25f, 4f, "Ragdoll Mass Scale", 670);
            TeleportRagdollOnGlitch = Toggle(config, Ragdolls,
                "TeleportRagdollOnGlitch", true,
                "Return the complete ragdoll to its saved death position if any single bone travels more than 35 metres away.",
                "Teleport Ragdoll back on glitch", 660);
            DebugLogging = Toggle(config, Ragdolls, "DebugLogging", false, "Write detailed joint diagnostics to the BepInEx log.", "Debug Logging", 100);
            PerformanceLogging = Toggle(config, Ragdolls,
                "PerformanceLogging", false,
                "Write one [Perf] line per bot spawn, death and settled corpse with the time the mod spent on it.",
                "Performance Logging", 90);

            Round(FreezeDelay);
            Round(ImpulseScale);
            Round(FatalPushDecay);
            Round(RagdollMassScale);

            LegToneLoss = Range(config, Collapse, "LegToneLoss", 0.15f,
                "Seconds for the legs to lose muscle tone after death. Short values buckle the knees at once instead of leaving the body standing.",
                0.02f, 4f, "Legs Give Way", 500);
            ArmToneLoss = Range(config, Collapse, "ArmToneLoss", 0.3f,
                "Seconds for the arms to lose muscle tone after death.",
                0.02f, 4f, "Arms Go Limp", 490);
            SpineToneLoss = Range(config, Collapse, "SpineToneLoss", 0.5f,
                "Seconds for the spine and pelvis to lose muscle tone after death. A little longer than the legs makes the body fold rather than flop.",
                0.02f, 4f, "Spine Gives Way", 480);
            HeadToneLoss = Range(config, Collapse, "HeadToneLoss", 0.4f,
                "Seconds for the head and neck to lose muscle tone after death.",
                0.02f, 4f, "Head Drops", 470);
            HeadshotLightsOut = Toggle(config, Collapse,
                "HeadshotLightsOut", true,
                "A killing shot to the head or neck makes every region go limp four times faster.",
                "Headshot Lights Out", 460);
            FallWithShot = Range(config, Collapse, "FallWithShot", 1.2f,
                "Sideways speed in m/s given to the upper body along the killing bullet's line, so the body tips away from the shot instead of dropping straight down into a heap. Head and neck hits push 1.5 times harder; a leg hit kicks that leg out instead. Moving bots still fall mostly with their own momentum.",
                0f, 4f, "Fall With The Shot", 450);
            ScaleByBullet = Toggle(config, Collapse, "ScaleByBullet", true,
                "Scale the death push by the killing round's energy at impact (bullet mass and speed; every caliber, modded ones included), with high-penetration rounds that pass straight through giving up to a quarter less. Heavy rounds also make the body go limp faster and kick a struck leg out harder. Roughly: 9x18 x0.5, 9x19 x0.7, 5.45x39 PS x0.95, 7.62x39 PS x1.2, 12-gauge buckshot (close) x1.3, 7.62x51 M80 x1.5, slug x1.5, .338 Lapua x2-3, .50 BMG and 12.7x108 x2.6. Off pushes every round the same.",
                "Bullet Type Matters", 440);
            ExplosionPush = Range(config, Collapse, "ExplosionPush", 3.5f,
                "Push in m/s from a strength-100 grenade at point-blank range, falling off with the square of the distance to nothing at its blast radius; limbs take more than the torso, unevenly, so bodies fold rather than fly. At the default an F-1 knocks a body back about 0.8 m at 1 m, 0.3 m at 2 m and barely at 4 m. Applies to bots killed by the blast and to fresh corpses that are still moving; settled, frozen corpses do not react.",
                0f, 10f, "Explosion Push", 430);
            Round(FallWithShot);
            Round(ExplosionPush);
            Round(LegToneLoss);
            Round(ArmToneLoss);
            Round(SpineToneLoss);
            Round(HeadToneLoss);

            StabilizeAiAimWhenHit = config.Bind("AI Hit Response", "StabilizeAimWhenHit", true, "Prevent random aim-direction flicks when an AI is hit by its already visible target.");

            PreviewEnabled = Toggle(config, Preview, "Enabled", false,
                "Continuously spawn, animate, kill, and clean up one test scav. This works independently of the main Ragdoll Kinetics Enabled setting.",
                "Enable Preview Loop", 1000);
            PreviewMode = config.Bind(Preview, "Motion", PreviewMotion.Standing,
                Description("Animation played immediately before death.", "Motion", 900));
            PreviewRouteLength = Range(config, Preview, "RouteLength", 10f,
                "Length of the preview line in metres.", 4f, 20f,
                "Route Length", 800);
            PreviewCorpseTime = Range(config, Preview, "CorpseTime", 4f,
                "Seconds to display each ragdoll before cleanup and respawn.",
                1f, 15f, "Corpse Display Time", 700);
            PreviewPathTime = Range(config, Preview, "PathTime", 8f,
                "Maximum seconds allowed for the bot to travel the displayed path.",
                1f, 30f, "Path Time", 600);
            PreviewKillTime = Range(config, Preview, "KillTime", 1.21f,
                "Seconds after movement begins before the preview bot is killed.",
                0.25f, 30f, "Kill At Time", 500);
            PreviewDiagnostics = Toggle(config, Preview, "Diagnostics", false,
                "Log the preview bot's commanded path and live locomotion state every half-second, and draw its error from the path in red.",
                "Movement Diagnostics", 400);
            NativeDeathDiagnostics = Toggle(config, Preview,
                "NativeDeathDiagnostics", false,
                "Log Unity native profiler markers for several frames around every AI death. Use this only while diagnosing death-frame stalls.",
                "Native Death Diagnostics", 300);
            Round(PreviewRouteLength);
            Round(PreviewCorpseTime);
            Round(PreviewPathTime);
            Round(PreviewKillTime);
        }

        internal static DeathProfile GetDeathProfile(DeathMotion motion)
        {
            switch (motion)
            {
                case DeathMotion.Crouching: return CrouchingDeath;
                case DeathMotion.Walking: return WalkingDeath;
                case DeathMotion.Running: return RunningDeath;
                default: return StandingDeath;
            }
        }

        private static DeathProfile BindDeathProfile(ConfigFile config,
            string section, float momentumScale, float momentumDecay,
            float worldStrength, float worldDecay, float replayStrength,
            float replayDecay, float maximumTorque, float limitRange,
            float limitStiffness)
        {
            DeathProfile profile = new DeathProfile();
            profile.MomentumScale = Range(config, section, "MomentumScale",
                momentumScale,
                "Fraction of captured horizontal velocity carried into death.",
                0f, 2f, "Momentum Scale", 560);
            profile.MomentumDecay = Range(config, section, "MomentumDecay",
                momentumDecay,
                "Seconds for inherited horizontal momentum to decay to zero.",
                0.05f, 8f, "Momentum Decay", 550);
            profile.WorldFollowStrength = Range(config, section,
                "WorldFollowStrength", worldStrength,
                "Strength carrying each rigidbody along its animated path. Horizontal only; gravity always acts, so the body falls while its momentum carries it.",
                0f, 200f, "World Follow Strength", 600);
            profile.WorldFollowDecay = Range(config, section,
                "WorldFollowDecay", worldDecay,
                "Seconds for animated world-position pulling to decay to zero.",
                0.05f, 8f, "World Follow Decay", 590);
            profile.BoneReplayStrength = Range(config, section,
                "BoneReplayStrength", replayStrength,
                "Relative joint strength used to replay the walk/run pose.",
                0f, 200f, "Bone Replay Strength", 580);
            profile.BoneReplayDecay = Range(config, section,
                "BoneReplayDecay", replayDecay,
                "Longest time any joint keeps replaying the pose. Each body region also has its own, usually shorter, time under Collapse.",
                0.05f, 8f, "Bone Replay Decay", 570);
            profile.BoneReplayMaximumTorque = Range(config, section,
                "BoneReplayMaximumTorque", maximumTorque,
                "Maximum animation-drive torque per joint. Lower values let collisions bend the pose more easily.",
                0f, 5000f, "Bone Replay Maximum Torque", 565);
            profile.JointLimitRange = Range(config, section,
                "JointLimitRange", limitRange,
                "Multiplier for anatomical joint bend ranges.",
                0.5f, 2f, "Joint Limit Range", 540);
            profile.JointLimitStiffness = Range(config, section,
                "JointLimitStiffness", limitStiffness,
                "Resistance applied only when a joint reaches its bend limit.",
                0f, 100f, "Joint Limit Stiffness", 530);
            Round(profile.MomentumScale);
            Round(profile.MomentumDecay);
            Round(profile.WorldFollowStrength);
            Round(profile.WorldFollowDecay);
            Round(profile.BoneReplayStrength);
            Round(profile.BoneReplayDecay);
            Round(profile.BoneReplayMaximumTorque);
            Round(profile.JointLimitRange);
            Round(profile.JointLimitStiffness);
            return profile;
        }

        private static void Round(ConfigEntry<float> entry)
        {
            entry.Value = Mathf.Round(entry.Value * 100f) / 100f;
        }

        private static void BindSeparator(ConfigFile config, string key,
            string label, int order)
        {
            ConfigurationManagerAttributes attributes =
                new ConfigurationManagerAttributes
                {
                    Category = Ragdolls,
                    DispName = label,
                    Order = order,
                    HideDefaultButton = true,
                    HideSettingName = true,
                    CustomDrawer = ignored =>
                    {
                        GUILayout.Space(10f);
                        GUILayout.Label(label);
                        GUILayout.Space(3f);
                    }
                };
            config.Bind(Ragdolls, key, false,
                new ConfigDescription(label, null, attributes));
        }

        private static ConfigEntry<bool> Toggle(ConfigFile config, string section,
            string key, bool value, string description, string displayName, int order)
        {
            return config.Bind(section, key, value, Description(description,
                displayName, order));
        }

        private static ConfigEntry<float> Range(ConfigFile config, string section, string key, float value, string description, float min, float max, string displayName = null, int order = 0)
        {
            return config.Bind(section, key, value, Description(description,
                displayName ?? key, order, new AcceptableValueRange<float>(min, max)));
        }

        private static ConfigDescription Description(string description,
            string displayName, int order, AcceptableValueBase acceptable = null)
        {
            return new ConfigDescription(description, acceptable,
                new ConfigurationManagerAttributes
                { DispName = displayName, Order = order });
        }
    }
}
