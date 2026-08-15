using BepInEx;
using BepInEx.Logging;
using RagdollKinetics.Patches;
using SPT.Reflection.Patching;

namespace RagdollKinetics
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.hysocs.ragdollkinetics";
        public const string Name = "Ragdoll Kinetics";
        public const string Version = "1.2.0";

        internal static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);
            PatchManager patchManager = new PatchManager(this);
            patchManager.AddPatches(RagdollPatches.CreateAll());
            patchManager.EnablePatches();
            gameObject.AddComponent<RagdollPreviewController>();
            gameObject.AddComponent<NativeDeathDiagnostics>();
            Logger.LogInfo(Name + " " + Version + " loaded");
        }
    }
}
