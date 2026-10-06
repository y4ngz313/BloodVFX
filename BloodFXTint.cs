using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Runtime recolouring for blood (#120). Nothing in this system carried colour before: the
    /// bundle materials did, which is precisely why a tint is possible at all — the decal textures
    /// are greyscale masks with no hue baked into them.
    ///
    /// <b>The sentinel is alpha, and it means "leave the authored colour alone".</b> A tint of
    /// <see cref="Color.clear"/> — or anything with a non-positive alpha — is not a transparent
    /// blood colour, it is the instruction to draw the asset exactly as authored. Human blood is
    /// already red in the bundle, so the flesh path passes clear and gets the shipped look bit for
    /// bit; only the species that bleed something else pay for a tint. That asymmetry is deliberate:
    /// the red path is the one with an in-game verdict behind it and it should stay byte-identical.
    ///
    /// <b>Particles and decals need completely different mechanisms.</b> A ParticleSystem's start
    /// colour is a per-instance struct write, free and reversible (see
    /// <c>BloodFXAssets.PooledParticle</c>). An HDRP <see cref="DecalProjector"/> is drawn by the
    /// decal system rather than by a <c>Renderer</c>, so a <c>MaterialPropertyBlock</c> does nothing
    /// at all and the only lever is the material itself — which is why this file exists.
    /// </summary>
    internal static class BloodFXTint
    {
        /// <summary>Shader_Decal.shadergraph's three colour inputs. <c>_MainColor</c> alone is not
        /// enough: the shipped material leaves red in the specular highlight and the rim, so a green
        /// decal tinted through main only reads as green with a red sheen.</summary>
        private const string MainColorProperty = "_MainColor";
        private const string SecondaryColorProperty = "_SecondaryColor";
        private const string SpecularColorProperty = "_SpecularColor";

        /// <summary>
        /// How the two derived colours follow the main one. These are not taste — they are read off
        /// the shipped red material and inverted: <c>_MainColor</c> (1,0,0), <c>_SecondaryColor</c>
        /// (0.509,0,0) = main x 0.51, <c>_SpecularColor</c> (1,0.7,0.7) = Lerp(main, white, 0.7).
        /// Feeding pure red back through both reproduces the authored values exactly, which is what
        /// makes this a generalisation of the authored look rather than a second opinion about it.
        /// </summary>
        private const float SecondaryFactor = 0.51f;
        private const float SpecularWhiteMix = 0.7f;

        /// <summary>
        /// One instanced material per (source material, tint) pair, for the lifetime of the session.
        ///
        /// <b>A cache, not a clone per decal.</b> The environment spray places up to seven decals per
        /// event against a 48-decal cap; instantiating a Material for each would leak one native
        /// object per decal with nothing to destroy them, because the projector's material is not
        /// owned by the GameObject the registry destroys.
        /// </summary>
        private static readonly Dictionary<long, Material> Instances = new Dictionary<long, Material>(8);

        /// <summary>Whether a tint asks for anything at all. See the alpha sentinel above.</summary>
        internal static bool IsTinted(Color tint)
        {
            return tint.a > 0f;
        }

        /// <summary>
        /// Point a freshly spawned decal's projector at a tinted copy of its own material.
        ///
        /// No-op when the tint is the "authored" sentinel, when the instance carries no projector,
        /// or when the material has none of the three colour properties — a future decal shader with
        /// different inputs then draws its authored colour rather than silently drawing nothing.
        /// </summary>
        internal static void ApplyDecalTint(GameObject instance, Color tint)
        {
            if (instance == null || !IsTinted(tint))
                return;

            DecalProjector projector = instance.GetComponent<DecalProjector>();
            if (projector == null)
                projector = instance.GetComponentInChildren<DecalProjector>(true);
            if (projector == null)
                return;

            Material tinted = Resolve(projector.material, tint);
            if (tinted != null)
                projector.material = tinted;
        }

        /// <summary>
        /// The tinted instance of <paramref name="source"/>, created once and reused.
        ///
        /// Returns the source unchanged when there is nothing to do, so a caller never has to
        /// branch. The authored ALPHA of each property is preserved and only the hue is replaced:
        /// the shipped material writes alpha 0 into <c>_MainColor</c> and <c>_SecondaryColor</c> and
        /// 1 into <c>_SpecularColor</c>, and those are the shader's own conventions to keep.
        /// </summary>
        private static Material Resolve(Material source, Color tint)
        {
            if (source == null || !IsTinted(tint))
                return source;

            long key = Key(source, tint);
            // Unity's overloaded != catches a material destroyed out from under the dictionary
            // (an UnloadUnusedAssets sweep), so a stale entry rebuilds instead of returning a
            // reference that draws nothing.
            if (Instances.TryGetValue(key, out Material cached) && cached != null)
                return cached;

            var instanced = new Material(source)
            {
                name = source.name + "_Y4NGZTint",
                // Not owned by any scene object and never saved. Without this the material is a
                // candidate for an UnloadUnusedAssets sweep between rounds, and the decals already
                // placed with it go black.
                hideFlags = HideFlags.HideAndDontSave,
            };

            SetHue(instanced, MainColorProperty, tint);
            SetHue(instanced, SecondaryColorProperty, tint * SecondaryFactor);
            SetHue(instanced, SpecularColorProperty, Color.Lerp(tint, Color.white, SpecularWhiteMix));

            Instances[key] = instanced;
            BloodFXBootstrap.Trace(
                $"tint material created source='{source.name}' "
                + $"tint=({tint.r:0.##},{tint.g:0.##},{tint.b:0.##}) cached={Instances.Count}.");
            return instanced;
        }

        /// <summary>Replace a colour property's RGB, keeping whatever alpha the asset authored.</summary>
        private static void SetHue(Material material, string property, Color hue)
        {
            if (!material.HasProperty(property))
                return;

            float authoredAlpha = material.GetColor(property).a;
            material.SetColor(property, new Color(hue.r, hue.g, hue.b, authoredAlpha));
        }

        /// <summary>
        /// (source material, tint) packed into one long. The tint is quantised to 8 bits per channel
        /// because the key exists to collapse the handful of species colours onto a handful of
        /// materials — a float-exact key would mint a new material for a rounding difference.
        /// </summary>
        private static long Key(Material source, Color tint)
        {
            int rgb = (Quantise(tint.r) << 16) | (Quantise(tint.g) << 8) | Quantise(tint.b);
            return ((long)(uint)source.GetInstanceID() << 32) | (uint)rgb;
        }

        private static int Quantise(float channel)
        {
            return Mathf.Clamp(Mathf.RoundToInt(channel * 255f), 0, 255);
        }
    }
}
