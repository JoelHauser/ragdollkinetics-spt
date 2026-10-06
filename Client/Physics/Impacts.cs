using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace RagdollKinetics.Patches
{
    // What last hit each player, and recent explosions, recorded where the game
    // hands them out and read by a corpse when its ragdoll is set up.
    internal static class Impacts
    {
        internal struct ShotRecord
        {
            internal float Time;
            internal float Energy;
            internal float Penetration;
            internal int FireIndex;
        }

        internal struct Blast
        {
            internal int Id;
            internal float Time;
            internal Vector3 Position;
            internal float Radius;
            internal float Strength;
        }

        // Impact energy at which a soft round pushes at the settings' face value;
        // a 7.62x54R LPS comes out close to it once its penetration is counted.
        private const float ReferenceEnergy = 1300f;
        private const float ShotMemory = 1f;
        private const float BlastMemory = 0.5f;

        private static readonly Dictionary<int, ShotRecord> Shots =
            new Dictionary<int, ShotRecord>(64);
        private static readonly List<int> StaleShots = new List<int>(16);
        private static readonly List<Blast> Blasts = new List<Blast>(8);
        private static int _nextBlastId;

        internal static void RecordShot(Player target, float energy,
            float penetration, int fireIndex)
        {
            int key = target.gameObject.GetInstanceID();
            float now = Time.time;
            // Pellets from one shell land in the same moment: add them up.
            if (Shots.TryGetValue(key, out ShotRecord previous) &&
                previous.FireIndex == fireIndex && now - previous.Time < 0.1f)
                energy += previous.Energy;
            Shots[key] = new ShotRecord
            {
                Time = now,
                Energy = energy,
                Penetration = penetration,
                FireIndex = fireIndex
            };
            if (Shots.Count > 128) PruneShots(now);
        }

        internal static bool TryGetShot(GameObject target, out ShotRecord record)
        {
            return Shots.TryGetValue(target.GetInstanceID(), out record) &&
                Time.time - record.Time <= ShotMemory;
        }

        // How hard this round hits relative to the reference. Below it the push grows
        // with the square root of energy; above it, faster, so the heavy hitters
        // (.338, .50 BMG, 12.7 mm, slugs) stand out.
        internal static float EnergyFactor(ShotRecord record)
        {
            float ratio = Mathf.Max(0f, record.Energy) / ReferenceEnergy;
            return Mathf.Clamp(ratio < 1f ? Mathf.Sqrt(ratio)
                : Mathf.Pow(ratio, 0.65f), 0.4f, 3.5f);
        }

        // Rounds that pass straight through (high penetration) give up to a quarter less.
        internal static float PenetrationFactor(ShotRecord record) =>
            0.75f + 0.25f * Mathf.InverseLerp(50f, 20f, record.Penetration);

        internal static void RecordBlast(Vector3 position, float radius,
            float strength)
        {
            float now = Time.time;
            Blasts.RemoveAll(blast => now - blast.Time > BlastMemory);
            Blasts.Add(new Blast
            {
                Id = ++_nextBlastId,
                Time = now,
                Position = position,
                Radius = Mathf.Max(1f, radius),
                Strength = strength
            });
        }

        // Explosions newer than lastId and younger than the blast memory.
        internal static int CollectBlasts(int lastId, List<Blast> into)
        {
            into.Clear();
            float now = Time.time;
            for (int i = 0; i < Blasts.Count; i++)
                if (Blasts[i].Id > lastId &&
                    now - Blasts[i].Time <= BlastMemory)
                    into.Add(Blasts[i]);
            return _nextBlastId;
        }

        private static void PruneShots(float now)
        {
            StaleShots.Clear();
            foreach (KeyValuePair<int, ShotRecord> pair in Shots)
                if (now - pair.Value.Time > ShotMemory) StaleShots.Add(pair.Key);
            foreach (int key in StaleShots) Shots.Remove(key);
        }
    }
}
