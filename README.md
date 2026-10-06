# BloodVFX

![About Y4NGZ](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shared/about-y4ngz.png)

I am Y4NGZ, an up and coming rapper out of NYC/Detroit. All of my mods are
inspired by the music I write. If you are interested in knowing more about me,
check out my SoundCloud: https://soundcloud.com/y4ngz

## Generative AI usage

The entire code base of this mod was created with generative AI. Assets are
free, licensed, paid for, or generated with AI tools. If you do not wish to play
with AI-generated content, do not install this mod.

## What BloodVFX does

Vanilla blood is a short-lived flat effect. BloodVFX replaces it with blood that lands on
nearby walls and floors, stays on a wounded body, and pools where a body falls. It changes
visuals only. Damage and combat work exactly as before.

![BloodVFX gameplay preview 1](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/y4ngzbloodfx/BloodVFX.gif)

## Requirements

Your mod manager installs these automatically:

- **BepInExPack**

## What you get

- **Player wounds** - blood sticks to the player's body and stays on it through movement and
  ragdoll. It lasts through healing and clears on respawn or at round end.
- **World splatter** - nearby walls, floors and props get blood in the direction of the hit.
- **Death pools** - a death can leave a puddle where the body lands.
- **Creature and finisher blood** - supported mods can put blood on creature bodies and during
  finishers.
- **No clutter build-up** - each kind of blood has a cap. Past it, the oldest decal fades out.

BloodVFX adds no items or enemies. Each player sees the blood only if they have it
installed, so install it on every client for the whole crew to see the same thing.

![BloodVFX gameplay preview 2](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/y4ngzbloodfx/BloodVFX1.gif)

## Gibs and footprints

Fatal blasts scatter limbs, torso pieces and flesh chunks, with splatter on nearby walls and floors.
The head stays scannable; bring it aboard and the death counts as a recovered body.
Every distinct hit leaves readable blood on the body, and wounds follow the body into ragdoll.
Wounds and fresh blood pools leave a short trail of bloody footprints that fades away.

## Configuration

The configuration file is `BepInEx/config/com.y4ngz.bloodfx.cfg`. It has a master `Enabled`
switch, decal caps for walls and floors, player bodies, creature bodies and finishers, an
intensity multiplier, the decal fade time, the maximum viewing distance for new blood, and a
switch to hide the game's own blood pools. `Enable Bundy Damage Blood` turns off blood on
Bundy while keeping the rest.
`Max Blood Events Per Second` limits how often one player can bleed from rapid damage.
`Enable Decals On Room Materials` lets blood show on interior walls and tiles.
`Gib Deaths Enabled` (host) turns gib deaths on or off.
`Gib Death Causes` (host) picks which causes of death gib; the default is Blast.
`Bloody Footprints Enabled` turns the bloody footprint trails on or off.

## Compatibility

Independent installation: BloodVFX needs only the libraries listed under Requirements. The
other Y4NGZ packages are optional companions - install any combination and each one lights up
its extra behavior.

| Mod | What it adds |
| --- | --- |
| **Contracted** | The Stranger bleeds when he is hit, and his chainsaw finishers leave BloodVFX gore. |

## Asset credits

- https://assetstore.unity.com/packages/vfx/particles/spells/realistic-blood-vfx-blood-effects-blood-blood-vfx-334130

## GitHub

Source code: https://github.com/y4ngz313/BloodVFX

## Bugs

Report bugs at https://github.com/y4ngz313/BloodVFX/issues.
