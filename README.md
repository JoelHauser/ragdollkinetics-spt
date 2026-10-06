# Ragdoll Kinetics

Ragdoll Kinetics is an animation-driven biomechanical ragdoll physics mod for SPT that carries movement into death, adds constrained joints and progressive muscle relaxation, and keeps corpses physically reactive.

## This fork

A personal fork of [Hysocs/ragdollkinetics-spt](https://github.com/Hysocs/ragdollkinetics-spt) (Apache 2.0).

- **Bots drop when they die.** Each body region loses muscle tone on its own timer, legs first, so the knees buckle instead of the body standing for a second. The game's push from the killing shot lands where the bullet hit, and a moving bot's momentum carries it along the ground without holding it up.
- **Bodies fall with the shot.** The upper body is pushed along the bullet's line, so a bot tips away from where it was hit. The struck limb goes limp at once, and a leg hit kicks the leg out from under it.
- **The caliber matters.** Each caliber has one push value, from 9x18 (x0.46) up to .50 BMG and 12.7x108 (x3.5). Heavy calibers also make the body go limp faster. Modded calibers use their round's measured energy.
- **Explosions push bodies.** Thrown grenades, launcher rounds and mines push bots they kill and fresh corpses near them, unevenly per body part and falling off quickly with distance: an F-1 knocks a body back about 0.3 m at 2 m.
- **Lighter on performance.** Corpses sleep and freeze once they settle, animation copies are built one bot per frame, and a debug component no longer allocates every frame.

## Settings (F12)

| Setting | Default | What it does |
| --- | --- | --- |
| Collapse: Legs Give Way, Arms Go Limp, Spine Gives Way, Head Drops | 0.15, 0.3, 0.5, 0.4 s | How long each region keeps muscle tone after death |
| Collapse: Headshot Lights Out | on | A head or neck kill makes every region go limp four times faster |
| Collapse: Fall With The Shot | 1.2 m/s | Push along the bullet's line; 1.5x for head hits |
| Collapse: Bullet Type Matters | on | Scale that push by caliber |
| Collapse: Explosion Push | 3.5 m/s | Push from a strength-100 grenade at point-blank range |
| Ragdolls: Freeze Corpses Once Settled | on | Off keeps every corpse awake and shootable for the freeze delay |
| Ragdolls: Performance Logging | off | One `[Perf]` line per spawn, death and settled corpse |

## Building

```
dotnet build Client\RagdollKinetics.Client.csproj -c Release -p:SptRoot=<your SPT folder>
```

It references the game's `Assembly-CSharp.dll`, so `SptRoot` must be an install that SPT has already patched (one you have launched at least once). The build copies the DLL into `BepInEx\plugins\Hysocs-RagdollKinetics`; close the game first.
