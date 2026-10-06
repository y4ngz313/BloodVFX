using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Resolves the live wound pose for a running gore chute (#322). Returns false when the pose can
    /// no longer be resolved at all — victim despawned, prop destroyed — which ends the chute.
    ///
    /// A delegate rather than a typed source object on purpose: BloodFX is a presentation system and
    /// must not take a reference on Bundy (or on anything else that wants a sustained wound). The
    /// caller closes over whatever it needs and hands back two vectors.
    /// </summary>
    public delegate bool BloodFXWoundPose(out Vector3 origin, out Vector3 direction);

    /// <summary>
    /// Player-body state that travels with a sustained finisher chute. The live wound pose remains
    /// a delegate, while this small value identifies which articulated body owns its persistent
    /// decal and which wound band selected the parent bone.
    /// </summary>
    public readonly struct BloodFXFinisherTargets
    {
        public readonly PlayerControllerB Victim;
        public readonly Transform BodyAnchor;
        public readonly int BodyIndex;

        public BloodFXFinisherTargets(
            PlayerControllerB victim,
            Transform bodyAnchor,
            int bodyIndex)
        {
            Victim = victim;
            BodyAnchor = bodyAnchor;
            BodyIndex = bodyIndex;
        }
    }

    /// <summary>
    /// A sustained blood chute pouring out of one wound for as long as a cut lasts (#322).
    ///
    /// <b>This is the one per-frame thing in BloodFX, and it is bounded on three axes.</b> Everything
    /// else in the system is a one-shot fired from a Harmony patch; a chainsaw finisher is a
    /// multi-second event where the wound moves with the pair, so it needs a driver. The driver
    /// exists only while a cut is running, re-fires the same round-robin pools every other system
    /// uses (it never instantiates a particle), and spends a small fixed decal budget rather than
    /// hosing the round-long world cap.
    /// </summary>
    internal sealed class BloodFXFinisherGore : MonoBehaviour
    {
        /// <summary>
        /// The burst and spray pools are four deep and round robin, and firing an instance clears
        /// whatever it was already playing. The cadence therefore has to leave each instance a full
        /// cycle (4 x this) to finish its own life before the cursor returns to it, or the chute
        /// visibly cuts itself off every pulse.
        /// </summary>
        private const float PulseIntervalSeconds = 0.3f;

        /// <summary>
        /// Environment decals are the expensive half: every one that lands takes a slot in the
        /// round-long world cap. They run on their own, far slower clock and stop entirely after
        /// <see cref="DecalPulseBudget"/> events, so one finisher cannot churn the whole cap.
        /// </summary>
        private const float DecalIntervalSeconds = 0.85f;

        private const int DecalPulseBudget = 3;

        /// <summary>
        /// Hard stop. Every finisher exit path calls <see cref="End"/>, but a teardown nobody wired
        /// must not leave a chute pulsing for the rest of the round.
        /// </summary>
        private const float MaxDurationSeconds = 8f;

        private const string DriverObjectName = "Y4NGZCompany_BloodFXFinisherGore";

        private static readonly Dictionary<int, BloodFXFinisherGore> Active =
            new Dictionary<int, BloodFXFinisherGore>(2);

        private static readonly List<int> ScratchKeys = new List<int>(2);

        private int _key;
        private string _label;
        private Transform _sourceRoot;
        private BloodFXWoundPose _pose;
        private BloodFXFinisherTargets _targets;
        private float _scale;
        private Vector3 _origin;
        private Vector3 _direction;
        private float _nextPulseAt;
        private float _nextDecalAt;
        private float _expireAt;
        private int _decalsRemaining;
        private int _pulses;
        private bool _ending;
        private bool _usingDedicatedChute;

        /// <summary>
        /// Start (or restart) the chute owned by <paramref name="key"/>. A second call for the same
        /// key replaces the running chute rather than stacking a second one.
        /// </summary>
        internal static bool Begin(
            int key,
            Transform sourceRoot,
            BloodFXWoundPose pose,
            in BloodFXFinisherTargets targets,
            float scale,
            string label)
        {
            if (pose == null)
                return false;

            if (!pose(out Vector3 origin, out Vector3 direction))
            {
                BloodFXBootstrap.Trace($"finisher gore ABORTED (no initial pose) key={key} label='{label}'.");
                return false;
            }

            End(key, "restarted");

            var driverObject = new GameObject(DriverObjectName);
            Object.DontDestroyOnLoad(driverObject);
            var driver = driverObject.AddComponent<BloodFXFinisherGore>();

            driver._key = key;
            driver._label = label;
            driver._sourceRoot = sourceRoot;
            driver._pose = pose;
            driver._targets = targets;
            driver._scale = Mathf.Max(0.05f, scale);
            driver._origin = origin;
            driver._direction = Normalize(direction);
            driver._expireAt = Time.time + MaxDurationSeconds;
            driver._nextPulseAt = Time.time + PulseIntervalSeconds;
            driver._nextDecalAt = Time.time + DecalIntervalSeconds;
            // The opening pulse below spends the first slot. Keeping the full budget here made the
            // nominal three-pulse chute paint four times.
            driver._decalsRemaining = Mathf.Max(0, DecalPulseBudget - 1);

            driver.UpdateDedicatedChutePose();
            driver._usingDedicatedChute = driver.TryStartDedicatedChute();
            Active[key] = driver;

            // The opening pulse always paints, so the wound reads immediately even if the cut is
            // interrupted before the second pulse lands.
            driver.Pulse(placeDecals: true);

            BloodFXBootstrap.Trace(
                $"finisher gore START key={key} label='{label}' scale={driver._scale:0.##} "
                + $"origin=({origin.x:0.##},{origin.y:0.##},{origin.z:0.##}) "
                + $"dir=({driver._direction.x:0.##},{driver._direction.y:0.##},{driver._direction.z:0.##}) "
                + $"particles={(driver._usingDedicatedChute ? "dedicated-chute" : "burst-spray-fallback")}.");

            return true;
        }

        /// <summary>Stop the chute owned by <paramref name="key"/>, if one is running.</summary>
        internal static void End(int key, string reason)
        {
            if (Active.Count == 0)
                return;

            if (!Active.TryGetValue(key, out BloodFXFinisherGore driver))
                return;

            Active.Remove(key);
            if (driver != null)
                driver.Teardown(reason);
        }

        /// <summary>Round reset / plugin teardown: nothing may survive a scene change.</summary>
        internal static void StopAll(string reason)
        {
            if (Active.Count == 0)
                return;

            ScratchKeys.Clear();
            foreach (KeyValuePair<int, BloodFXFinisherGore> entry in Active)
                ScratchKeys.Add(entry.Key);

            for (int i = 0; i < ScratchKeys.Count; i++)
                End(ScratchKeys[i], reason);

            ScratchKeys.Clear();
            Active.Clear();
        }

        private void Update()
        {
            if (_ending)
                return;

            float now = Time.time;
            if (now >= _expireAt)
            {
                Active.Remove(_key);
                Teardown("max duration");
                return;
            }

            if (_pose != null)
            {
                if (!_pose(out Vector3 origin, out Vector3 direction))
                {
                    Active.Remove(_key);
                    Teardown("pose lost");
                    return;
                }

                if (IsFinite(origin))
                    _origin = origin;
                if (direction.sqrMagnitude > 0.0001f && IsFinite(direction))
                    _direction = direction.normalized;
            }

            if (_usingDedicatedChute)
                UpdateDedicatedChutePose();

            if (now < _nextPulseAt)
                return;

            _nextPulseAt = now + PulseIntervalSeconds;

            bool placeDecals = _decalsRemaining > 0 && now >= _nextDecalAt;
            if (placeDecals)
            {
                _decalsRemaining--;
                _nextDecalAt = now + DecalIntervalSeconds;
            }

            Pulse(placeDecals);
        }

        private void Pulse(bool placeDecals)
        {
            _pulses++;
            BloodFXDirector.EmitFinisherPulse(
                _sourceRoot,
                _origin,
                _direction,
                _scale,
                placeDecals,
                !_usingDedicatedChute,
                in _targets);
        }

        private bool TryStartDedicatedChute()
        {
            GameObject[] prefabs = BloodFXAssets.ChutePrefabs;
            if (prefabs == null || prefabs.Length == 0)
                return false;

            int spawned = 0;
            for (int prefabIndex = 0; prefabIndex < prefabs.Length; prefabIndex++)
            {
                GameObject prefab = prefabs[prefabIndex];
                if (prefab == null)
                    continue;

                GameObject instance = Object.Instantiate(prefab, transform, false);
                instance.name = prefab.name + "_FinisherChute";
                instance.SetActive(false);

                Transform instanceTransform = instance.transform;
                instanceTransform.localPosition = Vector3.zero;
                instanceTransform.localRotation = Quaternion.identity;
                instanceTransform.localScale = prefab.transform.localScale;

                ParticleSystem[] systems = instance.GetComponentsInChildren<ParticleSystem>(true);
                if (systems.Length == 0)
                {
                    Object.Destroy(instance);
                    continue;
                }

                for (int systemIndex = 0; systemIndex < systems.Length; systemIndex++)
                {
                    ParticleSystem system = systems[systemIndex];
                    ParticleSystem.MainModule main = system.main;
                    main.loop = true;
                    main.stopAction = ParticleSystemStopAction.None;
                    main.playOnAwake = true;

                    ParticleSystem.CollisionModule collision = system.collision;
                    collision.enabled = false;
                    system.Clear(withChildren: false);
                }

                instance.SetActive(true);
                systems[0].Play(withChildren: true);
                spawned++;
            }

            if (spawned > 0)
                BloodFXBootstrap.Trace($"finisher dedicated chute START prefabs={spawned} key={_key}.");
            return spawned > 0;
        }

        private void UpdateDedicatedChutePose()
        {
            Vector3 forward = Normalize(_direction);
            Vector3 up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f
                ? Vector3.forward
                : Vector3.up;
            transform.SetPositionAndRotation(_origin, Quaternion.LookRotation(forward, up));
            transform.localScale = Vector3.one * _scale;
        }

        private void Teardown(string reason)
        {
            if (_ending)
                return;

            _ending = true;
            BloodFXBootstrap.Trace($"finisher gore END key={_key} label='{_label}' pulses={_pulses} reason={reason}.");
            Object.Destroy(gameObject);
        }

        private static Vector3 Normalize(Vector3 value)
        {
            return value.sqrMagnitude > 0.0001f && IsFinite(value) ? value.normalized : Vector3.forward;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                   && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                   && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}
