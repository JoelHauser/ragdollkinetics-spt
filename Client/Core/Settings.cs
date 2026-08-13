using BepInEx.Configuration;
using UnityEngine;

namespace RagdollKinetics
{
    internal static class Settings
    {
        internal enum BendDecayCurve
        {
            Linear,
            SlowStart,
            SlowEnd
        }

        private const string Ragdolls = "Ragdolls";
        private const string Reactions = "Ragdoll Reactions";

        internal static ConfigEntry<bool> Enabled { get; private set; }
        internal static ConfigEntry<bool> DebugLogging { get; private set; }
        internal static ConfigEntry<bool> StabilizeAiAimWhenHit { get; private set; }
        internal static ConfigEntry<bool> SupportAndTone { get; private set; }
        internal static ConfigEntry<bool> StiffLeg { get; private set; }
        internal static ConfigEntry<bool> StiffHip { get; private set; }
        internal static ConfigEntry<bool> StiffArm { get; private set; }
        internal static ConfigEntry<float> StartBendForce { get; private set; }
        internal static ConfigEntry<float> EndBendForce { get; private set; }
        internal static ConfigEntry<float> BendForceDecayDuration { get; private set; }
        internal static ConfigEntry<BendDecayCurve> BendForceDecayCurve { get; private set; }
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
            BindPresetButton(config, "PresetVanillaPlus", "Vanilla+", 1200,
                ApplyVanillaPlus);
            BindPresetButton(config, "PresetRealisticSettle", "Realistic Settle", 1190,
                ApplyRealisticSettle);
            BindPresetButton(config, "PresetRealisticPlus", "Realistic+", 1180,
                ApplyRealisticPlus);
            Enabled = Toggle(config, Ragdolls, "Enabled", true, "Enable Ragdoll Kinetics.", "Enabled", 1000);
            BindSeparator(config, "BendForceSeparator", "Bend Force", 950);
            StartBendForce = Range(config, Ragdolls, "StartBendForce", 200f, "Skeleton stiffness when the ragdoll starts.", 0f, 200f, "Bend Force - Start", 900);
            EndBendForce = Range(config, Ragdolls, "EndBendForce", 1f, "Skeleton stiffness after the decay finishes.", 0f, 200f, "Bend Force - End", 890);
            BendForceDecayDuration = Range(config, Ragdolls, "BendForceDecayDuration", 0.55f, "Seconds for bend force to move from its start value to its end value.", 0.1f, 10f, "Bend Force - Decay Time", 880);
            BendForceDecayCurve = BindDecayCurve(config);
            BindSeparator(config, "AnimationSeparator", "Animation Carry", 850);
            AnimationCarryDuration = Range(config, Ragdolls, "AnimationCarryDuration", 0.8f, "Seconds for death-pose motion to decay into physics.", 0.1f, 2f, "Animation Carry Duration", 800);
            AnimationCarryStrength = Range(config, Ragdolls, "AnimationCarryStrength", 2.5f, "Strength of animation-to-ragdoll guidance.", 0.5f, 10f, "Animation Carry Strength", 790);
            AnimationSettleDuration = Range(config, Ragdolls, "AnimationSettleDuration", 0.25f, "Residual damping after animation carry.", 0.1f, 1.5f, "Animation Settle Duration", 780);
            BindSeparator(config, "CorpsePhysicsSeparator", "Corpse Physics", 750);
            FreezeDelay = Range(config, Ragdolls, "FreezeDelay", 10f, "Seconds before EFT freezes corpse physics.", 2f, 60f, "Corpse Freeze Delay", 700);
            ImpulseScale = Range(config, Ragdolls, "ImpulseScale", 0.35f, "Scale applied to EFT's fatal-shot impulse.", 0f, 1f, "Fatal Impulse Scale", 690);
            DebugLogging = Toggle(config, Ragdolls, "DebugLogging", false, "Write detailed joint diagnostics to the BepInEx log.", "Debug Logging", 100);

            StabilizeAiAimWhenHit = config.Bind("AI Hit Response", "StabilizeAimWhenHit", true, "Prevent random aim-direction flicks when an AI is hit by its already visible target.");

            SupportAndTone = config.Bind(Reactions, "SupportAndTone", true, "Stagger muscle release and make leg force depend on support.");
            ToneReleaseSpread = Range(config, Reactions, "ToneReleaseSpread", 0.125f, "Maximum random joint tone-release delay.", 0f, 0.6f);
            UnsupportedLegStrength = Range(config, Reactions, "UnsupportedLegStrength", 50f, "Percent of leg power retained without ground support.", 0f, 100f);
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

