using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Lazy bundle + manifest loader and particle pool for the blood VFX system.
    /// Asset names come from the sidecar manifest; only the category keys are known to code.
    /// </summary>
    public static class BloodFXAssets
    {
        // NOT ".lethalbundle": LethalLevelLoader auto-loads every *.lethalbundle in the
        // plugins folder, which double-loads this bundle and makes our own
        // AssetBundle.LoadFromFile return null. The extension IS the mechanism.
        internal const string BundleFileName = "y4ngz-bloodfx.bundle";
        internal const string ManifestFileName = "y4ngz-bloodfx.manifest.json";

        internal const int BurstPoolSize = 4;
        internal const int SprayPoolSize = 4;

        private const string PoolRootName = "Y4NGZCompany_BloodFXPool";
        private const string DecalRootName = "Y4NGZCompany_BloodFXDecals";

        private static bool _loaded;
        private static AssetBundle _bundle;

        private static GameObject[] _burstPrefabs = Array.Empty<GameObject>();
        private static GameObject[] _chutePrefabs = Array.Empty<GameObject>();
        private static GameObject[] _sprayPrefabs = Array.Empty<GameObject>();
        private static GameObject[] _woundPrefabs = Array.Empty<GameObject>();
        private static GameObject[] _wallPrefabs = Array.Empty<GameObject>();
        private static GameObject[] _spotsPrefabs = Array.Empty<GameObject>();
        private static GameObject[] _bodyPrefabs = Array.Empty<GameObject>();
        private static GameObject[] _puddlePrefabs = Array.Empty<GameObject>();
        private static GameObject[] _vfxSpikePrefabs = Array.Empty<GameObject>();

        private static Transform _poolRoot;
        private static Transform _decalRoot;
        private static PooledParticle[] _burstPool = Array.Empty<PooledParticle>();
        private static PooledParticle[] _sprayPool = Array.Empty<PooledParticle>();
        private static int _burstCursor;
        private static int _sprayCursor;

        public static bool IsAvailable
        {
            get
            {
                EnsureLoaded();
                return _bundle != null && _wallPrefabs.Length + _spotsPrefabs.Length > 0;
            }
        }

        /// <summary>
        /// Every prefab in the manifest's chute category. The sustained driver instantiates the
        /// small set once per finisher so a stream and its dripping accent can run together.
        /// </summary>
        internal static GameObject[] ChutePrefabs
        {
            get
            {
                EnsureLoaded();
                return _chutePrefabs;
            }
        }

        internal static GameObject WallPrefab => Pick(_wallPrefabs);
        internal static GameObject SpotsPrefab => Pick(_spotsPrefabs);
        internal static GameObject WoundPrefab => Pick(_woundPrefabs);
        internal static GameObject BodyPrefab => Pick(_bodyPrefabs);
        internal static GameObject PuddlePrefab => Pick(_puddlePrefabs);

        public static bool HasBodyPrefabs => _bodyPrefabs.Length > 0;

        /// <summary>Container every world/body decal is parented under. Recreated if the scene unloaded it.</summary>
        internal static Transform DecalRoot
        {
            get
            {
                if (_decalRoot == null)
                {
                    var go = new GameObject(DecalRootName);
                    _decalRoot = go.transform;
                }

                return _decalRoot;
            }
        }

        /// <summary>Round-robin burst instance. Never destroyed; reused for the whole session.</summary>
        internal static PooledParticle TakeBurst()
        {
            EnsureLoaded();
            if (_burstPool.Length == 0)
                return null;

            PooledParticle taken = _burstPool[_burstCursor];
            _burstCursor = (_burstCursor + 1) % _burstPool.Length;
            return taken;
        }

        /// <summary>Round-robin spray instance. Never destroyed; reused for the whole session.</summary>
        internal static PooledParticle TakeSpray()
        {
            EnsureLoaded();
            if (_sprayPool.Length == 0)
                return null;

            PooledParticle taken = _sprayPool[_sprayCursor];
            _sprayCursor = (_sprayCursor + 1) % _sprayPool.Length;
            return taken;
        }

        private static GameObject Pick(GameObject[] pool)
        {
            EnsureLoaded();
            return pool.Length == 0 ? null : pool[UnityEngine.Random.Range(0, pool.Length)];
        }

        // ------------------------------------------------------------------ load

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;

            if (!TryLoadManifest(out BloodFXManifest manifest, out string manifestReason))
            {
                BloodFXBootstrap.Log?.LogWarning(
                    $"[Y4NGZCompany.BloodFX] Manifest '{ManifestFileName}' unavailable ({manifestReason}); blood VFX disabled.");
                return;
            }

            _bundle = FindLoadedBundle(manifest);
            if (_bundle == null)
            {
                string bundlePath = ResolveRuntimeAssetPath(BundleFileName);
                if (string.IsNullOrWhiteSpace(bundlePath))
                {
                    BloodFXBootstrap.Log?.LogWarning(
                        $"[Y4NGZCompany.BloodFX] Bundle '{BundleFileName}' was not found next to the plugin; blood VFX disabled.");
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    BloodFXBootstrap.Log?.LogWarning(
                        $"[Y4NGZCompany.BloodFX] AssetBundle.LoadFromFile returned null for '{bundlePath}'; blood VFX disabled.");
                    return;
                }

                BloodFXBootstrap.Log?.LogInfo($"[Y4NGZCompany.BloodFX] Loaded blood VFX bundle from '{bundlePath}'.");
            }

            BloodFXManifestCategories categories = manifest.categories ?? new BloodFXManifestCategories();
            _burstPrefabs = LoadCategory(categories.burst, "burst");
            _chutePrefabs = LoadCategory(categories.chute, "chute");
            _sprayPrefabs = LoadCategory(categories.spray, "spray");
            _woundPrefabs = LoadCategory(categories.wound, "wound");
            _wallPrefabs = LoadCategory(categories.wall, "wall");
            _spotsPrefabs = LoadCategory(categories.spots, "spots");
            _bodyPrefabs = LoadCategory(categories.body, "body");
            _puddlePrefabs = LoadCategory(categories.puddle, "puddle");
            _vfxSpikePrefabs = LoadCategory(categories.vfx_spike, "vfx_spike");

            BuildPools();

            // Decides which anti-bleed path the director can use; logged unconditionally.
            BloodFXDecalSurfaces.Probe();

            BloodFXBootstrap.Log?.LogInfo(
                "[Y4NGZCompany.BloodFX] Assets ready: " +
                $"burst={_burstPrefabs.Length}, chute={_chutePrefabs.Length}, " +
                $"spray={_sprayPrefabs.Length}, wound={_woundPrefabs.Length}, " +
                $"wall={_wallPrefabs.Length}, spots={_spotsPrefabs.Length}, " +
                $"body={_bodyPrefabs.Length}, puddle={_puddlePrefabs.Length}, " +
                $"vfxSpike={_vfxSpikePrefabs.Length}.");

            if (categories.vfx_spike != null && categories.vfx_spike.Length > 0)
            {
                BloodFXVfxGraphSpike.Arm(
                    _vfxSpikePrefabs.Length > 0 ? _vfxSpikePrefabs[0] : null,
                    categories.vfx_spike[0]);
            }
        }

        private static void BuildPools()
        {
            if (_burstPrefabs.Length == 0 && _sprayPrefabs.Length == 0)
                return;

            var rootObject = new GameObject(PoolRootName);
            UnityEngine.Object.DontDestroyOnLoad(rootObject);
            _poolRoot = rootObject.transform;

            _burstPool = BuildPool(_burstPrefabs, BurstPoolSize);
            _sprayPool = BuildPool(_sprayPrefabs, SprayPoolSize);
        }

        private static PooledParticle[] BuildPool(GameObject[] prefabs, int count)
        {
            if (prefabs.Length == 0)
                return Array.Empty<PooledParticle>();

            var pool = new PooledParticle[count];
            for (int i = 0; i < count; i++)
            {
                GameObject prefab = prefabs[i % prefabs.Length];
                GameObject instance = UnityEngine.Object.Instantiate(prefab, _poolRoot, false);
                instance.name = prefab.name + "_Pooled" + i.ToString();
                instance.SetActive(false);
                pool[i] = new PooledParticle(instance);
            }

            return pool;
        }

        private static GameObject[] LoadCategory(string[] names, string categoryKey)
        {
            if (names == null || names.Length == 0)
                return Array.Empty<GameObject>();

            var loaded = new GameObject[names.Length];
            int found = 0;
            for (int i = 0; i < names.Length; i++)
            {
                GameObject asset = LoadAssetByName<GameObject>(_bundle, names[i]);
                if (asset == null)
                {
                    BloodFXBootstrap.Log?.LogWarning(
                        $"[Y4NGZCompany.BloodFX] Manifest category '{categoryKey}' lists '{names[i]}', which the bundle does not contain.");
                    continue;
                }

                loaded[found++] = asset;
            }

            if (found == loaded.Length)
                return loaded;

            var trimmed = new GameObject[found];
            Array.Copy(loaded, trimmed, found);
            return trimmed;
        }

        private static AssetBundle FindLoadedBundle(BloodFXManifest manifest)
        {
            string probe = FirstName(manifest);
            if (string.IsNullOrWhiteSpace(probe))
                return null;

            foreach (AssetBundle bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle == null)
                    continue;

                try
                {
                    if (bundle.Contains(probe) || BundleHasMatchingAsset(bundle, probe))
                        return bundle;
                }
                catch
                {
                    // Best-effort scan only; keep probing other bundles.
                }
            }

            return null;
        }

        private static string FirstName(BloodFXManifest manifest)
        {
            BloodFXManifestCategories categories = manifest?.categories;
            if (categories == null)
                return null;

            string[][] all =
            {
                categories.burst,
                categories.chute,
                categories.spray,
                categories.wound,
                categories.wall,
                categories.spots,
                categories.body,
                categories.puddle
            };
            for (int i = 0; i < all.Length; i++)
            {
                string[] names = all[i];
                if (names != null && names.Length > 0 && !string.IsNullOrWhiteSpace(names[0]))
                    return names[0];
            }

            return null;
        }

        // --------------------------------------------------------------- manifest

        private static bool TryLoadManifest(out BloodFXManifest manifest, out string reason)
        {
            manifest = null;
            reason = string.Empty;

            try
            {
                string manifestPath = ResolveRuntimeAssetPath(ManifestFileName);
                if (string.IsNullOrWhiteSpace(manifestPath))
                {
                    reason = $"file='{ManifestFileName}' not found next to the plugin";
                    return false;
                }

                manifest = JsonUtility.FromJson<BloodFXManifest>(File.ReadAllText(manifestPath));
                if (manifest == null)
                {
                    reason = "manifest_json_returned_null";
                    return false;
                }

                if (manifest.categories == null)
                {
                    reason = "manifest_missing_categories";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>Loose-first probe: next to the DLL, then the known plugin folders.</summary>
        private static string ResolveRuntimeAssetPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            string dllDir = null;
            try
            {
                string assemblyLocation = typeof(BloodFXAssets).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(assemblyLocation))
                    dllDir = Path.GetDirectoryName(assemblyLocation);
            }
            catch
            {
                dllDir = null;
            }

            // The assembly directory is the normal answer: the bundle deploys beside this DLL.
            // The named package directories are the fallbacks for a profile whose plugin folder
            // layout differs from the DLL's own location - Gale names an imported package's
            // directory after the package, hand-made profiles use the `y4ngz-` prefixed form, and
            // the two Y4NGZCompany names remain because the bundle shipped inside that package
            // before this system became its own plugin. The Y4NGZBloodFX names remain for the
            // same reason: that was the Thunderstore package name before it was renamed BloodVFX,
            // so an existing profile still has a directory under the old name.
            string[] candidates =
            {
                !string.IsNullOrWhiteSpace(dllDir) ? Path.Combine(dllDir, fileName) : null,
                Path.Combine(Paths.PluginPath, "y4ngz-BloodVFX", fileName),
                Path.Combine(Paths.PluginPath, "BloodVFX", fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZBloodFX", fileName),
                Path.Combine(Paths.PluginPath, "Y4NGZBloodFX", fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", fileName),
                Path.Combine(Paths.PluginPath, "Y4NGZCompany", fileName),
                Path.Combine(Paths.PluginPath, fileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        // ----------------------------------------------------------- bundle lookup

        private static T LoadAssetByName<T>(AssetBundle bundle, string assetName) where T : UnityEngine.Object
        {
            if (bundle == null || string.IsNullOrWhiteSpace(assetName))
                return null;

            try
            {
                T direct = bundle.LoadAsset<T>(assetName);
                if (direct != null)
                    return direct;
            }
            catch
            {
                // Fall through to the path/name scan.
            }

            string[] names = GetBundleAssetNames(bundle);
            for (int i = 0; i < names.Length; i++)
            {
                if (!AssetNameMatches(names[i], assetName))
                    continue;

                try
                {
                    T asset = bundle.LoadAsset<T>(names[i]);
                    if (asset != null)
                        return asset;
                }
                catch
                {
                    // Keep scanning.
                }
            }

            return null;
        }

        private static bool BundleHasMatchingAsset(AssetBundle bundle, string assetName)
        {
            string[] names = GetBundleAssetNames(bundle);
            for (int i = 0; i < names.Length; i++)
            {
                if (AssetNameMatches(names[i], assetName))
                    return true;
            }

            return false;
        }

        private static string[] GetBundleAssetNames(AssetBundle bundle)
        {
            try { return bundle != null ? bundle.GetAllAssetNames() : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        private static bool AssetNameMatches(string bundleAssetName, string requestedName)
        {
            if (string.IsNullOrWhiteSpace(bundleAssetName) || string.IsNullOrWhiteSpace(requestedName))
                return false;

            string bundleLeaf = Path.GetFileNameWithoutExtension(bundleAssetName).Replace('\\', '/');
            string requestLeaf = Path.GetFileNameWithoutExtension(requestedName).Replace('\\', '/');
            return string.Equals(bundleLeaf, requestLeaf, StringComparison.OrdinalIgnoreCase)
                   || bundleAssetName.EndsWith("/" + requestedName, StringComparison.OrdinalIgnoreCase)
                   || bundleAssetName.EndsWith("/" + requestLeaf, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ types

        [Serializable]
        private sealed class BloodFXManifest
        {
            public int version = 0;
            public BloodFXManifestCategories categories = null;
        }

        [Serializable]
        private sealed class BloodFXManifestCategories
        {
            public string[] burst = new string[0];
            public string[] chute = new string[0];
            public string[] spray = new string[0];
            public string[] wound = new string[0];
            public string[] wall = new string[0];
            public string[] spots = new string[0];
            public string[] body = new string[0];
            public string[] puddle = new string[0];
            public string[] vfx_spike = new string[0];
        }

        /// <summary>
        /// A reusable particle instance. Original burst counts and start colours are captured once
        /// at pool build so the per-hit intensity scale and the per-species tint (#120) write
        /// structs only and never allocate.
        /// </summary>
        internal sealed class PooledParticle
        {
            private readonly GameObject _instance;
            private readonly ParticleSystem[] _systems;
            private readonly ParticleSystem.Burst[][] _originalBursts;
            private readonly float[][] _originalCounts;
            private readonly ParticleSystem.MinMaxGradient[] _originalStartColors;

            /// <summary>
            /// The prefab's authored local scale, captured once (#147).
            ///
            /// Same reason the authored burst counts and start colours above are captured: the pool
            /// is round robin and shared, so every value a <see cref="Play"/> overwrites has to be
            /// recoverable or the first caller to scale an instance up leaves it that size for
            /// every caller after it. Read before anything in this constructor touches the
            /// transform, so it is the authored number and not a number this class already changed.
            /// </summary>
            private readonly Vector3 _baseScale;

            internal PooledParticle(GameObject instance)
            {
                _instance = instance;
                _baseScale = instance.transform.localScale;
                _systems = instance.GetComponentsInChildren<ParticleSystem>(true);
                _originalBursts = new ParticleSystem.Burst[_systems.Length][];
                _originalCounts = new float[_systems.Length][];
                _originalStartColors = new ParticleSystem.MinMaxGradient[_systems.Length];

                for (int i = 0; i < _systems.Length; i++)
                {
                    ParticleSystem system = _systems[i];

                    // Authoring contract says collision is off, but a stray prefab must not
                    // start raycasting every frame in a shipped build.
                    ParticleSystem.CollisionModule collision = system.collision;
                    collision.enabled = false;

                    ParticleSystem.EmissionModule emission = system.emission;
                    var bursts = new ParticleSystem.Burst[emission.burstCount];
                    emission.GetBursts(bursts);
                    _originalBursts[i] = bursts;

                    var counts = new float[bursts.Length];
                    for (int b = 0; b < bursts.Length; b++)
                        counts[b] = bursts[b].count.constant;
                    _originalCounts[i] = counts;

                    // The pool is round robin and shared across species, so a tinted play must be
                    // undoable — without the authored value stored here, one insect hit would turn
                    // that instance green for every human who bleeds through it afterwards.
                    _originalStartColors[i] = system.main.startColor;
                }

                foreach (Light light in instance.GetComponentsInChildren<Light>(true))
                    UnityEngine.Object.Destroy(light);

                foreach (ParticleSystemRenderer renderer in instance.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
            }

            /// <summary>
            /// Fire the instance. <paramref name="tint"/> follows the
            /// <see cref="BloodFXTint"/> alpha sentinel: a non-positive alpha restores the authored
            /// start colour instead of applying one, which is what keeps human blood byte-identical
            /// to the look that already has an in-game verdict.
            ///
            /// <paramref name="scale"/> multiplies the authored size (#147). <b>It is a separate
            /// lever from <paramref name="emissionMultiplier"/>, not a duplicate of it</b>: the
            /// emission multiplier rewrites burst COUNTS, so turning it up puts more particles in
            /// the same volume. Until this parameter existed there was no size control anywhere in
            /// the system, and every burst since it shipped has been the size it was authored at —
            /// tuned for a player wound seen at melee range. Pass 1f for that authored size.
            /// </summary>
            internal void Play(
                Vector3 position, Quaternion rotation, float emissionMultiplier, Color tint, float scale)
            {
                if (_instance == null || _systems.Length == 0)
                    return;

                Transform t = _instance.transform;
                t.SetPositionAndRotation(position, rotation);
                // Always written, never skipped when scale is 1: the instance may carry a previous
                // caller's size, and "leave it alone" would inherit that rather than reset it.
                t.localScale = _baseScale * Mathf.Max(0.01f, scale);

                bool tinted = BloodFXTint.IsTinted(tint);

                for (int i = 0; i < _systems.Length; i++)
                {
                    ParticleSystem system = _systems[i];
                    if (system == null)
                        continue;

                    ApplyStartColor(system, i, tinted, tint);

                    ParticleSystem.Burst[] originals = _originalBursts[i];
                    if (originals.Length == 0)
                        continue;

                    float[] counts = _originalCounts[i];
                    ParticleSystem.EmissionModule emission = system.emission;
                    for (int b = 0; b < originals.Length; b++)
                    {
                        ParticleSystem.Burst burst = originals[b];
                        burst.count = new ParticleSystem.MinMaxCurve(Mathf.Max(1f, counts[b] * emissionMultiplier));
                        emission.SetBurst(b, burst);
                    }
                }

                _instance.SetActive(true);

                ParticleSystem root = _systems[0];
                root.Clear(withChildren: true);
                root.Play(withChildren: true);
            }

            /// <summary>
            /// Recolour one system, or put its authored colour back.
            ///
            /// Only the HUE is replaced. The authored alpha is kept when the system's start colour is
            /// a plain constant, because that is the channel the prefabs use to set a system's weight
            /// in the blend — the colour-over-lifetime gradient is white-to-transparent-white and
            /// carries no hue of its own, which is the property that makes tinting work at all.
            /// </summary>
            private void ApplyStartColor(ParticleSystem system, int index, bool tinted, Color tint)
            {
                ParticleSystem.MainModule main = system.main;
                ParticleSystem.MinMaxGradient authored = _originalStartColors[index];

                if (!tinted)
                {
                    main.startColor = authored;
                    return;
                }

                float alpha = authored.mode == ParticleSystemGradientMode.Color ? authored.color.a : 1f;
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(tint.r, tint.g, tint.b, alpha));
            }
        }
    }
}
