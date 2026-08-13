# Ragdoll Kinetics

Ragdoll Kinetics is an animation-driven, biomechanical ragdoll physics mod for SPT.

It carries living animation and horizontal momentum into death, uses anatomically constrained joints, progressively releases muscle tone, supports independent limb-stiffness reactions, and keeps corpses physically reactive until EFT freezes them for performance.

It also includes an optional AI hit-response correction that prevents the native random aim flick when a bot is damaged by an enemy it already sees.

## Build

Run `./build.ps1` for a Release build, deployment to SPT, and a distributable zip in `dist/`.

Use `./build.ps1 -NoDeploy` to package without installing, or `./build.ps1 -NoDeploy -NoPackage` for compile-only verification.
