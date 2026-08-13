using BepInEx.Configuration;

namespace RagdollKinetics
{
    internal static class Settings
    {
        private const string Ragdolls = "Ragdolls";
        private const string Reactions = "Ragdoll Reactions";

        internal static ConfigEntry<bool> Enabled { get; private set; }
        internal static ConfigEntry<bool> DebugLogging { get; private set; }
        internal static ConfigEntry<bool> StabilizeAiAimWhenHit { get; private set; }
        internal static ConfigEntry<bool> SupportAndTone { get; private set; }
        internal static ConfigEntry<bool> StiffLeg { get; private set; }
        internal static ConfigEntry<bool> StiffHip { get; private set; }
        internal static ConfigEntry<bool> StiffArm { get; private set; }
        internal static ConfigEntry<float> BendForce { get; private set; }
        internal static ConfigEntry<float> ImpulseScale { get; private set; }
        internal static ConfigEntry<float> AnimationCarryDuration { get; private set; }
        internal static ConfigEntry<float> AnimationCarryStrength { get; private set; }
        internal static ConfigEntry<float> AnimationSettleDuration { get; private set; }
        internal static ConfigEntry<float> FreezeDelay { get; private set; }
        internal static ConfigEntry<float> ToneReleaseSpread { get; private set; }
        internal static ConfigEntry<float> UnsupportedLegStrength { get; private set; }
        internal static ConfigEntry<float> StiffLegChance { get; private set; }
        internal static ConfigEntry<float> StiffLegStrengthMin { get; private set; }
        internal static ConfigEntry<float> StiffLegStrengthMax { get; private set; }
        internal static ConfigEntry<float> StiffHipChance { get; private set; }
        internal static ConfigEntry<float> StiffHipStrengthMin { get; private set; }
        internal static ConfigEntry<float> StiffHipStrengthMax { get; private set; }
        internal static ConfigEntry<float> StiffArmChance { get; private set; }
        internal static ConfigEntry<float> StiffArmStrengthMin { get; private set; }
        internal static ConfigEntry<float> StiffArmStrengthMax { get; private set; }

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind(Ragdolls, "Enabled", true, "Enable Ragdoll Kinetics.");
            BendForce = Range(config, Ragdolls, "SkeletonBendForce", 25f, "Persistent linked-skeleton stiffness; 0 is loose and 200 is fully stiff.", 0f, 200f);
            AnimationCarryDuration = Range(config, Ragdolls, "AnimationCarryDuration", 0.8f, "Seconds for death-pose motion to decay into physics.", 0.1f, 2f);
            AnimationCarryStrength = Range(config, Ragdolls, "AnimationCarryStrength", 4f, "Strength of animation-to-ragdoll guidance.", 0.5f, 10f);
            AnimationSettleDuration = Range(config, Ragdolls, "AnimationSettleDuration", 0.5f, "Residual damping after animation carry.", 0.1f, 1.5f);
            FreezeDelay = Range(config, Ragdolls, "FreezeDelay", 10f, "Seconds before EFT freezes corpse physics.", 2f, 60f);
            ImpulseScale = Range(config, Ragdolls, "ImpulseScale", 0.35f, "Scale applied to EFT's fatal-shot impulse.", 0f, 1f);
            DebugLogging = config.Bind(Ragdolls, "DebugLogging", false, "Write detailed joint diagnostics to the BepInEx log.");

            StabilizeAiAimWhenHit = config.Bind("AI Hit Response", "StabilizeAimWhenHit", true, "Prevent random aim-direction flicks when an AI is hit by its already visible target.");

            SupportAndTone = config.Bind(Reactions, "SupportAndTone", true, "Stagger muscle release and make leg force depend on support.");
            ToneReleaseSpread = Range(config, Reactions, "ToneReleaseSpread", 0.15f, "Maximum random joint tone-release delay.", 0f, 0.6f);
            UnsupportedLegStrength = Range(config, Reactions, "UnsupportedLegStrength", 100f, "Percent of leg power retained without ground support.", 0f, 100f);
            StiffLeg = config.Bind(Reactions, "StiffLeg", true, "Enable random stiff-leg reactions.");
            StiffLegChance = Percentage(config, "StiffLegChance", 45f, "Reaction chance.");
            StiffLegStrengthMin = Percentage(config, "StiffLegStrengthMin", 0f, "Minimum strength.");
            StiffLegStrengthMax = Percentage(config, "StiffLegStrengthMax", 30f, "Maximum strength.");
            StiffHip = config.Bind(Reactions, "StiffHip", true, "Enable random stiff-hip reactions.");
            StiffHipChance = Percentage(config, "StiffHipChance", 30f, "Reaction chance.");
            StiffHipStrengthMin = Percentage(config, "StiffHipStrengthMin", 0f, "Minimum strength.");
            StiffHipStrengthMax = Percentage(config, "StiffHipStrengthMax", 30f, "Maximum strength.");
            StiffArm = config.Bind(Reactions, "StiffArm", true, "Enable random stiff-arm reactions.");
            StiffArmChance = Percentage(config, "StiffArmChance", 35f, "Reaction chance.");
            StiffArmStrengthMin = Percentage(config, "StiffArmStrengthMin", 0f, "Minimum strength.");
            StiffArmStrengthMax = Percentage(config, "StiffArmStrengthMax", 15f, "Maximum strength.");
        }

        private static ConfigEntry<float> Percentage(ConfigFile config, string key, float value, string description)
        {
            return Range(config, Reactions, key, value, description, 0f, 100f);
        }

        private static ConfigEntry<float> Range(ConfigFile config, string section, string key, float value, string description, float min, float max)
        {
            return config.Bind(section, key, value, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
        }
    }
}
