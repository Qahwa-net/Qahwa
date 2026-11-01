# ADR-141: Patches run in app order

**Status:** accepted (platform)

## Context
Patches (ADR-043) ran in one list sorted by name. An app that depends on another (ADR-085) could have a patch whose name sorts before a patch of its dependency, so it ran first and saw data the dependency had not yet migrated.

## Decision
- The order is: patches registered by the host itself first (name order), then the patches of each app, **in the order the apps are installed** (an app after every app it needs, otherwise as added), each app's own patches in name order. The runner (`PatchRunner`) and the patch list in the Desk, the API and the CLI use the same order (`PatchOrder.Sort`).
- A patch belongs to the app whose `Patches` list holds it (`AddQahwaApp` registers them). Names stay globally unique and are still the key in the Patch Log, so nothing already logged is run again and no name changes.
- Apps whose dependencies cannot be resolved (installing refuses those with a clear message) never make ordering fail: the apps go by name.
- A failing patch still stops the run, so the patches of later apps wait for it, which is what a dependency needs.

## Consequences
- A site that already ran patches in the old order is not affected (the log decides what is pending). Only the order of still-pending patches changes.
- Within one app, order is still by name (use a date prefix).
