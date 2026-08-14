using UnityEngine;

namespace RagdollKinetics
{
    internal sealed class PreviewDeathMarker : MonoBehaviour
    {
        private float _expiresAt;

        internal bool SuppressImpulse =>
            Time.realtimeSinceStartup <= _expiresAt;

        internal void Arm()
        {
            _expiresAt = Time.realtimeSinceStartup + 10f;
        }
    }
}
