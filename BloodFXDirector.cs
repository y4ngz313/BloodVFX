using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Event-driven blood presentation. Every entry point is called from a Harmony patch;
    /// nothing here runs per frame and the hit path allocates nothing beyond the decal
    /// instances themselves (the diagnostics StringBuilder is reused and only touched when
    /// the Debug Logging config entry is on).
    /// </summary>
    public static class BloodFXDirector
    {
        // Same event reaches a client through more than one vanilla path (the owner's
        // DamagePlayer, the broadcast DamagePlayerClientRpc, and the player-vs-player
        // DamagePlayerFromOtherClientClientRpc). One burst per player per window.
        //
        // This is the FLOOR on the per-player interval, not the whole story: the configurable
        // "Max Blood Events Per Second" rate limit widens the same window (see
        // ResolveMinimumHitInterval). Deduplication must keep working even with the rate limit
        // turned off, which is why the two are separate numbers sharing one timestamp.
        public const float DuplicateSuppressionSeconds = 0.12f;

        private const float SplatterMaxDistance = 2.5f;
        private const float SurfaceOffset = 0.01f;

        // --- body projector geometry -------------------------------------------------
        // The volume must hug the torso and project INWARD. Anything that pokes out in
        // front of the chest paints whatever is held there (the revolver, in the first
        // in-game test). Spawn point sits just outside the suit; the projector box then
        // runs from BodyDecalFrontMargin outside that point to BodyDecalDepth inside it,
        // stopping short of the spine so it cannot punch through onto the player's back.
        private const float BodyDecalRadius = 0.26f;
        private const float BodyDecalDepth = 0.24f;
        private const float BodyDecalFrontMargin = 0.04f;

        // Dark red on a dark suit needs presence to read at third-person distance, so these are
        // deliberately larger than the volume-leak fix alone would want. Safe to widen: the leak
        // is closed by the pivot (nothing projects forward of the chest) and by the item
        // material shield, and width only spreads the decal sideways across the torso.
        private const float BodyDecalMinWidth = 0.28f;
        private const float BodyDecalMaxWidth = 0.42f;
        private const float BodyDecalHeavyScale = 1.15f;

        // World decals project into the surface they hit; only a hair of the box is allowed
        // to stand proud of it, so a decal on a wall cannot reach a player standing near it.
        private const float WorldDecalFrontMargin = 0.03f;

        // KillPlayerClientRpc runs twice on the host (once to send, once to execute), so death
        // presentation needs its own, wider guard than the per-hit one.
        private const float DuplicateDeathSeconds = 0.5f;

        private static readonly Dictionary<int, float> LastHitTime = new Dictionary<int, float>(8);
        private static readonly Dictionary<int, float> LastDeathTime = new Dictionary<int, float>(8);

        /// <summary>
        /// Creature bleed timestamps (#120), keyed on the creature. Separate from
        /// <see cref="LastHitTime"/> on purpose: these are two independent populations sharing a
        /// key space (both are <c>GetInstanceID()</c>), and one dictionary would let a player and a
        /// monster suppress each other's blood.
        /// </summary>
        private static readonly Dictionary<int, float> LastCreatureBurst = new Dictionary<int, float>(8);
        private static readonly Dictionary<int, float> LastCreatureSpray = new Dictionary<int, float>(8);
        private static readonly StringBuilder Diagnostics = new StringBuilder(512);
        private static RaycastHit _hit;
        private static bool _loggedWorldProjector;

        /// <summary>
        /// Set by the DamagePlayerFromOtherClientClientRpc prefix so the owner's inner
        /// DamagePlayer call can use the real hit direction instead of an omni burst.
        /// </summary>
        internal static Vector3 PendingHitDirection { get; set; }

        internal static void ResetTransientState()
        {
            LastHitTime.Clear();
            LastDeathTime.Clear();
            LastCreatureBurst.Clear();
            LastCreatureSpray.Clear();
            PendingHitDirection = Vector3.zero;
            _loggedWorldProjector = false;
            // #322: a chute is the only thing here that outlives its own call, so it is the only
            // thing that can survive a round boundary.
            BloodFXFinisherGore.StopAll("transient state reset");
            // #393: Bundy's finisher collision suppression is the other static registry with the
            // same round-boundary problem, and this used to reset it from here. It now runs its own
            // patches in Y4NGZCompany (BundyFinisherRoundLifecyclePatches): ignored collider pairs
            // are gameplay state, and blood is an optional plugin that must not own them.
            BloodFXDecalSurfaces.ResetPlayerState();
        }

        // ------------------------------------------------------------------- hits

        internal static void OnPlayerDamaged(PlayerControllerB player, int damage, Vector3 hitDirection, string clientPath)
        {
            if (player == null || damage <= 0)
                return;
            if (!(BloodFXBootstrap.Enabled?.Value ?? false) || !BloodFXAssets.IsAvailable)
                return;

            int key = player.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            bool directional = hitDirection.sqrMagnitude > 0.0001f;

            float minimumInterval = ResolveMinimumHitInterval();
            if (LastHitTime.TryGetValue(key, out float last) && now - last < minimumInterval)
            {
                // Below the dedupe floor it is the same damage event arriving twice; above it,
                // a genuinely distinct hit that the rate limit is dropping.
                string reason = now - last < DuplicateSuppressionSeconds ? "duplicate" : "rate limited";
                BloodFXBootstrap.Trace(
                    $"hit SUPPRESSED ({reason} {now - last:0.000}s < {minimumInterval:0.000}s) "
                    + $"player='{BloodFXDecalSurfaces.SafeName(player)}' dmg={damage} path={clientPath}.");
                return;
            }

            LastHitTime[key] = now;

            Transform anchor = ResolveTorsoAnchor(player);
            if (anchor == null)
            {
                BloodFXBootstrap.Trace(
                    $"hit ABORTED (no torso anchor) player='{BloodFXDecalSurfaces.SafeName(player)}' "
                    + $"dmg={damage} path={clientPath}.");
                return;
            }

            float intensity = Mathf.Max(0.01f, BloodFXBootstrap.IntensityMultiplier?.Value ?? 1f);
            Tier tier = GetTier(damage);
            Vector3 origin = anchor.position;
            Vector3 direction = directional ? hitDirection.normalized : Vector3.zero;

            bool nearby = IsWithinViewerRange(origin, out float viewerDistance);

            BloodFXBootstrap.Trace(
                $"hit player='{BloodFXDecalSurfaces.SafeName(player)}' dmg={damage} tier={tier} "
                + $"dirKnown={(directional ? "y" : "n")} path={clientPath} anchor='{anchor.name}' "
                + $"viewerDist={FormatDistance(viewerDistance)} nearby={(nearby ? "y" : "n")}.");

            if (nearby)
            {
                // Color.clear: a player bleeds the colour the bundle authored. See BloodFXTint.
                // 1f: the authored size. Player blood is unchanged by #147.
                PlayBurst(origin, direction, tier, intensity, Color.clear, 1f);
                SprayEnvironment(player.transform, origin, direction, tier, intensity, Color.clear);
            }

            // Body blood is deliberately NOT distance-gated. It rides a player who can walk into
            // view minutes later, so gating it would show a wounded teammate with a clean suit.
            // The per-player cap already bounds it, and the rate limit above bounds how fast it
            // can accumulate.
            AddBodyDecal(player, anchor, direction, tier);
        }

        internal static void OnPlayerKilled(PlayerControllerB player, Vector3 bodyVelocity, bool spawnBody)
        {
            if (player == null)
                return;
            if (!(BloodFXBootstrap.Enabled?.Value ?? false) || !BloodFXAssets.IsAvailable)
                return;

            int key = player.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            if (LastDeathTime.TryGetValue(key, out float lastDeath) && now - lastDeath < DuplicateDeathSeconds)
            {
                BloodFXBootstrap.Trace(
                    $"death SUPPRESSED (duplicate {now - lastDeath:0.000}s) player='{BloodFXDecalSurfaces.SafeName(player)}'.");
                return;
            }

            LastDeathTime[key] = now;
            LastHitTime[key] = now;

            // KillPlayerClientRpc has already run DisablePlayerModel and reparented the player
            // to playersContainer by the time our postfix fires, but SetParent preserves world
            // position and the rig transforms keep their death pose, so the spine bone is still
            // the right origin whether or not a body was spawned.
            Transform anchor = ResolveTorsoAnchor(player);
            bool anchored = anchor != null;
            Vector3 origin = anchored ? anchor.position : player.transform.position + Vector3.up;
            float intensity = Mathf.Max(0.01f, BloodFXBootstrap.IntensityMultiplier?.Value ?? 1f);
            Vector3 direction = bodyVelocity.sqrMagnitude > 0.0001f ? bodyVelocity.normalized : Vector3.zero;

            bool nearby = IsWithinViewerRange(origin, out float viewerDistance);

            int placed = 0;
            if (nearby)
            {
                // 1f: the authored size. Player blood is unchanged by #147.
                PlayBurst(origin, direction, Tier.Heavy, intensity, Color.clear, 1f);
                placed = SprayEnvironment(player.transform, origin, direction, Tier.Heavy, intensity, Color.clear);
            }

            // A corpse can re-enter view after the death event, just like a wounded living player.
            // Keep this persistent mark distance-independent and on the ordinary hit channel; the
            // finisher chute owns a separate budget below.
            if (anchor != null)
                AddBodyDecal(player, anchor, direction, Tier.Heavy);

            // The puddle marks where a body is lying and players walk back to it to collect the
            // corpse, so it is placed no matter how far away the viewer was at the moment of
            // death. It is one decal per death, at most four per round.
            bool puddleFoundFloor = SpawnDeathPuddle(player, out Vector3 puddlePoint, out bool puddleSpawned);

            BloodFXBootstrap.Trace(
                $"death player='{BloodFXDecalSurfaces.SafeName(player)}' spawnBody={spawnBody} "
                + $"burst={(nearby ? "heavy" : "skipped(far)")} origin={Format(origin)} "
                + $"originSource={(anchored ? "spine:" + anchor.name : "transform+up")} "
                + $"viewerDist={FormatDistance(viewerDistance)} nearby={(nearby ? "y" : "n")} "
                + $"splatterDecals={placed} puddle={(puddleSpawned ? "spawned" : "none")} "
                + $"puddlePos={Format(puddlePoint)} puddleRaycastHitFloor={puddleFoundFloor}.");
        }

        // -------------------------------------------------------------- creatures

        /// <summary>
        /// A creature bled at a known point (#120). Called from Y4NGZUpgrades through
        /// <see cref="BloodFXCreatureBlood"/>; nothing in this plugin raises it.
        ///
        /// <b>Two gates, not one, and they are deliberately different widths.</b> The player path
        /// runs burst and splatter behind a single interval because a player is hit a few times a
        /// minute. A creature is hit twelve times a second by a rifle, and the two halves of the
        /// effect have nothing like the same cost: the particle burst is a round-robin pool that
        /// never allocates, while every splatter ray that lands mints a decal against a 48-decal
        /// round-long cap. So the burst runs at the duplicate-suppression floor — responsive, which
        /// is the entire point of the effect — and only the decals answer to the configured
        /// events-per-second limit. Tying them together would either make sustained fire
        /// unresponsive or let one magazine churn the whole world cap.
        ///
        /// <paramref name="contacts"/> is how many separate impacts this one event stands for: the
        /// caller coalesces a shotgun's pellets into a single bleed rather than eight, and a blast
        /// that put five pellets in should read heavier than one rifle round.
        /// </summary>
        internal static bool OnCreatureBled(
            int creatureKey, Vector3 origin, Vector3 direction, Color tint, int contacts, float scale)
        {
            if (!(BloodFXBootstrap.Enabled?.Value ?? false) || !BloodFXAssets.IsAvailable)
                return false;

            // Creature hits resolve on every client, exactly like damage RPCs, so the same reasoning
            // that gates player blood by distance applies unchanged: blood placed across the moon
            // spends slots in the cap and evicts the blood in the room you are standing in.
            if (!IsWithinViewerRange(origin, out float viewerDistance))
            {
                BloodFXBootstrap.Trace(
                    $"creature bleed SKIPPED (far {FormatDistance(viewerDistance)}) key={creatureKey}.");
                return false;
            }

            float now = Time.realtimeSinceStartup;
            bool burst = !LastCreatureBurst.TryGetValue(creatureKey, out float lastBurst)
                         || now - lastBurst >= DuplicateSuppressionSeconds;
            float sprayInterval = ResolveMinimumHitInterval();
            bool spray = !LastCreatureSpray.TryGetValue(creatureKey, out float lastSpray)
                         || now - lastSpray >= sprayInterval;

            if (!burst && !spray)
            {
                BloodFXBootstrap.Trace($"creature bleed SUPPRESSED (both gates) key={creatureKey}.");
                return false;
            }

            float intensity = Mathf.Max(0.01f, BloodFXBootstrap.IntensityMultiplier?.Value ?? 1f);
            Tier tier = contacts >= HeavyContactCount ? Tier.Heavy : Tier.Medium;
            Vector3 travel = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;

            if (burst)
            {
                LastCreatureBurst[creatureKey] = now;
                PlayBurst(origin, travel, tier, intensity, tint, scale);
            }

            int placed = 0;
            if (spray)
            {
                LastCreatureSpray[creatureKey] = now;
                // No source root: IsRejectedSurface refuses every EnemyAI outright, so the creature
                // that is bleeding — and every other one nearby — is already excluded.
                placed = SprayEnvironment(null, origin, travel, tier, intensity, tint);
            }

            BloodFXBootstrap.Trace(
                $"creature bleed key={creatureKey} tier={tier} contacts={contacts} scale={scale:0.##} "
                + $"tint=({tint.r:0.##},{tint.g:0.##},{tint.b:0.##},{tint.a:0.##}) "
                + $"burst={(burst ? "y" : "gated")} spray={(spray ? "y" : "gated")} decals={placed} "
                + $"origin={Format(origin)} viewerDist={FormatDistance(viewerDistance)}.");

            return burst || placed > 0;
        }

        /// <summary>Coalesced impacts at or above this many read as a heavy wound — one shotgun
        /// blast rather than one rifle round.</summary>
        private const int HeavyContactCount = 3;

        // ------------------------------------------------------------- finisher gore

        /// <summary>
        /// Whether a sustained wound chute (#322) can run at all. Callers that own a fallback
        /// presentation check this BEFORE starting one, so a disabled system or a missing bundle
        /// degrades to their fallback rather than to no gore at all.
        /// </summary>
        public static bool FinisherGoreReady =>
            (BloodFXBootstrap.Enabled?.Value ?? false) && BloodFXAssets.IsAvailable;

        /// <summary>
        /// Open a blood chute at a wound that will keep moving for the length of a cut (#322).
        /// <paramref name="key"/> owns the chute — pass a stable id (the attacker's instance id) and
        /// hand the same one to <see cref="EndFinisherGore"/>. <paramref name="sourceRoot"/> is
        /// excluded from environment splatter so the chute cannot paint the body it is pouring out
        /// of; <paramref name="pose"/> is re-asked every pulse so the wound tracks the pair.
        /// </summary>
        public static bool BeginFinisherGore(
            int key,
            Transform sourceRoot,
            BloodFXWoundPose pose,
            in BloodFXFinisherTargets targets,
            float scale,
            string label)
        {
            if (!FinisherGoreReady)
            {
                BloodFXBootstrap.Trace($"finisher gore UNAVAILABLE key={key} label='{label}'.");
                return false;
            }

            return BloodFXFinisherGore.Begin(key, sourceRoot, pose, in targets, scale, label);
        }

        /// <summary>Close the chute owned by <paramref name="key"/>. Safe to call when none is open.</summary>
        public static void EndFinisherGore(int key, string reason)
        {
            BloodFXFinisherGore.End(key, reason);
        }

        /// <summary>
        /// One beat of a running chute. Called from <see cref="BloodFXFinisherGore"/> only.
        ///
        /// The burst runs at <see cref="Tier.Heavy"/> (a chainsaw through a torso is the heaviest
        /// wound this system presents) while the splatter runs a tier down and only on the pulses the
        /// driver budgeted for: emission is a free round-robin replay, decals are not.
        /// </summary>
        internal static void EmitFinisherPulse(
            Transform sourceRoot,
            Vector3 origin,
            Vector3 direction,
            float scale,
            bool placeDecals,
            bool emitFallbackParticles,
            in BloodFXFinisherTargets targets)
        {
            if (!FinisherGoreReady)
                return;

            bool nearby = IsWithinViewerRange(origin, out float viewerDistance);
            float intensity = Mathf.Max(0.01f, BloodFXBootstrap.IntensityMultiplier?.Value ?? 1f);

            // Burst and environment paint are momentary presentation, so they stay viewer-gated.
            // Body paint persists on a moving victim and must be there if that body later enters view.
            int environmentPlaced = 0;
            if (nearby)
            {
                // A manifest chute owns the continuous particles. Legacy manifests retain the
                // established pooled burst + directional spray fallback with no hardcoded name.
                if (emitFallbackParticles)
                    PlayBurst(origin, direction, Tier.Heavy, intensity, Color.clear, scale);
                if (placeDecals)
                {
                    environmentPlaced = SprayEnvironment(
                        sourceRoot,
                        origin,
                        direction,
                        Tier.Medium,
                        intensity,
                        Color.clear);
                }
            }

            int bodyPlaced = placeDecals
                ? PlaceFinisherBodyDecal(in targets, origin, direction)
                : 0;

            BloodFXBootstrap.Trace(
                $"finisher gore pulse origin={Format(origin)} dir={Format(direction)} scale={scale:0.##} "
                + $"particles={(nearby
                    ? (emitFallbackParticles ? "burst-spray-fallback" : "dedicated-chute")
                    : "far")} "
                + $"environmentDecals={(placeDecals ? environmentPlaced.ToString() : "budgeted-out")} "
                + $"bodyDecals={(placeDecals ? bodyPlaced.ToString() : "budgeted-out")} "
                + $"viewerDist={FormatDistance(viewerDistance)}.");
        }

        private static int PlaceFinisherBodyDecal(
            in BloodFXFinisherTargets targets,
            Vector3 origin,
            Vector3 outwardDirection)
        {
            PlayerControllerB victim = targets.Victim;
            Transform anchor = targets.BodyAnchor;
            if (victim == null || anchor == null || !BloodFXAssets.HasBodyPrefabs)
                return 0;

            int cap = Mathf.Max(0, BloodFXBootstrap.FinisherBodyDecalCap?.Value ?? 6);
            if (cap == 0)
                return 0;

            BloodFXDecalSurfaces.AuditPlayerMaterials(victim);
            BloodFXDecalSurfaces.EnsurePlayerDecalLayer(victim);

            long channelKey = BloodFXDecalRegistry.ChannelFor(
                victim.GetInstanceID(),
                BodyChannel.Finisher);
            bool final = BloodFXDecalRegistry.BodyCount(channelKey) >= cap - 1;
            var profile = new BloodFXBodyDecals.Profile(
                0.26f,
                0.28f,
                0.42f,
                0.24f,
                0.04f,
                1.15f);

            // ResolveFinisherWoundPose returns the direction blood exits. PlaceAt accepts an
            // incoming direction, so negate it to project from the wound surface into the body.
            GameObject instance = BloodFXBodyDecals.PlaceAt(
                channelKey,
                anchor,
                origin,
                -outwardDirection,
                in profile,
                final,
                cap,
                $"player='{BloodFXDecalSurfaces.SafeName(victim)}' source=finisher bodyIndex={targets.BodyIndex}",
                BloodFXAssets.WoundPrefab);
            return instance != null ? 1 : 0;
        }

        // ------------------------------------------------------------------ gates

        /// <summary>
        /// Smallest gap between two blood events on one player. The dedupe floor and the
        /// configured rate limit share a single timestamp, so the wider of the two wins.
        /// </summary>
        private static float ResolveMinimumHitInterval()
        {
            float eventsPerSecond = BloodFXBootstrap.MaxEventsPerSecond?.Value ?? 0f;
            if (eventsPerSecond <= 0f)
                return DuplicateSuppressionSeconds;

            return Mathf.Max(DuplicateSuppressionSeconds, 1f / eventsPerSecond);
        }

        /// <summary>
        /// Whether a blood event at <paramref name="origin"/> is close enough to this client's
        /// camera to be worth presenting. Damage RPCs run on every client, so without this a hit
        /// on the far side of the moon still instantiates decals and spends slots in the global
        /// world-decal cap — evicting blood in the room the local player is actually standing in.
        ///
        /// Fails OPEN: if no camera can be resolved (early load, unusual spectate state) blood is
        /// placed as before, because silently dropping every effect is far worse than placing one
        /// nobody sees.
        /// </summary>
        private static bool IsWithinViewerRange(Vector3 origin, out float distance)
        {
            distance = -1f;

            float limit = BloodFXBootstrap.MaxViewerDistance?.Value ?? 0f;
            if (limit <= 0f)
                return true;

            Transform viewer = ResolveViewer();
            if (viewer == null)
                return true;

            distance = Vector3.Distance(viewer.position, origin);
            return distance <= limit;
        }

        /// <summary>
        /// Where this client is looking from. StartOfRound.activeCamera is the authoritative
        /// answer because it already tracks spectating and the ship monitor; the rest are
        /// fallbacks for the frames before it is assigned.
        /// </summary>
        private static Transform ResolveViewer()
        {
            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.activeCamera != null)
                    return round.activeCamera.transform;

                PlayerControllerB local = round.localPlayerController;
                if (local != null)
                    return local.gameplayCamera != null ? local.gameplayCamera.transform : local.transform;
            }

            Camera main = Camera.main;
            return main != null ? main.transform : null;
        }

        /// <summary>
        /// <paramref name="scale"/> multiplies the prefabs' authored SIZE (#147), where
        /// <paramref name="intensity"/> and the tier both scale particle COUNT. They are separate
        /// levers: before #147 nothing in this system could change a burst's size at all, so a
        /// "heavier" wound only ever meant more particles in the same volume. Player blood passes
        /// 1f and stays byte-identical to the look that already has an in-game verdict.
        /// </summary>
        private static void PlayBurst(
            Vector3 origin, Vector3 direction, Tier tier, float intensity, Color tint, float scale)
        {
            float multiplier = TierEmission(tier) * intensity;

            BloodFXAssets.PooledParticle burst = BloodFXAssets.TakeBurst();
            burst?.Play(origin, Quaternion.identity, multiplier, tint, scale);

            if (direction.sqrMagnitude <= 0.0001f)
                return;

            BloodFXAssets.PooledParticle spray = BloodFXAssets.TakeSpray();
            spray?.Play(origin, Quaternion.LookRotation(direction, Vector3.up), multiplier, tint, scale);
        }

        // ---------------------------------------------------------- world splatter

        /// <summary>
        /// Fling rays out of a wound and paint what they land on.
        ///
        /// <b>Takes a Transform, not a player (#120).</b> The only thing this ever wanted from the
        /// wounded party was "do not paint the thing that is bleeding", and a creature needs exactly
        /// the same exclusion. <paramref name="sourceRoot"/> may be null, which the creature path
        /// relies on: <see cref="IsRejectedSurface"/> already refuses every <c>EnemyAI</c> outright,
        /// so a monster is excluded by the general rule before the specific one is needed.
        /// </summary>
        private static int SprayEnvironment(
            Transform sourceRoot, Vector3 origin, Vector3 direction, Tier tier, float intensity, Color tint)
        {
            int cap = Mathf.Max(0, BloodFXBootstrap.WorldDecalCap?.Value ?? 48);
            if (cap == 0)
                return 0;

            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;

            int rayCount = TierRayCount(tier);
            int budget = Mathf.Max(1, Mathf.RoundToInt(TierDecalBudget(tier) * intensity));
            int placed = 0;
            int rayHits = 0;
            int rejected = 0;

            bool verbose = BloodFXBootstrap.Verbose;
            if (verbose)
                Diagnostics.Length = 0;

            for (int i = 0; i < rayCount && placed < budget; i++)
            {
                // First two or three rays go down (floor pooling), the rest fan out in a
                // hemisphere biased away from the incoming hit.
                Vector3 rayDirection = i < TierFloorRays(tier)
                    ? Vector3.Slerp(Vector3.down, Random.onUnitSphere, 0.25f)
                    : BiasedHemisphere(direction);

                if (!Physics.Raycast(origin, rayDirection, out _hit, SplatterMaxDistance, mask, QueryTriggerInteraction.Ignore))
                    continue;

                rayHits++;

                if (IsRejectedSurface(_hit.collider, sourceRoot, out string rejectReason))
                {
                    rejected++;
                    if (verbose)
                    {
                        Diagnostics.Append(" {rejected '")
                            .Append(_hit.collider != null ? _hit.collider.name : "<null>")
                            .Append("' reason=").Append(rejectReason).Append('}');
                    }

                    continue;
                }

                bool floor = _hit.normal.y > 0.5f;
                if (!SpawnSurfaceDecal(_hit.point, _hit.normal, tier, tint))
                    continue;

                placed++;

                Collider hitCollider = _hit.collider;

                if (verbose)
                {
                    int layer = hitCollider != null ? hitCollider.gameObject.layer : -1;
                    Diagnostics.Append(" {'")
                        .Append(hitCollider != null ? hitCollider.name : "<null>")
                        .Append("' layer=").Append(layer)
                        .Append('(').Append(layer >= 0 ? LayerMask.LayerToName(layer) : "n/a").Append(')')
                        .Append(" surface=").Append(floor ? "floor" : "wall")
                        .Append(" n=").Append(Format(_hit.normal));
                }

                // Audits the surface we just painted and, unless the config says otherwise,
                // flips it to decal-receiving. This is what makes wall blood visible: interior
                // tile materials ship with Receive Decals off while floors have it on.
                BloodFXDecalSurfaces.EnsureSurfaceReceivesDecals(hitCollider, verbose ? Diagnostics : null);

                if (verbose)
                    Diagnostics.Append('}');
            }

            if (verbose)
            {
                BloodFXBootstrap.Trace(
                    $"splatter tier={tier} rays={rayCount} rayHits={rayHits} rejected={rejected} decals={placed}"
                    + (Diagnostics.Length > 0 ? " ->" + Diagnostics : " -> none"));
            }

            return placed;
        }

        /// <summary>
        /// Wall/floor blood may only land on static room geometry. Held items, any player
        /// (not just the one bleeding) and enemies are all rejected: a decal projector box
        /// on a moving object would smear across it and follow it around the level.
        ///
        /// <b>The enemy reject stays a blanket one, and that is not an oversight.</b> #120 asked
        /// whether it should become material-aware now that creatures bleed; the answer is that this
        /// guard governs WORLD decals, and the reason a monster cannot receive one is not that it is
        /// the wrong material — it is that it walks away. Blood ON a creature is a body-decal
        /// problem (parented, per-species sizing) and is not in this phase.
        /// </summary>
        private static bool IsRejectedSurface(Collider collider, Transform sourceRoot, out string reason)
        {
            if (collider == null)
            {
                reason = "null_collider";
                return true;
            }

            Transform transform = collider.transform;

            if (sourceRoot != null && transform.IsChildOf(sourceRoot))
            {
                reason = "hit_source";
                return true;
            }

            if (collider.GetComponentInParent<GrabbableObject>() != null)
            {
                reason = "grabbable_item";
                return true;
            }

            if (collider.GetComponentInParent<PlayerControllerB>() != null)
            {
                reason = "other_player";
                return true;
            }

            if (collider.GetComponentInParent<EnemyAI>() != null)
            {
                reason = "enemy";
                return true;
            }

            reason = null;
            return false;
        }

        private static Vector3 BiasedHemisphere(Vector3 direction)
        {
            Vector3 random = Random.onUnitSphere;
            if (direction.sqrMagnitude <= 0.0001f)
                return random;

            // Fold the sample into the hemisphere the hit is travelling towards, then relax it
            // back so the spread stays wide rather than a tight cone.
            if (Vector3.Dot(random, direction) < 0f)
                random = Vector3.Reflect(random, direction);

            return Vector3.Slerp(random, direction, 0.35f).normalized;
        }

        private static bool SpawnSurfaceDecal(Vector3 point, Vector3 normal, Tier tier, Color tint)
        {
            bool useSpots = Random.value < TierSpotsWeight(tier);
            GameObject prefab = useSpots ? BloodFXAssets.SpotsPrefab : BloodFXAssets.WallPrefab;
            prefab = prefab != null ? prefab : (useSpots ? BloodFXAssets.WallPrefab : BloodFXAssets.SpotsPrefab);
            if (prefab == null)
                return false;

            Quaternion rotation = ProjectAlong(-normal);
            rotation *= Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.forward);

            GameObject instance = Object.Instantiate(
                prefab,
                point + normal * SurfaceOffset,
                rotation,
                BloodFXAssets.DecalRoot);

            float scale = useSpots ? Random.Range(0.4f, 1.4f) : Random.Range(0.7f, 1.8f);
            ApplyProjectorScale(instance, scale, scale * 0.5f, WorldDecalFrontMargin, DecalLayerEnum.Everything);
            BloodFXTint.ApplyDecalTint(instance, tint);

            // World decals are the known-good case (floor blood is visible), so dumping the
            // first one gives a direct A/B against the body projector description.
            if (!_loggedWorldProjector && BloodFXBootstrap.Verbose)
            {
                _loggedWorldProjector = true;
                BloodFXBootstrap.Trace("first world decal " + DescribeProjector(instance) + ".");
            }

            BloodFXDecalRegistry.RegisterWorldDecal(instance);
            return true;
        }

        private static bool SpawnDeathPuddle(PlayerControllerB player, out Vector3 point, out bool spawned)
        {
            spawned = false;
            point = player != null ? player.transform.position : Vector3.zero;

            GameObject prefab = BloodFXAssets.PuddlePrefab;
            if (prefab == null || player == null)
                return false;

            // Same mask the environment splatter uses, so a puddle can never find a floor the
            // wall decals would have ignored (or vice versa).
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;

            Vector3 origin = player.transform.position + Vector3.up * 0.6f;
            Vector3 normal = Vector3.up;
            bool foundFloor = false;
            if (Physics.Raycast(origin, Vector3.down, out _hit, 4f, mask, QueryTriggerInteraction.Ignore))
            {
                point = _hit.point;
                normal = _hit.normal;
                foundFloor = true;
            }

            Quaternion rotation = ProjectAlong(-normal) * Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.forward);
            GameObject instance = Object.Instantiate(prefab, point + normal * SurfaceOffset, rotation, BloodFXAssets.DecalRoot);
            ApplyProjectorScale(instance, Random.Range(1.4f, 2.2f), 0.6f, WorldDecalFrontMargin, DecalLayerEnum.Everything);
            BloodFXDecalRegistry.RegisterWorldDecal(instance);
            spawned = true;

            return foundFloor;
        }

        // ------------------------------------------------------------- body blood

        private static void AddBodyDecal(PlayerControllerB player, Transform anchor, Vector3 direction, Tier tier)
        {
            int cap = Mathf.Max(0, BloodFXBootstrap.BodyDecalCap?.Value ?? 4);
            if (cap == 0 || !BloodFXAssets.HasBodyPrefabs)
                return;

            int key = player.GetInstanceID();

            // The last slot draws from the same body pool but at full width, so a repeatedly
            // wounded player reads as soaked rather than as four separate spots. It used to
            // use a dedicated "accumulation" prefab; the 2026-07-31 asset audit found that
            // prefab shared M_Decal_BloodPuddle_01 with BloodFX_Puddle_01 — a byte-identical
            // duplicate that stamped a floor-puddle silhouette onto a chest — so it was
            // retired from the bundle rather than kept as a distinct-looking asset it was not.
            bool final = BloodFXDecalRegistry.BodyCount(key) >= cap - 1;
            GameObject prefab = BloodFXAssets.BodyPrefab;
            if (prefab == null)
                return;

            // One-time "can this suit even show blood" audit, and the decal-layer grant when
            // the pipeline supports it.
            BloodFXDecalSurfaces.AuditPlayerMaterials(player);
            BloodFXDecalSurfaces.EnsurePlayerDecalLayer(player);

            // Face the wound outwards along the hit direction when we have one, otherwise
            // pick a random horizontal facing on the torso.
            Vector3 outward = direction.sqrMagnitude > 0.0001f
                ? new Vector3(-direction.x, 0f, -direction.z)
                : Vector3.zero;
            if (outward.sqrMagnitude <= 0.0001f)
                outward = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));
            if (outward.sqrMagnitude <= 0.0001f)
                outward = Vector3.forward;
            outward.Normalize();

            Vector3 surface = anchor.position
                              + outward * BodyDecalRadius
                              + Vector3.up * Random.Range(-0.16f, 0.12f);

            Quaternion rotation = ProjectAlong(-outward) * Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.forward);

            GameObject instance = Object.Instantiate(prefab, surface, rotation, anchor);
            float size = final
                ? BodyDecalMaxWidth * BodyDecalHeavyScale
                : Random.Range(BodyDecalMinWidth, BodyDecalMaxWidth)
                  * (tier == Tier.Heavy ? BodyDecalHeavyScale : 1f);

            // Default decal layer unless the escape-hatch config is on. DecalLayer7 was tried
            // first and produced invisible body blood: the HDRP asset reports
            // supportDecalLayers=true but the shipped shaders carry no DECAL_LAYERS variants,
            // so a non-default layer mask means "draw nothing".
            DecalLayerEnum layer = BloodFXDecalSurfaces.BodyDecalLayerIsolationActive
                ? BloodFXDecalSurfaces.BodyDecalLayer
                : DecalLayerEnum.Everything;
            ApplyProjectorScale(instance, size, BodyDecalDepth, BodyDecalFrontMargin, layer);

            BloodFXDecalRegistry.RegisterBodyDecal(key, instance);

            BloodFXBootstrap.Trace(
                $"body decal player='{BloodFXDecalSurfaces.SafeName(player)}' bone='{anchor.name}' "
                + $"pos={Format(surface)} prefab='{prefab.name}' "
                + $"size={size:0.###}x{size:0.###}x{BodyDecalDepth:0.###} frontMargin={BodyDecalFrontMargin:0.###} "
                + $"decalLayer={layer} path={BloodFXDecalSurfaces.ActivePathName} "
                + $"count={BloodFXDecalRegistry.BodyCount(key)}/{cap} "
                + DescribeProjector(instance) + ".");
        }

        // --------------------------------------------------------- #374 phase 0 gate probe

        /// <summary>
        /// Place one body decal on an arbitrary bone, bypassing every gate the real body-decal
        /// path applies — no damage event, no rate limit, no <see cref="PlayerControllerB"/> —
        /// so the one question "can this surface show an HDRP decal at all" is answerable in a
        /// single in-game run. #374 gates the whole Bundy half of the issue on that answer.
        ///
        /// This is NOT the creature body-blood path and must not grow into one. That path is
        /// #374 Phase 1/2, goes through the generalized primitive lifted out of
        /// <see cref="AddBodyDecal"/>, and carries its own channel, cap and replication.
        ///
        /// Registered on the body FIFO under <paramref name="ownerKey"/> — which is already a
        /// plain int, not a player — so repeated probes cap and dissolve like any other body
        /// decal and round-end teardown collects them, instead of needing a lifetime of their own.
        ///
        /// <paramref name="standoff"/> and <paramref name="depth"/> are deliberately generous
        /// next to the player torso constants above. Those are tuned to hug a known-radius suit
        /// and project inward only; a probe has to straddle a bone whose surface radius is not
        /// known, on a creature authored much larger than a player, so it spans well outside and
        /// well past the bone. Painting the far side of a limb is an acceptable cost for a
        /// probe whose only job is yes/no.
        /// </summary>
        public static GameObject PlaceGateProbeDecal(
            int ownerKey,
            Transform anchor,
            Vector3 outward,
            float size,
            float standoff,
            float depth,
            string label)
        {
            if (anchor == null || !BloodFXAssets.HasBodyPrefabs)
                return null;

            GameObject prefab = BloodFXAssets.BodyPrefab;
            if (prefab == null)
                return null;

            // RegisterBodyDecal destroys outright at cap 0, and Object.Destroy defers to the end
            // of the frame, so the instance would still test non-null here and this would report
            // a decal it had just thrown away. Refuse instead: a probe that reports success and
            // shows nothing is the exact failure this gate exists to rule out.
            int cap = Mathf.Max(0, BloodFXBootstrap.CreatureBodyDecalCap?.Value ?? 10);
            if (cap == 0)
            {
                BloodFXBootstrap.Log?.LogWarning(
                    $"[Y4NGZCompany.BloodFX] gate probe decal '{label}' refused: "
                    + "'Creature Body Decal Cap' is 0, so any decal placed would be destroyed on registration.");
                return null;
            }

            long channelKey = BloodFXDecalRegistry.ChannelFor(ownerKey, BodyChannel.Creature);

            Vector3 facing = outward;
            facing.y = 0f;
            if (facing.sqrMagnitude <= 0.0001f)
                facing = Vector3.forward;
            facing.Normalize();

            // No random roll or size jitter here on purpose: a probe that looks the same every
            // time is one fewer variable when the answer is "I think I saw something".
            Vector3 surface = anchor.position + facing * standoff;
            GameObject instance = Object.Instantiate(prefab, surface, ProjectAlong(-facing), anchor);
            ApplyProjectorScale(instance, size, depth, BodyDecalFrontMargin, DecalLayerEnum.Everything);
            BloodFXDecalRegistry.RegisterBodyDecal(channelKey, instance, cap);

            // Unconditional, like the HDRP asset probe: this is an explicitly requested one-shot
            // diagnostic, and a gate answer that only prints when a second debug flag happens to
            // be on is how the last round of this question went unanswered.
            BloodFXBootstrap.Log?.LogInfo(
                $"[Y4NGZCompany.BloodFX] gate probe decal '{label}' bone='{anchor.name}' "
                + $"pos={Format(surface)} facing={Format(facing)} prefab='{prefab.name}' "
                + $"size={size:0.###} standoff={standoff:0.###} depth={depth:0.###} "
                + $"count={BloodFXDecalRegistry.BodyCount(channelKey)} "
                + DescribeProjector(instance) + ".");

            return instance;
        }

        // ------------------------------------------------------------------ utils

        /// <summary>HDRP DecalProjectors project along +Z, so aim +Z down the projection axis.</summary>
        internal static Quaternion ProjectAlong(Vector3 projectionAxis)
        {
            Vector3 forward = projectionAxis.sqrMagnitude > 0.0001f ? projectionAxis.normalized : Vector3.down;
            Vector3 up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>
        /// Size and place a projector's volume. HDRP centres the box on `pivot` (default zero,
        /// i.e. straddling the transform), so a naive size assignment leaves half the depth
        /// standing in FRONT of the spawn point — which is what painted the held revolver.
        /// Pushing the pivot forward by half the depth (less <paramref name="frontMargin"/>)
        /// makes the box start at the spawn point and run inward only.
        /// </summary>
        internal static void ApplyProjectorScale(
            GameObject instance,
            float width,
            float depth,
            float frontMargin,
            DecalLayerEnum decalLayerMask)
        {
            if (instance == null)
                return;

            var projector = instance.GetComponent<DecalProjector>();
            if (projector == null)
                projector = instance.GetComponentInChildren<DecalProjector>(true);
            if (projector == null)
                return;

            float clampedDepth = Mathf.Max(0.05f, depth);
            projector.size = new Vector3(width, width, clampedDepth);
            projector.pivot = new Vector3(0f, 0f, clampedDepth * 0.5f - Mathf.Min(frontMargin, clampedDepth * 0.5f));

            // Bone and prop hierarchies carry arbitrary scales; the sizes above are world
            // metres and must stay that way.
            projector.scaleMode = DecalScaleMode.ScaleInvariant;
            projector.decalLayerMask = decalLayerMask;

            // DecalSystem only skips the angle-fade maths when startAngleFade == 180 exactly
            // (see DecalSystem.UpdateCachedData). Anything else authored on the prefab would
            // fade blood out by surface angle, which is never what we want here — we already
            // align the projector to the surface normal ourselves.
            projector.startAngleFade = 180f;
            projector.endAngleFade = 180f;
            projector.fadeFactor = 1f;
        }

        private static Transform ResolveTorsoAnchor(PlayerControllerB player)
        {
            if (player == null)
                return null;

            if (player.upperSpine != null)
                return player.upperSpine;
            if (player.lowerSpine != null)
                return player.lowerSpine;
            if (player.playerBodyAnimator != null)
                return player.playerBodyAnimator.transform;

            return player.transform;
        }

        public static string Format(Vector3 value)
        {
            return $"({value.x:0.##},{value.y:0.##},{value.z:0.##})";
        }

        /// <summary>Distance is -1 when the proximity gate is off or no camera resolved.</summary>
        private static string FormatDistance(float distance)
        {
            return distance < 0f ? "n/a" : distance.ToString("0.#") + "m";
        }

        /// <summary>
        /// Reports what the spawned projector actually ended up as. DecalProjector.IsValid()
        /// is the cheap answer to "is this prefab's material even a usable decal material" —
        /// the other candidate explanation for a decal that logs fine and draws nothing.
        /// </summary>
        internal static string DescribeProjector(GameObject instance)
        {
            if (instance == null)
                return "projector=<no instance>";

            var projector = instance.GetComponent<DecalProjector>();
            if (projector == null)
                projector = instance.GetComponentInChildren<DecalProjector>(true);
            if (projector == null)
                return "projector=<none on prefab>";

            Material material = projector.material;
            return $"projector[valid={projector.IsValid()} "
                   + $"mat='{(material != null ? material.name : "<null>")}' "
                   + $"shader='{(material != null && material.shader != null ? material.shader.name : "<null>")}' "
                   + $"size={Format(projector.size)} pivot={Format(projector.pivot)} "
                   + $"drawDist={projector.drawDistance:0.#} fade={projector.fadeFactor:0.##} "
                   + $"scaleMode={projector.scaleMode} decalLayers={projector.decalLayerMask} "
                   + $"enabled={projector.enabled && projector.gameObject.activeInHierarchy}]";
        }

        // ------------------------------------------------------------------ tiers

        private enum Tier
        {
            Small,
            Medium,
            Heavy
        }

        private static Tier GetTier(int damage)
        {
            if (damage < 15)
                return Tier.Small;
            return damage <= 40 ? Tier.Medium : Tier.Heavy;
        }

        private static float TierEmission(Tier tier)
        {
            switch (tier)
            {
                case Tier.Small: return 0.6f;
                case Tier.Medium: return 1f;
                default: return 1.8f;
            }
        }

        private static int TierRayCount(Tier tier)
        {
            switch (tier)
            {
                case Tier.Small: return 6;
                case Tier.Medium: return 8;
                default: return 10;
            }
        }

        private static int TierFloorRays(Tier tier)
        {
            return tier == Tier.Heavy ? 3 : 2;
        }

        private static int TierDecalBudget(Tier tier)
        {
            switch (tier)
            {
                case Tier.Small: return 2;
                case Tier.Medium: return 4;
                default: return 7;
            }
        }

        private static float TierSpotsWeight(Tier tier)
        {
            switch (tier)
            {
                case Tier.Small: return 0.85f;
                case Tier.Medium: return 0.5f;
                default: return 0.2f;
            }
        }
    }
}
