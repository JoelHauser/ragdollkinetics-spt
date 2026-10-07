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
            internal string Caliber;
            internal float Energy;
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

        // One push per caliber, whatever the round (FMJ, HP, AP...), from the energy of
        // the caliber's typical round in the SPT 4.1 item database through
        // RoundPushForEnergy. Calibers not listed (modded ones) use their round's
        // measured energy instead.
        private static readonly Dictionary<string, float> CaliberPush =
            new Dictionary<string, float>
        {
            { "Caliber9x18PM", 0.46f },
            { "Caliber46x30", 0.53f },
            { "Caliber9x19PARA", 0.58f },
            { "Caliber762x25TT", 0.62f },
            { "Caliber1143x23ACP", 0.66f }, // .45 ACP
            { "Caliber9x21", 0.66f },
            { "Caliber57x28", 0.74f },
            { "Caliber9x39", 0.77f },
            { "Caliber9x33R", 0.87f }, // .357 Magnum
            { "Caliber545x39", 1.04f },
            { "Caliber23x75", 1.07f }, // KS-23
            { "Caliber556x45NATO", 1.20f },
            { "Caliber762x39", 1.37f },
            { "Caliber762x35", 1.39f }, // .300 Blackout
            { "Caliber20g", 1.41f },
            { "Caliber366TKM", 1.43f },
            { "Caliber127x33", 1.45f }, // .50 AE
            { "Caliber12g", 1.47f },
            { "Caliber127x55", 1.59f },
            { "Caliber762x51", 1.86f },
            { "Caliber762x54R", 1.94f },
            { "Caliber68x51", 1.96f },
            { "Caliber86x70", 2.76f }, // .338 Lapua Magnum
            { "Caliber127x108", 3.50f },
            { "Caliber127x99", 3.50f }, // .50 BMG
            { "Caliber20x1mm", 0.40f },
            { "Caliber26x75", 0.40f }, // flares
            { "Caliber40x46", 0.40f }
        };

        // Impact energy at which the push settings apply at face value.
        private const float ReferenceEnergy = 1300f;
        private const float ShotMemory = 1f;
        private const float BlastMemory = 0.5f;

        private static readonly Dictionary<int, ShotRecord> Shots =
            new Dictionary<int, ShotRecord>(64);
        private static readonly List<int> StaleShots = new List<int>(16);
        private static readonly List<Blast> Blasts = new List<Blast>(8);
        private static int _nextBlastId;

        // Corpses compare this every physics step and skip the blast list while it
        // has not moved.
        internal static int LatestBlastId => _nextBlastId;

        internal static void RecordShot(Player target, string caliber,
            float energy, int fireIndex)
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
                Caliber = caliber,
                Energy = energy,
                FireIndex = fireIndex
            };
            if (Shots.Count > 128) PruneShots(now);
        }

        internal static bool TryGetShot(GameObject target, out ShotRecord record)
        {
            return Shots.TryGetValue(target.GetInstanceID(), out record) &&
                Time.time - record.Time <= ShotMemory;
        }

        internal static float RoundPush(ShotRecord record) =>
            record.Caliber != null &&
            CaliberPush.TryGetValue(record.Caliber, out float push)
                ? push : RoundPushForEnergy(record.Energy);

        // Below the reference the push grows with the square root of energy; above it,
        // faster, so the heavy hitters stand out. Capped at x3.5.
        internal static float RoundPushForEnergy(float energy)
        {
            float ratio = Mathf.Max(0f, energy) / ReferenceEnergy;
            return Mathf.Clamp(ratio < 1f ? Mathf.Sqrt(ratio)
                : Mathf.Pow(ratio, 0.65f), 0.4f, 3.5f);
        }

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
