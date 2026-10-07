# Ragdoll Kinetics

Ragdoll Kinetics is an animation-driven biomechanical ragdoll physics mod for SPT that carries movement into death, adds constrained joints and progressive muscle relaxation, and keeps corpses physically reactive.

## This fork

A personal fork of [Hysocs/ragdollkinetics-spt](https://github.com/Hysocs/ragdollkinetics-spt) (Apache 2.0).

- **Bots drop when they die.** Each body region loses muscle tone on its own timer, legs first, so the knees buckle instead of the body standing for a second. The game's push from the killing shot lands where the bullet hit, and a moving bot's momentum carries it along the ground without holding it up.
- **Bodies fall with the shot.** The upper body is pushed along the bullet's line, so a bot tips away from where it was hit. The struck limb goes limp at once, and a leg hit kicks the leg out from under it.
- **The caliber matters.** Each caliber has one push value, from 9x18 (x0.46) up to .50 BMG and 12.7x108 (x3.5). Heavy calibers also make the body go limp faster. Modded calibers use their round's measured energy.
- **Explosions push bodies.** Thrown grenades, launcher rounds and mines push bots they kill and fresh corpses near them, unevenly per body part and falling off quickly with distance: an F-1 knocks a body back about 0.3 m at 2 m.
- **The body moves as one.** Each joint's muscles and bend limits are sized by the weight it carries, so the head and forearms follow the body as freely as the hips instead of staying locked in pose, and a spine or hip no longer folds far past its limit under its own weight.
- **Motion carries into death.** Every body part starts its fall moving the way it was moving in the animation: a running bot's legs and arms keep their swing instead of every part starting at the same speed with no spin.
- **Lighter on performance.** A corpse stops driving its joints and drops its hidden animation copy as soon as its last muscle lets go (about 0.5-0.9 s instead of 2.3-4.3 s), then sleeps and freezes once it settles. The animation copy's controller is parsed on a worker thread, so a spawn costs the main thread far less. Settling corpses, blast checks and the preview hooks no longer do work every frame that they don't need.

## Settings (F12)

| Setting | Default | What it does |
| --- | --- | --- |
| Collapse: Legs Give Way, Arms Go Limp, Spine Gives Way, Head Drops | 0.15, 0.3, 0.5, 0.4 s | How long each region keeps muscle tone after death |
| Collapse: Headshot Lights Out | on | A head or neck kill makes every region go limp four times faster |
| Collapse: Fall With The Shot | 1.2 m/s | Push along the bullet's line; 1.5x for head hits |
| Collapse: Bullet Type Matters | on | Scale that push by caliber |
| Collapse: Explosion Push | 3.5 m/s | Push from a strength-100 grenade at point-blank range |
| Collapse: Carry Limb Motion | 1 | How much of each part's own motion at death carries into the fall; 0 starts every part at the body's speed |
| Collapse: Limb Speed Limit | 20 rad/s | Fastest a body part may spin; higher lets limbs whip on impacts, lower calms them (was 12) |
| Ragdolls: Freeze Corpses Once Settled | on | Off keeps every corpse awake and shootable for the freeze delay |
| Ragdolls: Performance Logging | off | One `[Perf]` line per spawn, death and settled corpse |

## Building

```
dotnet build Client\RagdollKinetics.Client.csproj -c Release -p:SptRoot=<your SPT folder>
```

It references the game's `Assembly-CSharp.dll`, so `SptRoot` must be an install that SPT has already patched (one you have launched at least once). The build copies the DLL into `BepInEx\plugins\Hysocs-RagdollKinetics`; close the game first.
