using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Experience.BloodFX
{
    public enum BodyChannel
    {
        Hits,
        Finisher,
        Creature
    }

    /// <summary>
    /// One global FIFO registry for every blood decal this system spawns.
    ///
    /// World decals live for the whole round under a single cap (oldest recycled first).
    /// Body decals are capped per owner and channel. Player decals survive healing on purpose;
    /// all channels for that player are cleared by respawn (PlayerControllerB.ResetPlayerBloodObjects)
    /// or round end.
    ///
    /// Event-driven: nothing here ticks. Recycling hands the evicted decal to
    /// <see cref="BloodFXDecalFade"/> so it dissolves out rather than popping, and that is
    /// the only part of decal lifetime that runs per frame. An evicted decal leaves its queue
    /// immediately, so time spent mid-fade never counts against the cap.
    ///
    /// Lifecycle resets (respawn, round end) still destroy outright — those are not evictions
    /// and should not leave blood fading on a body that no longer exists.
    /// </summary>
    public static class BloodFXDecalRegistry
    {
        private const int BodyChannelCount = 3;

        private static readonly Queue<GameObject> WorldDecals = new Queue<GameObject>(64);
        private static readonly Dictionary<int, Queue<GameObject>> BodyDecals = new Dictionary<int, Queue<GameObject>>(8);
        private static readonly Dictionary<long, Queue<GameObject>> BodyChannelDecals = new Dictionary<long, Queue<GameObject>>(8);

        internal static int WorldCount => WorldDecals.Count;

        internal static void RegisterWorldDecal(GameObject decal)
        {
            if (decal == null)
                return;

            int cap = Mathf.Max(0, BloodFXBootstrap.WorldDecalCap?.Value ?? 48);
            if (cap == 0)
            {
                Object.Destroy(decal);
                return;
            }

            WorldDecals.Enqueue(decal);
            while (WorldDecals.Count > cap)
            {
                GameObject oldest = WorldDecals.Dequeue();
                if (oldest != null)
                    BloodFXDecalFade.Begin(oldest);
            }
        }

        /// <summary>
        /// Produce a stable queue key for one owner's independent decal channel. Unity instance IDs
        /// are 32-bit; supplemental channels occupy a marked 64-bit namespace so no owner ID can
        /// collide with them. The ordinary hit channel deliberately remains the raw owner ID so the
        /// established player body-decal path is byte-for-byte unchanged.
        /// </summary>
        public static long ChannelFor(int ownerKey, BodyChannel channel)
        {
            if (channel == BodyChannel.Hits)
                return ownerKey;

            const long SupplementalChannelMarker = 1L << 62;
            return SupplementalChannelMarker
                   | ((long)(uint)ownerKey << 8)
                   | (byte)channel;
        }

        internal static void RegisterBodyDecal(int playerKey, GameObject decal)
        {
            if (decal == null)
                return;

            int cap = Mathf.Max(0, BloodFXBootstrap.BodyDecalCap?.Value ?? 4);
            if (cap == 0)
            {
                Object.Destroy(decal);
                return;
            }

            if (!BodyDecals.TryGetValue(playerKey, out Queue<GameObject> queue))
            {
                queue = new Queue<GameObject>(cap + 1);
                BodyDecals[playerKey] = queue;
            }

            queue.Enqueue(decal);
            while (queue.Count > cap)
            {
                GameObject oldest = queue.Dequeue();
                if (oldest != null)
                    BloodFXDecalFade.Begin(oldest);
            }
        }

        internal static void RegisterBodyDecal(long channelKey, GameObject decal, int cap)
        {
            if (decal == null)
                return;

            cap = Mathf.Max(0, cap);
            if (cap == 0)
            {
                Object.Destroy(decal);
                return;
            }

            if (!BodyChannelDecals.TryGetValue(channelKey, out Queue<GameObject> queue))
            {
                queue = new Queue<GameObject>(cap + 1);
                BodyChannelDecals[channelKey] = queue;
            }

            queue.Enqueue(decal);
            while (queue.Count > cap)
            {
                GameObject oldest = queue.Dequeue();
                if (oldest != null)
                    BloodFXDecalFade.Begin(oldest);
            }
        }

        internal static int BodyCount(int playerKey)
        {
            return BodyDecals.TryGetValue(playerKey, out Queue<GameObject> queue) ? queue.Count : 0;
        }

        public static int BodyCount(long channelKey)
        {
            return BodyChannelDecals.TryGetValue(channelKey, out Queue<GameObject> queue) ? queue.Count : 0;
        }

        internal static void ClearBody(int playerKey)
        {
            if (!BodyDecals.TryGetValue(playerKey, out Queue<GameObject> queue))
                return;

            while (queue.Count > 0)
            {
                GameObject decal = queue.Dequeue();
                if (decal != null)
                    Object.Destroy(decal);
            }

            BodyDecals.Remove(playerKey);
        }

        /// <summary>Clear every supplemental body-decal channel owned by one object.</summary>
        internal static void ClearSupplementalBodyChannels(int ownerKey)
        {
            for (int channel = 1; channel < BodyChannelCount; channel++)
                ClearBodyChannel(ChannelFor(ownerKey, (BodyChannel)channel));
        }

        public static void ClearBodyChannel(long channelKey)
        {
            if (!BodyChannelDecals.TryGetValue(channelKey, out Queue<GameObject> queue))
                return;

            while (queue.Count > 0)
            {
                GameObject decal = queue.Dequeue();
                if (decal != null)
                    Object.Destroy(decal);
            }

            BodyChannelDecals.Remove(channelKey);
        }

        internal static void ClearWorld()
        {
            while (WorldDecals.Count > 0)
            {
                GameObject decal = WorldDecals.Dequeue();
                if (decal != null)
                    Object.Destroy(decal);
            }
        }

        /// <summary>Round end / ship leave / disconnect: drop everything this system placed.</summary>
        internal static void ClearAll()
        {
            ClearWorld();
            BloodFXDecalFade.ClearAll();

            foreach (KeyValuePair<int, Queue<GameObject>> entry in BodyDecals)
            {
                Queue<GameObject> queue = entry.Value;
                while (queue.Count > 0)
                {
                    GameObject decal = queue.Dequeue();
                    if (decal != null)
                        Object.Destroy(decal);
                }
            }

            BodyDecals.Clear();

            foreach (KeyValuePair<long, Queue<GameObject>> entry in BodyChannelDecals)
            {
                Queue<GameObject> queue = entry.Value;
                while (queue.Count > 0)
                {
                    GameObject decal = queue.Dequeue();
                    if (decal != null)
                        Object.Destroy(decal);
                }
            }

            BodyChannelDecals.Clear();
        }
    }
}