        private static void ApplyVanillaPlus()
        {
            ApplyPreset(25f, 25f, 1.5f, BendDecayCurve.Linear,
                0.8f, 4f, 0.5f);
        }

        private static void ApplyRealisticSettle()
        {
            ApplyPreset(30f, 1f, 0.6f, BendDecayCurve.Linear,
                0.65f, 10f, 0.25f);
        }

        private static void ApplyRealisticPlus()
        {
            ApplyPreset(200f, 1f, 0.55f, BendDecayCurve.SlowStart,
                0.8f, 2.5f, 0.25f);
        }

        private static void ApplyPreset(float startBend, float endBend,
            float bendDecay, BendDecayCurve decayCurve, float carryDuration,
            float carryStrength, float settleDuration)
        {
            StartBendForce.Value = startBend;
            EndBendForce.Value = endBend;
            BendForceDecayDuration.Value = bendDecay;
            AnimationCarryDuration.Value = carryDuration;
            AnimationCarryStrength.Value = carryStrength;
            AnimationSettleDuration.Value = settleDuration;
            BendForceDecayCurve.Value = decayCurve;
            FreezeDelay.Value = 10f;
            ImpulseScale.Value = 0.35f;
            SupportAndTone.Value = true;
            ToneReleaseSpread.Value = 0.125f;
            UnsupportedLegStrength.Value = 50f;
            StiffLeg.Value = true;
            StiffLegChance.Value = 45f;
            StiffLegStrengthMin.Value = 0f;
            StiffLegStrengthMax.Value = 30f;
            StiffHip.Value = true;
            StiffHipChance.Value = 30f;
            StiffHipStrengthMin.Value = 0f;
            StiffHipStrengthMax.Value = 30f;
            StiffArm.Value = true;
            StiffArmChance.Value = 35f;
            StiffArmStrengthMin.Value = 0f;
            StiffArmStrengthMax.Value = 15f;
        }

        private static void BindPresetButton(ConfigFile config, string key,
            string label, int order, System.Action action)
        {
            ConfigurationManagerAttributes attributes =
                new ConfigurationManagerAttributes
                {
                    Category = "Ragdoll Presets",
                    DispName = label,
                    Order = order,
                    HideDefaultButton = true,
                    HideSettingName = true,
                    CustomDrawer = ignored =>
                    {
                        if (GUILayout.Button(label, GUILayout.ExpandWidth(true)))
                            action();
                    }
                };
            config.Bind(Ragdolls, key, false,
                new ConfigDescription("Apply the " + label + " preset.",
                    null, attributes));
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

        private static ConfigEntry<BendDecayCurve> BindDecayCurve(ConfigFile config)
        {
            ConfigEntry<BendDecayCurve> entry = null;
            ConfigurationManagerAttributes attributes =
                new ConfigurationManagerAttributes
                {
                    DispName = "Bend Force - Decay Curve",
                    Order = 870,
                    HideDefaultButton = true,
                    HideSettingName = true,
                    CustomDrawer = ignored => DrawDecayCurve(entry)
                };
            entry = config.Bind(Ragdolls, "BendForceDecayCurve",
                BendDecayCurve.SlowStart,
                new ConfigDescription("Controls how bend force changes over time.",
                    null, attributes));
            return entry;
        }

        private static void DrawDecayCurve(ConfigEntry<BendDecayCurve> entry)
        {
            GUILayout.BeginHorizontal();
            try
            {
                DrawCurveButton(entry, BendDecayCurve.Linear, "Linear");
                DrawCurveButton(entry, BendDecayCurve.SlowStart, "Slow Start");
                DrawCurveButton(entry, BendDecayCurve.SlowEnd, "Slow End");
            }
            finally
            {
                GUILayout.EndHorizontal();
            }
        }

        private static void DrawCurveButton(ConfigEntry<BendDecayCurve> entry,
            BendDecayCurve value, string label)
        {
            bool selected = entry.Value == value;
            if (GUILayout.Toggle(selected, label, GUI.skin.button,
                GUILayout.ExpandWidth(true)) && !selected)
                entry.Value = value;
        }

        private static ConfigEntry<float> Percentage(ConfigFile config, string key, float value, string description)
        {
            return Range(config, Reactions, key, value, description, 0f, 100f);
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
