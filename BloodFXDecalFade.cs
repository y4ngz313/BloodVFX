using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Dissolves a blood decal out instead of destroying it outright.
    ///
    /// The pack ships <c>RealisticBlood/Scripts/DecalDissolveHDRP.cs</c> for this, but pack
    /// MonoBehaviours are stripped at bundle time, so the behaviour is reimplemented here.
    /// Three things are deliberately done differently from the pack script:
    ///
    /// 1. It cloned the projector material and destroyed the GameObject but never the clone,
    ///    leaking one material per decal for the whole round. Both are destroyed here.
    /// 2. It ran an Update() per decal. One driver walks every fade instead.
    /// 3. It left <c>_DissolveSoftness</c> alone, which on most shipped materials is 0.01 —
    ///    that erodes as a hard-edged wipe rather than a fade. It is raised per fade.
    ///
    /// <c>_Dissolve</c> runs 0 (fully visible) to 1 (gone) and erodes whichever texture
    /// <c>Shader_Decal</c>'s Branch selected, so this works on the shipped materials as-is
    /// regardless of <c>_CustomDissolveMap</c>. Purely visual and client-local — the decals
    /// themselves are spawned per client from a damage patch, so nothing here is networked.
    /// </summary>
    internal static class BloodFXDecalFade
    {
        /// <summary>
        /// Most shipped decal materials carry 0.01, which reads as a wipe rather than a fade.
        /// Only ever raised — a material authored softer than this keeps its own value.
        /// </summary>
        private const float MinDissolveSoftness = 0.25f;

        private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        private static readonly int DissolveSoftnessId = Shader.PropertyToID("_DissolveSoftness");

        private static readonly List<FadingDecal> Fading = new List<FadingDecal>(16);
        private static Driver _driver;

        /// <summary>Decals currently mid-fade. They have already left their cap queue.</summary>
        internal static int Count => Fading.Count;

        /// <summary>
        /// Take ownership of an evicted decal and dissolve it away. Falls back to an immediate
        /// destroy when the fade is disabled or the prefab has nothing dissolvable on it, so a
        /// decal can never be orphaned into living forever.
        /// </summary>
        internal static void Begin(GameObject decal)
        {
            if (decal == null)
                return;

            float duration = BloodFXBootstrap.DecalFadeSeconds?.Value ?? 1.2f;
            if (duration <= 0f)
            {
                Object.Destroy(decal);
                return;
            }

            Material[] materials = CloneDissolvableMaterials(decal);
            if (materials == null)
            {
                Object.Destroy(decal);
                return;
            }

            // Nothing may enter the list before the driver exists to tick it — an untracked
            // entry would sit fully visible forever and never be destroyed.
            if (!EnsureDriver())
            {
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null)
                        Object.Destroy(materials[i]);
                }

                Object.Destroy(decal);
                return;
            }

            Fading.Add(new FadingDecal(decal, materials));
        }

        internal static void Tick()
        {
            if (Fading.Count == 0)
                return;

            float duration = Mathf.Max(0.01f, BloodFXBootstrap.DecalFadeSeconds?.Value ?? 1.2f);
            float delta = Time.deltaTime;

            for (int i = Fading.Count - 1; i >= 0; i--)
            {
                FadingDecal entry = Fading[i];

                // Something else destroyed it mid-fade (round end, scene unload).
                if (entry.Decal == null)
                {
                    ReleaseMaterials(entry);
                    Fading.RemoveAt(i);
                    continue;
                }

                entry.Elapsed += delta;
                float progress = Mathf.Clamp01(entry.Elapsed / duration);

                for (int m = 0; m < entry.Materials.Length; m++)
                {
                    Material material = entry.Materials[m];
                    if (material != null)
                        material.SetFloat(DissolveId, progress);
                }

                if (progress >= 1f)
                {
                    Object.Destroy(entry.Decal);
                    ReleaseMaterials(entry);
                    Fading.RemoveAt(i);
                }
            }
        }

        /// <summary>Round end / disconnect: drop everything mid-fade immediately.</summary>
        internal static void ClearAll()
        {
            for (int i = 0; i < Fading.Count; i++)
            {
                FadingDecal entry = Fading[i];
                if (entry.Decal != null)
                    Object.Destroy(entry.Decal);

                ReleaseMaterials(entry);
            }

            Fading.Clear();
        }

        /// <summary>
        /// Clones every dissolvable material on the decal and swaps it onto its projector.
        /// Cloned at fade time rather than at spawn: the instance only has to outlive the fade,
        /// so a full decal cap costs no permanent material instances. Returns null when nothing
        /// on the prefab can dissolve.
        /// </summary>
        private static Material[] CloneDissolvableMaterials(GameObject decal)
        {
            DecalProjector[] projectors = decal.GetComponentsInChildren<DecalProjector>(true);
            if (projectors == null || projectors.Length == 0)
                return null;

            List<Material> clones = null;

            for (int i = 0; i < projectors.Length; i++)
            {
                DecalProjector projector = projectors[i];
                Material shared = projector != null ? projector.material : null;
                if (shared == null || !shared.HasProperty(DissolveId))
                    continue;

                var instance = new Material(shared);
                instance.SetFloat(DissolveId, 0f);

                if (instance.HasProperty(DissolveSoftnessId)
                    && instance.GetFloat(DissolveSoftnessId) < MinDissolveSoftness)
                {
                    instance.SetFloat(DissolveSoftnessId, MinDissolveSoftness);
                }

                projector.material = instance;

                if (clones == null)
                    clones = new List<Material>(projectors.Length);
                clones.Add(instance);
            }

            return clones != null ? clones.ToArray() : null;
        }

        private static void ReleaseMaterials(FadingDecal entry)
        {
            for (int i = 0; i < entry.Materials.Length; i++)
            {
                Material material = entry.Materials[i];
                if (material != null)
                    Object.Destroy(material);
            }
        }

        /// <summary>
        /// Puts the tick host back on the decal root, re-adding it if a scene load took the
        /// root with it. World decals are parented to that root, body decals to a player bone,
        /// so either can outlive a driver; the stale entries they leave are reaped by the null
        /// check in <see cref="Tick"/> once a driver exists again.
        /// </summary>
        private static bool EnsureDriver()
        {
            if (_driver != null)
                return true;

            Transform root = BloodFXAssets.DecalRoot;
            if (root == null)
                return false;

            _driver = root.gameObject.AddComponent<Driver>();
            return _driver != null;
        }

        private sealed class FadingDecal
        {
            internal readonly GameObject Decal;
            internal readonly Material[] Materials;
            internal float Elapsed;

            internal FadingDecal(GameObject decal, Material[] materials)
            {
                Decal = decal;
                Materials = materials;
                Elapsed = 0f;
            }
        }

        /// <summary>
        /// Single tick host, living on the decal root so it shares that object's lifetime.
        /// </summary>
        private sealed class Driver : MonoBehaviour
        {
            private void LateUpdate()
            {
                Tick();
            }
        }
    }
}
