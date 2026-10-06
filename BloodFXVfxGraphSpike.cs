using UnityEngine;
using UnityEngine.VFX;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// TEMP #374 compatibility spike. It proves the one bundled VFX Graph in Lethal Company's own
    /// runtime, reports whether particles are alive, and presents it in front of the local camera
    /// for a human render verdict. Remove after docs/assets/README.md records the result.
    /// </summary>
    internal sealed class BloodFXVfxGraphSpike : MonoBehaviour
    {
        private const float RuntimeReportDelaySeconds = 1.5f;
        private const float VisibleLifetimeSeconds = 10f;
        private const string DriverName = "Y4NGZCompany_VFXGraphSpike";

        private static bool _armed;

        private GameObject _prefab;
        private string _manifestName;
        private GameObject _instance;
        private VisualEffect _effect;
        private float _reportAt;
        private float _destroyAt;
        private bool _spawned;
        private bool _reported;

        internal static void Arm(GameObject prefab, string manifestName)
        {
            if (_armed)
                return;

            _armed = true;
            VisualEffect prefabEffect = prefab != null
                ? prefab.GetComponentInChildren<VisualEffect>(true)
                : null;
            BloodFXBootstrap.Log?.LogWarning(
                "[Y4NGZCompany.BloodFX.VFXGraphSpike] " +
                $"manifest='{manifestName}' LoadAsset<GameObject>={(prefab != null ? "NON_NULL" : "NULL")} " +
                $"VisualEffectComponent={(prefabEffect != null ? "present" : "missing")} " +
                $"VisualEffectAsset={(prefabEffect != null && prefabEffect.visualEffectAsset != null ? prefabEffect.visualEffectAsset.name : "<null>")}.");

            if (prefab == null || prefabEffect == null || prefabEffect.visualEffectAsset == null)
                return;

            var driverObject = new GameObject(DriverName);
            Object.DontDestroyOnLoad(driverObject);
            var driver = driverObject.AddComponent<BloodFXVfxGraphSpike>();
            driver._prefab = prefab;
            driver._manifestName = manifestName;
        }

        private void Update()
        {
            if (!_spawned)
            {
                TrySpawnForLocalCamera();
                return;
            }

            float now = Time.unscaledTime;
            if (!_reported && now >= _reportAt)
            {
                _reported = true;
                bool effectAlive = _effect != null;
                BloodFXBootstrap.Log?.LogWarning(
                    "[Y4NGZCompany.BloodFX.VFXGraphSpike] " +
                    $"RUNTIME manifest='{_manifestName}' componentAlive={effectAlive} " +
                    $"enabled={(effectAlive && _effect.enabled)} " +
                    $"culled={(effectAlive ? _effect.culled.ToString() : "<missing>")} " +
                    $"aliveParticleCount={(effectAlive ? _effect.aliveParticleCount.ToString() : "<missing>")}. " +
                    "Human verdict: look 1.5m in front of the local camera for the arterial VFX.");
            }

            if (now < _destroyAt)
                return;

            if (_instance != null)
                Object.Destroy(_instance);
            Object.Destroy(gameObject);
        }

        private void TrySpawnForLocalCamera()
        {
            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.localPlayerController == null)
                return;

            Camera camera = round.localPlayerController.gameplayCamera;
            if (camera == null)
                return;

            _instance = Object.Instantiate(_prefab, camera.transform, false);
            _instance.name = _prefab.name + "_RuntimeSpike";
            Transform instanceTransform = _instance.transform;
            instanceTransform.localPosition = new Vector3(0f, -0.25f, 1.5f);
            instanceTransform.localRotation = Quaternion.identity;
            instanceTransform.localScale = Vector3.one * 0.5f;
            _instance.SetActive(true);

            _effect = _instance.GetComponentInChildren<VisualEffect>(true);
            if (_effect == null || _effect.visualEffectAsset == null)
            {
                BloodFXBootstrap.Log?.LogWarning(
                    "[Y4NGZCompany.BloodFX.VFXGraphSpike] RUNTIME wrapper instantiated but its VisualEffect dependency is missing.");
                Object.Destroy(_instance);
                Object.Destroy(gameObject);
                return;
            }

            _effect.enabled = true;
            _effect.pause = false;
            _effect.playRate = 1f;
            _effect.Reinit();
            _effect.Play();

            _spawned = true;
            _reportAt = Time.unscaledTime + RuntimeReportDelaySeconds;
            _destroyAt = Time.unscaledTime + VisibleLifetimeSeconds;
            BloodFXBootstrap.Log?.LogWarning(
                "[Y4NGZCompany.BloodFX.VFXGraphSpike] RUNTIME START: arterial graph placed 1.5m in front of the local camera for 10 seconds.");
        }
    }
}
