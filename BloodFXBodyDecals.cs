using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Shared geometry and lifetime primitive for blood decals attached to an articulated body.
    /// Callers own material preparation, choose an isolated registry channel, and supply geometry
    /// appropriate to the target bone.
    /// </summary>
    public static class BloodFXBodyDecals
    {
        public readonly struct Profile
        {
            public readonly float Radius;
            public readonly float MinWidth;
            public readonly float MaxWidth;
            public readonly float Depth;
            public readonly float FrontMargin;
            public readonly float HeavyScale;

            public Profile(
                float radius,
                float minWidth,
                float maxWidth,
                float depth,
                float frontMargin,
                float heavyScale)
            {
                Radius = radius;
                MinWidth = minWidth;
                MaxWidth = maxWidth;
                Depth = depth;
                FrontMargin = frontMargin;
                HeavyScale = heavyScale;
            }
        }

        internal static GameObject Place(
            long channelKey,
            Transform anchor,
            Vector3 direction,
            in Profile profile,
            bool final,
            int cap,
            string label)
        {
            cap = Mathf.Max(0, cap);
            if (cap == 0 || anchor == null || !BloodFXAssets.HasBodyPrefabs)
                return null;

            GameObject prefab = BloodFXAssets.BodyPrefab;
            if (prefab == null)
                return null;

            // Face the wound outwards along the hit direction when we have one, otherwise
            // pick a random horizontal facing on the target bone.
            Vector3 outward = direction.sqrMagnitude > 0.0001f
                ? new Vector3(-direction.x, 0f, -direction.z)
                : Vector3.zero;
            if (outward.sqrMagnitude <= 0.0001f)
                outward = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));
            if (outward.sqrMagnitude <= 0.0001f)
                outward = Vector3.forward;
            outward.Normalize();

            Vector3 surface = anchor.position
                              + outward * profile.Radius
                              + Vector3.up * Random.Range(-0.16f, 0.12f);

            Quaternion rotation = BloodFXDirector.ProjectAlong(-outward)
                                  * Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.forward);

            GameObject instance = Object.Instantiate(prefab, surface, rotation, anchor);
            float size = final
                ? profile.MaxWidth * profile.HeavyScale
                : Random.Range(profile.MinWidth, profile.MaxWidth) * profile.HeavyScale;

            // Preserve the existing player escape hatch. Everything is the working default;
            // DecalLayer7 remains available only for profiles whose shaders eventually support it.
            DecalLayerEnum layer = BloodFXDecalSurfaces.BodyDecalLayerIsolationActive
                ? BloodFXDecalSurfaces.BodyDecalLayer
                : DecalLayerEnum.Everything;
            BloodFXDirector.ApplyProjectorScale(instance, size, profile.Depth, profile.FrontMargin, layer);

            BloodFXDecalRegistry.RegisterBodyDecal(channelKey, instance, cap);

            BloodFXBootstrap.Trace(
                $"body decal {label} bone='{anchor.name}' "
                + $"pos={BloodFXDirector.Format(surface)} prefab='{prefab.name}' "
                + $"size={size:0.###}x{size:0.###}x{profile.Depth:0.###} frontMargin={profile.FrontMargin:0.###} "
                + $"decalLayer={layer} path={BloodFXDecalSurfaces.ActivePathName} "
                + $"count={BloodFXDecalRegistry.BodyCount(channelKey)}/{cap} "
                + BloodFXDirector.DescribeProjector(instance) + ".");

            return instance;
        }
        /// <summary>
        /// Place at an already-resolved world-space contact point while parenting the projector to
        /// the matched bone. Used for replicated creature hits: every client receives the same
        /// position, while prefab choice, roll, and size remain cosmetic local variation.
        /// </summary>
        public static GameObject PlaceAt(
            long channelKey,
            Transform anchor,
            Vector3 surface,
            Vector3 direction,
            in Profile profile,
            bool final,
            int cap,
            string label,
            GameObject prefabOverride = null)
        {
            cap = Mathf.Max(0, cap);
            if (cap == 0 || anchor == null)
                return null;

            GameObject prefab = prefabOverride != null ? prefabOverride : BloodFXAssets.BodyPrefab;
            if (prefab == null)
                return null;

            Vector3 outward = direction.sqrMagnitude > 0.0001f
                ? -direction.normalized
                : surface - anchor.position;
            if (outward.sqrMagnitude <= 0.0001f)
                outward = anchor.forward;
            if (outward.sqrMagnitude <= 0.0001f)
                outward = Vector3.forward;
            outward.Normalize();

            Quaternion rotation = BloodFXDirector.ProjectAlong(-outward)
                                  * Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.forward);
            GameObject instance = Object.Instantiate(prefab, surface, rotation, anchor);
            float size = final
                ? profile.MaxWidth * profile.HeavyScale
                : Random.Range(profile.MinWidth, profile.MaxWidth) * profile.HeavyScale;

            // Creature materials use the known-working default layer. The optional player-only
            // DecalLayer7 escape hatch has no shader variants on this build and must not leak here.
            const DecalLayerEnum layer = DecalLayerEnum.Everything;
            BloodFXDirector.ApplyProjectorScale(instance, size, profile.Depth, profile.FrontMargin, layer);
            BloodFXDecalRegistry.RegisterBodyDecal(channelKey, instance, cap);

            BloodFXBootstrap.Trace(
                $"body decal {label} bone='{anchor.name}' "
                + $"pos={BloodFXDirector.Format(surface)} prefab='{prefab.name}' "
                + $"size={size:0.###}x{size:0.###}x{profile.Depth:0.###} frontMargin={profile.FrontMargin:0.###} "
                + $"decalLayer={layer} path=creature-contact "
                + $"count={BloodFXDecalRegistry.BodyCount(channelKey)}/{cap} "
                + BloodFXDirector.DescribeProjector(instance) + ".");

            return instance;
        }
    }
}
