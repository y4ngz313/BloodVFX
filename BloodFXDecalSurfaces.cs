using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Controls WHICH surfaces this system's decals are allowed to land on.
    ///
    /// An HDRP DecalProjector paints every decal-receiving surface inside its box, so the
    /// torso projector caught the revolver held in front of the chest during the first
    /// in-game test. Two mechanisms fix that, and this class owns both:
    ///
    ///   (a) Decal layers — only usable when the game's HDRP asset has supportDecalLayers
    ///       on. Body projectors move to DecalLayer7 and only the damaged player's body
    ///       renderers get that bit in their renderingLayerMask. Surgical; nothing else in
    ///       the volume can receive.
    ///   (b) Material-level opt-out — HDRP Lit exposes `_SupportDecals`; clearing it (plus
    ///       the `_DISABLE_DECALS` keyword and moving the material out of the 2225..2500
    ///       "opaque decal" render-queue range HDRP uses to build its decal depth-prepass
    ///       list) makes a surface stop receiving decals entirely. Applied to every
    ///       GrabbableObject material. Materials are shared assets, so one marking covers
    ///       every instance — which is exactly what we want: items should never receive
    ///       blood, or any other decal.
    ///
    /// RESOLVED 2026-07-30 by in-game test — (a) IS UNUSABLE IN THIS BUILD, and the static
    /// and runtime evidence only look contradictory:
    ///   - Runtime probe reports supportDecalLayers=TRUE on the live HDRP asset.
    ///   - Static scan of the shipped v81 data finds the decal-layer shader keyword string
    ///     "DECAL_LAYERS" in ZERO of globalgamemanagers*, level*, sharedassets*,
    ///     resources.assets — every decal-layer shader variant was stripped at build time.
    /// An asset flag with no shader variants behind it means a projector on a non-default
    /// decal layer draws NOTHING: body decals placed on DecalLayer7 were logged, counted,
    /// and completely invisible, even though the suit audited as receivesDecals=true.
    /// So body decals now use the default layer and (b) is the sole protection for items.
    /// (a) survives behind the off-by-default "Isolate Body Decals On Decal Layer" config.
    /// Do not trust supportDecalLayers here; trust the keyword scan.
    ///
    /// "_DISABLE_DECALS" by contrast IS present (globalgamemanagers.assets,
    /// sharedassets1.assets, sharedassets3.assets), so that shader_feature variant is in the
    /// build and toggling the keyword at runtime — in either direction — takes effect. That
    /// is what makes both (b) and its inverse (EnsureSurfaceReceivesDecals, which turns
    /// decal reception ON for interior wall materials that shipped with it off) work.
    /// </summary>
    public static class BloodFXDecalSurfaces
    {
        /// <summary>
        /// HDRP packs decal layers into bits 0..7 of Renderer.renderingLayerMask and light
        /// layers into bits 8..15 (HDRenderPipeline sets defaultRenderingLayerMask = 257,
        /// i.e. bit0 | bit8). DecalLayerEnum.DecalLayer7 is bit 7.
        /// </summary>
        private const uint BodyDecalLayerBit = 1u << 7;

        internal const DecalLayerEnum BodyDecalLayer = DecalLayerEnum.DecalLayer7;

        // HDRenderQueue boundaries (Unity.RenderPipelines.HighDefinition.Runtime, v14):
        //   2000 opaque             2225 opaque + receives decals
        //   2450 opaque alpha-test  2475 opaque alpha-test + receives decals
        private const int QueueOpaque = 2000;
        private const int QueueOpaqueDecal = 2225;
        private const int QueueOpaqueAlphaTest = 2450;
        private const int QueueOpaqueAlphaTestDecal = 2475;
        private const int QueueOpaqueMax = 2500;

        private static readonly int SupportDecalsId = Shader.PropertyToID("_SupportDecals");
        private const string DisableDecalsKeyword = "_DISABLE_DECALS";

        private static readonly HashSet<int> ShieldedMaterials = new HashSet<int>();
        private static readonly HashSet<int> ReceptiveMaterials = new HashSet<int>();
        private static readonly HashSet<int> AuditedPlayers = new HashSet<int>();
        private static readonly HashSet<int> DecalLayeredPlayers = new HashSet<int>();

        private static bool _probed;
        private static bool _decalLayersSupported;
        private static bool _itemPrefabsSwept;
        private static int _shieldedMaterialCount;
        private static int _receptiveMaterialCount;

        /// <summary>What the live HDRP asset claims. See the class remarks: it lies in this build.</summary>
        internal static bool DecalLayersSupported
        {
            get
            {
                Probe();
                return _decalLayersSupported;
            }
        }

        /// <summary>
        /// Whether body decals should be isolated onto their own decal layer. Needs BOTH the
        /// pipeline flag and the opt-in config, because the flag alone proved untrustworthy:
        /// the shipped shaders have no DECAL_LAYERS variants, so layered decals never draw.
        /// </summary>
        internal static bool BodyDecalLayerIsolationActive =>
            (BloodFXBootstrap.IsolateBodyDecalsOnDecalLayer?.Value ?? false) && DecalLayersSupported;

        internal static string ActivePathName =>
            BodyDecalLayerIsolationActive ? "decalLayers+materials" : "materials";

        // ------------------------------------------------------------------ probe

        /// <summary>
        /// One-time read of the live HDRP asset. Logged unconditionally (not behind Debug
        /// Logging) because the answer decides which anti-bleed path is even possible.
        /// </summary>
        internal static void Probe()
        {
            if (_probed)
                return;

            _probed = true;

            var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (asset == null)
            {
                BloodFXBootstrap.Log?.LogWarning(
                    "[Y4NGZCompany.BloodFX] HDRP asset probe: GraphicsSettings.currentRenderPipeline is not an "
                    + "HDRenderPipelineAsset; assuming decal layers unsupported (material path only).");
                return;
            }

            RenderPipelineSettings settings = asset.currentPlatformRenderPipelineSettings;
            _decalLayersSupported = settings.supportDecals && settings.supportDecalLayers;

            BloodFXBootstrap.Log?.LogInfo(
                "[Y4NGZCompany.BloodFX] HDRP asset probe: "
                + $"asset='{asset.name}', supportDecals={settings.supportDecals}, "
                + $"supportDecalLayers={settings.supportDecalLayers}, "
                + $"defaultRenderingLayerMask={GraphicsSettings.defaultRenderingLayerMask}, "
                + $"anti-bleed path='{(_decalLayersSupported ? "decalLayers+materials" : "materials")}'.");
        }

        // ------------------------------------------------------- material shielding

        /// <summary>Stop one material receiving decals. Returns true if this call changed it.</summary>
        private static bool ShieldMaterial(Material material)
        {
            if (material == null)
                return false;
            if (!ShieldedMaterials.Add(material.GetInstanceID()))
                return false;

            bool touched = false;

            if (material.HasProperty(SupportDecalsId) && material.GetFloat(SupportDecalsId) > 0f)
            {
                material.SetFloat(SupportDecalsId, 0f);
                touched = true;
            }

            if (!material.IsKeywordEnabled(DisableDecalsKeyword))
            {
                material.EnableKeyword(DisableDecalsKeyword);
                touched = true;
            }

            // Move out of HDRP's decal render-queue ranges, preserving the sort offset.
            // Deliberately NOT via HDMaterial.ValidateMaterial: that wipes shaderKeywords and
            // rebuilds them from properties, which is a much larger blast radius on shared
            // game materials than the three edits we actually need.
            int queue = material.renderQueue;
            if (queue >= QueueOpaqueAlphaTestDecal && queue <= QueueOpaqueMax)
            {
                material.renderQueue = Mathf.Clamp(
                    QueueOpaqueAlphaTest + (queue - QueueOpaqueAlphaTestDecal),
                    QueueOpaqueAlphaTest,
                    QueueOpaqueAlphaTestDecal - 1);
                touched = true;
            }
            else if (queue >= QueueOpaqueDecal && queue < QueueOpaqueAlphaTest)
            {
                material.renderQueue = QueueOpaque + (queue - QueueOpaqueDecal);
                touched = true;
            }

            if (touched)
                _shieldedMaterialCount++;

            return touched;
        }

        /// <summary>Shield every renderer material under <paramref name="root"/>. Cheap after the first pass.</summary>
        internal static int ShieldHierarchy(GameObject root)
        {
            if (root == null)
                return 0;

            int changed = 0;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Material[] materials = renderer.sharedMaterials;
                if (materials == null)
                    continue;

                for (int m = 0; m < materials.Length; m++)
                {
                    if (ShieldMaterial(materials[m]))
                        changed++;
                }
            }

            return changed;
        }

        /// <summary>
        /// Single pass over StartOfRound's item registry. Item instances share their prefab's
        /// materials, so this covers every item that will ever spawn this session.
        /// </summary>
        internal static void SweepItemPrefabs()
        {
            if (_itemPrefabsSwept)
                return;

            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.allItemsList == null || round.allItemsList.itemsList == null)
                return;

            _itemPrefabsSwept = true;

            int items = 0;
            int changed = 0;
            List<Item> list = round.allItemsList.itemsList;
            for (int i = 0; i < list.Count; i++)
            {
                Item item = list[i];
                if (item == null || item.spawnPrefab == null)
                    continue;

                items++;
                changed += ShieldHierarchy(item.spawnPrefab);
            }

            BloodFXBootstrap.Log?.LogInfo(
                $"[Y4NGZCompany.BloodFX] Item decal shield: swept {items} item prefabs, "
                + $"{changed} materials marked no-decals (total shielded={_shieldedMaterialCount}).");
        }

        // ------------------------------------------- room surfaces (inverse of shield)

        /// <summary>
        /// Audit — and, when "Enable Decals On Room Materials" is on, fix — the surface a
        /// world blood decal just landed on.
        ///
        /// Floors showed blood and walls never did, from identical placement code, because
        /// the interior wall/tile materials ship with HDRP's "Receive Decals" cleared. This
        /// is the exact inverse of ShieldMaterial: set _SupportDecals, drop _DISABLE_DECALS,
        /// and move the render queue back INTO HDRP's 2225..2500 decal band so the surface
        /// rejoins the decal depth-prepass renderer list.
        ///
        /// Only ever reached for colliders that already passed the item/player/enemy
        /// rejection filter, i.e. static room geometry, and shielded item materials are
        /// skipped explicitly so the two systems can never fight over one material.
        /// </summary>
        internal static void EnsureSurfaceReceivesDecals(Collider collider, StringBuilder verbose)
        {
            if (collider == null)
                return;

            Renderer renderer = ResolveSurfaceRenderer(collider);
            if (renderer == null)
            {
                verbose?.Append(" surfaceMat=<no renderer>");
                return;
            }

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                verbose?.Append(" surfaceMat=<none>");
                return;
            }

            bool allowFix = BloodFXBootstrap.EnableDecalsOnRoomMaterials?.Value ?? true;

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null)
                    continue;

                if (verbose != null)
                    AppendMaterialAudit(verbose, " surfaceMat", material);

                if (!allowFix)
                    continue;

                MakeMaterialReceiveDecals(material, verbose);
            }
        }

        /// <summary>
        /// A raycast hit reports the collider, not the renderer. Tile geometry usually carries
        /// both on the same GameObject; fall back outward then inward for split hierarchies.
        /// </summary>
        private static Renderer ResolveSurfaceRenderer(Collider collider)
        {
            Renderer renderer = collider.GetComponent<Renderer>();
            if (renderer != null)
                return renderer;

            renderer = collider.GetComponentInParent<Renderer>();
            if (renderer != null)
                return renderer;

            return collider.GetComponentInChildren<Renderer>();
        }

        private static void MakeMaterialReceiveDecals(Material material, StringBuilder verbose)
        {
            // Never un-shield an item: the shield is the only thing keeping blood off held props.
            if (ShieldedMaterials.Contains(material.GetInstanceID()))
            {
                verbose?.Append(" [skipped: item-shielded]");
                return;
            }

            if (!ReceptiveMaterials.Add(material.GetInstanceID()))
                return;

            if (!ApplyDecalReception(material))
                return;

            _receptiveMaterialCount++;
            verbose?.Append(" [FIXED -> receives decals, queue=").Append(material.renderQueue).Append(']');

            BloodFXBootstrap.Trace(
                $"room material '{material.name}' (shader '{(material.shader != null ? material.shader.name : "<null>")}') "
                + $"switched to decal-receiving; queue={material.renderQueue}, total fixed={_receptiveMaterialCount}.");
        }

        /// <summary>
        /// The three writes that make one material decal-receiving, and nothing else — no dedupe
        /// set, no counter, no logging. Returns true when this call actually changed something.
        ///
        /// The bookkeeping lives in the callers because the two callers need opposite bookkeeping:
        /// a shared room material must be touched once ever, a per-instance copy must be touched
        /// every single time. See <see cref="MakeInstanceMaterialReceiveDecals"/>.
        /// </summary>
        private static bool ApplyDecalReception(Material material)
        {
            bool touched = false;

            if (material.HasProperty(SupportDecalsId) && material.GetFloat(SupportDecalsId) <= 0f)
            {
                material.SetFloat(SupportDecalsId, 1f);
                touched = true;
            }

            if (material.IsKeywordEnabled(DisableDecalsKeyword))
            {
                material.DisableKeyword(DisableDecalsKeyword);
                touched = true;
            }

            // Move back into the decal band, preserving the sort offset and the alpha-test
            // bucket the material was already in. Note what is NOT handled: a transparent
            // material (queue > QueueOpaqueMax) matches neither branch and is left exactly
            // where it is. That is deliberate — a render-queue write alone would not turn an
            // alpha-blended material opaque, it would only sort it wrongly — but it means a
            // caller that ignores the return of ClassifyDecalReception gets a silent no-op.
            int queue = material.renderQueue;
            if (queue >= QueueOpaque && queue < QueueOpaqueDecal)
            {
                material.renderQueue = QueueOpaqueDecal + (queue - QueueOpaque);
                touched = true;
            }
            else if (queue >= QueueOpaqueAlphaTest && queue < QueueOpaqueAlphaTestDecal)
            {
                material.renderQueue = Mathf.Min(
                    QueueOpaqueAlphaTestDecal + (queue - QueueOpaqueAlphaTest),
                    QueueOpaqueMax);
                touched = true;
            }

            return touched;
        }

        // -------------------------------------------- per-instance copies (creature skin)

        /// <summary>How HDRP will treat one material as a decal RECEIVER.</summary>
        public enum DecalReception
        {
            /// <summary>
            /// Decals enabled and the render queue sits inside HDRP's 2225..2500 opaque decal
            /// band, so the surface is in the decal depth-prepass renderer list. The only path
            /// this build is known to actually draw.
            /// </summary>
            OpaqueDecalBand,

            /// <summary>
            /// Decals enabled but the material is transparent (queue > 2500). HDRP routes those
            /// through the forward transparent decal path rather than the opaque band, and that
            /// path has never been shown to draw here. Unproven rather than failed — and nothing
            /// in <see cref="ApplyDecalReception"/> can move it.
            /// </summary>
            Transparent,

            /// <summary>
            /// _SupportDecals cleared, _DISABLE_DECALS set, or opaque and below 2225. No decal
            /// can land on this surface.
            /// </summary>
            Blocked,
        }

        /// <summary>
        /// The single decal-reception verdict, shared by the audit line and by the per-instance
        /// fixer so the two can never disagree about what "receives decals" means.
        /// </summary>
        internal static DecalReception ClassifyDecalReception(Material material, out int queue)
        {
            queue = 0;
            if (material == null)
                return DecalReception.Blocked;

            queue = material.renderQueue;

            if (material.HasProperty(SupportDecalsId) && material.GetFloat(SupportDecalsId) <= 0f)
                return DecalReception.Blocked;
            if (material.IsKeywordEnabled(DisableDecalsKeyword))
                return DecalReception.Blocked;
            if (queue > QueueOpaqueMax)
                return DecalReception.Transparent;

            return queue >= QueueOpaqueDecal ? DecalReception.OpaqueDecalBand : DecalReception.Blocked;
        }

        /// <summary>
        /// Force one PER-INSTANCE material copy to receive decals, and append its verdict to
        /// <paramref name="verbose"/>. This is how a creature's own skin is made paintable
        /// (#374 Phase 0) — always on the copies <c>Renderer.materials</c> hands out, never on
        /// the shared bundle asset, which every other spawn and the pack itself are reading.
        ///
        /// Deliberately shares none of <see cref="MakeMaterialReceiveDecals"/>'s bookkeeping:
        ///
        ///   - No <see cref="ReceptiveMaterials"/> dedupe. That set is never cleared and Unity
        ///     recycles instance IDs, so the fresh copy made for a second spawn can collide with
        ///     a destroyed one's ID. On a shared room material a redundant skip costs nothing;
        ///     on a per-instance copy it means that spawn is silently never made receptive.
        ///   - No <see cref="ShieldedMaterials"/> check, for the same recycled-ID reason, and
        ///     because these copies are minted by this plugin from a known creature material.
        ///     There is no held prop to protect here, and a false positive would be the same
        ///     silent skip in a different disguise.
        ///
        /// Act on the returned verdict rather than assuming success:
        /// <see cref="DecalReception.Transparent"/> is a real outcome the writes cannot fix.
        /// </summary>
        public static DecalReception MakeInstanceMaterialReceiveDecals(Material material, StringBuilder verbose)
        {
            if (material == null)
            {
                verbose?.Append(" <null mat>");
                return DecalReception.Blocked;
            }

            bool changed = ApplyDecalReception(material);
            DecalReception reception = ClassifyDecalReception(material, out int queue);

            if (verbose != null)
            {
                AppendMaterialAudit(verbose, " |", material);
                verbose.Append(changed ? " [FIXED]" : " [already set]");

                // Never report this as a bare boolean: the two failure modes need different
                // answers, and both of them look identical from "receivesDecals=false".
                if (reception == DecalReception.Transparent)
                {
                    verbose.Append(" [UNPROVEN: transparent, queue=").Append(queue)
                        .Append(" is above the ").Append(QueueOpaqueDecal).Append("..").Append(QueueOpaqueMax)
                        .Append(" opaque decal band and cannot be moved into it]");
                }
                else if (reception == DecalReception.Blocked)
                {
                    verbose.Append(" [BLOCKED: queue=").Append(queue)
                        .Append(" outside ").Append(QueueOpaqueDecal).Append("..").Append(QueueOpaqueMax)
                        .Append(", or decals still disabled on the material]");
                }
            }

            return reception;
        }

        // ---------------------------------------------------------- decal layers (a)

        /// <summary>
        /// Give the damaged player's body renderers the body decal layer bit so a
        /// layer-restricted projector can still reach the suit. No-op when the pipeline
        /// does not support decal layers.
        /// </summary>
        internal static void EnsurePlayerDecalLayer(PlayerControllerB player)
        {
            if (player == null || !BodyDecalLayerIsolationActive)
                return;
            if (!DecalLayeredPlayers.Add(player.GetInstanceID()))
                return;

            ApplyLayerBit(player.thisPlayerModel);
            ApplyLayerBit(player.thisPlayerModelLOD1);
            ApplyLayerBit(player.thisPlayerModelLOD2);

            BloodFXBootstrap.Trace(
                $"decal layer: player='{SafeName(player)}' body renderers now include DecalLayer7 (bit 0x80).");
        }

        private static void ApplyLayerBit(Renderer renderer)
        {
            if (renderer == null)
                return;

            renderer.renderingLayerMask |= BodyDecalLayerBit;
        }

        internal static void ResetPlayerState()
        {
            AuditedPlayers.Clear();
            DecalLayeredPlayers.Clear();
        }

        // ------------------------------------------------------------- audit (task 1)

        /// <summary>
        /// One-time enumeration of a player's SkinnedMeshRenderers on their first body decal.
        /// Answers "can the suit show blood at all" without needing to see it.
        /// </summary>
        internal static void AuditPlayerMaterials(PlayerControllerB player)
        {
            if (player == null || !BloodFXBootstrap.Verbose)
                return;
            if (!AuditedPlayers.Add(player.GetInstanceID()))
                return;

            var builder = new StringBuilder(512);
            builder.Append("material audit: player='").Append(SafeName(player)).Append("' ");

            AppendRendererAudit(builder, "thisPlayerModel", player.thisPlayerModel);
            AppendRendererAudit(builder, "LOD1", player.thisPlayerModelLOD1);
            AppendRendererAudit(builder, "LOD2", player.thisPlayerModelLOD2);
            AppendRendererAudit(builder, "arms", player.thisPlayerModelArms);

            BloodFXBootstrap.Trace(builder.ToString());
        }

        private static void AppendRendererAudit(StringBuilder builder, string label, Renderer renderer)
        {
            builder.Append('[').Append(label).Append(": ");
            if (renderer == null)
            {
                builder.Append("null] ");
                return;
            }

            builder.Append("renderingLayerMask=0x").Append(renderer.renderingLayerMask.ToString("X"));

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                builder.Append(" no materials] ");
                return;
            }

            for (int i = 0; i < materials.Length; i++)
                AppendMaterialAudit(builder, " |", materials[i]);

            builder.Append("] ");
        }

        /// <summary>
        /// The single material verdict format, shared by the suit audit and the world-surface
        /// audit so the two can be compared line for line.
        /// </summary>
        private static void AppendMaterialAudit(StringBuilder builder, string label, Material material)
        {
            builder.Append(label);

            if (material == null)
            {
                builder.Append(" <null mat>");
                return;
            }

            bool hasProperty = material.HasProperty(SupportDecalsId);
            float supportDecals = hasProperty ? material.GetFloat(SupportDecalsId) : -1f;
            bool disabledKeyword = material.IsKeywordEnabled(DisableDecalsKeyword);
            int queue = material.renderQueue;
            bool opaque = queue <= QueueOpaqueMax;
            bool inDecalQueue = queue >= QueueOpaqueDecal && queue <= QueueOpaqueMax;

            // An opaque surface must ALSO sit in HDRP's decal queue band, or it is left out of
            // the decal depth-prepass renderer list and no decal can reach it whatever the
            // keyword says. That is the wall-vs-floor discriminator. Taken from the shared
            // classifier so this line and the per-instance fixer cannot drift apart.
            bool receives = ClassifyDecalReception(material, out _) != DecalReception.Blocked;

            builder.Append(" '").Append(material.name).Append("' shader='")
                .Append(material.shader != null ? material.shader.name : "<null>")
                .Append("' _SupportDecals=").Append(hasProperty ? supportDecals.ToString("0.##") : "n/a")
                .Append(" DECALS_OFF=").Append(disabledKeyword)
                .Append(" queue=").Append(queue)
                .Append(opaque ? (inDecalQueue ? "(decal range)" : "(no-decal range)") : "(transparent)")
                .Append(" receivesDecals=").Append(receives);
        }

        internal static string SafeName(PlayerControllerB player)
        {
            if (player == null)
                return "<null>";

            return string.IsNullOrEmpty(player.playerUsername)
                ? "player" + player.playerClientId.ToString()
                : player.playerUsername;
        }
    }

    /// <summary>
    /// Hooks that keep item materials out of the decal receiver set. The StartOfRound sweep
    /// is the bulk pass; GrabItemOnClient is the safety net for anything spawned outside the
    /// registry (modded items, enemy-dropped props).
    /// </summary>
    [HarmonyPatch]
    internal static class BloodFXDecalShieldPatches
    {
        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPostfix]
        private static void StartOfRoundStartPostfix()
        {
            BloodFXDecalSurfaces.Probe();
            BloodFXDecalSurfaces.SweepItemPrefabs();
        }

        [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.GrabItemOnClient))]
        [HarmonyPostfix]
        private static void GrabItemOnClientPostfix(GrabbableObject __instance)
        {
            if (__instance == null)
                return;

            int changed = BloodFXDecalSurfaces.ShieldHierarchy(__instance.gameObject);
            if (changed > 0)
                BloodFXBootstrap.Trace($"item shield: '{__instance.name}' -> {changed} materials marked no-decals.");
        }

        [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.EquipItem))]
        [HarmonyPostfix]
        private static void EquipItemPostfix(GrabbableObject __instance)
        {
            if (__instance == null)
                return;

            BloodFXDecalSurfaces.ShieldHierarchy(__instance.gameObject);
        }
    }
}
