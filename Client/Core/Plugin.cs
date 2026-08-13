using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace RagdollKinetics
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.hysocs.ragdollkinetics";
        public const string Name = "Ragdoll Kinetics";
        public const string Version = "1.1.0";

        internal static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo(Name + " " + Version + " loaded");
        }
    }
}
