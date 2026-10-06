using UnityEngine;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// The blood system's only public surface: enough for another Y4NGZ plugin to make something
    /// bleed at a point, and nothing else (#120).
    ///
    /// <b>Why a facade rather than InternalsVisibleTo.</b> Every other type in this folder is
    /// internal and stays that way. Y4NGZUpgrades needs one verb — "this creature bled here, in
    /// this colour" — and handing it the whole director would make the pool, the caps, the decal
    /// registry and the projector geometry into a cross-plugin contract that could not be changed
    /// without breaking a separately versioned DLL. Upgrades already depends on this assembly
    /// (<c>CombatReticleHud</c> reads <c>Experience.UITheme</c>), so the pattern is established; the
    /// discipline is that the surface stays one method wide.
    ///
    /// <b>Whose plugin owns what.</b> This assembly owns HOW blood looks and how much of it the
    /// world can hold — the pool, the caps, the rate limit, the decal-receiving audit, the config
    /// section. The caller owns WHICH creatures bleed and in WHAT colour; that is a per-species
    /// classification and it lives in Upgrades' impact-material catalog, where the roster is.
    /// Neither half can be inferred from the other, which is why the split is here.
    ///
    /// <b>The config still governs.</b> A call is honoured only when the user has Blood FX on and
    /// the bundle loaded, and it respects the same intensity multiplier, rate limit and viewer
    /// distance as player blood. Turning blood off turns creature blood off with it.
    /// </summary>
    public static class BloodFXCreatureBlood
    {
        /// <summary>
        /// Whether a call would draw anything at all: the system is enabled and its bundle is
        /// loaded. Callers use this to report "blood is off" once, rather than to decide whether
        /// to call — <see cref="PlayCreatureBleed"/> re-checks both and is safe to call regardless.
        /// </summary>
        public static bool IsAvailable =>
            (BloodFXBootstrap.Enabled?.Value ?? false) && BloodFXAssets.IsAvailable;

        /// <summary>
        /// Make a creature bleed at a contact point.
        /// </summary>
        /// <param name="creatureKey">Stable per-creature id — <c>EnemyAI.GetInstanceID()</c>. Used
        /// only to rate-limit one creature independently of the others, so any consistent integer
        /// works; passing a shared constant would make every monster on the moon share one budget.</param>
        /// <param name="point">World contact point. This is a world position, not a parented one:
        /// the burst is sub-second and lives where the round landed, not where the creature walks
        /// to next.</param>
        /// <param name="direction">Which way the blood travels — the projectile's direction of
        /// travel, NOT the surface normal. Aims the spray cone and biases the splatter rays, so
        /// passing the normal sprays blood back at the shooter. <see cref="Vector3.zero"/> for an
        /// omnidirectional burst with no spray.</param>
        /// <param name="tint">The blood colour, or <see cref="Color.clear"/> — any non-positive
        /// alpha — to draw the bundle's authored colour untouched. Human red is authored, so the
        /// flesh case should pass clear and get the shipped look bit for bit; only a species that
        /// bleeds something else pays for a tint.</param>
        /// <param name="contacts">How many separate impacts this one event stands for. Coalesce a
        /// shotgun's pellets into a single call and pass the count rather than calling per pellet;
        /// several at once read as a heavier wound.</param>
        /// <param name="scale">Multiplies the burst's authored SIZE (#147). Deliberately distinct
        /// from <paramref name="contacts"/>, which raises the particle COUNT: until #147 this
        /// system had no size control on any path, so every burst was the size it was authored at
        /// — for a player wound seen at melee range — and a "heavier" wound only ever meant more
        /// particles in the same volume. Pass 1f for the authored size. The caller owns what a
        /// rifle round or a shotgun blast is worth, because the ammo families live on its side of
        /// the bridge and this plugin has no opinion about them.</param>
        /// <returns>True if a burst or at least one decal was actually placed. False means a gate
        /// declined it — disabled, no bundle, too far from the camera, or rate limited — and is a
        /// normal outcome, not an error.</returns>
        public static bool PlayCreatureBleed(
            int creatureKey, Vector3 point, Vector3 direction, Color tint, int contacts, float scale)
        {
            return BloodFXDirector.OnCreatureBled(
                creatureKey, point, direction, tint, Mathf.Max(1, contacts), Mathf.Max(0.01f, scale));
        }
    }
}
