using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Every damage/death path the blood system reacts to. Targets were verified against the
    /// shipped Assembly-CSharp (v81):
    ///
    ///   DamagePlayer                        owner only  (early-returns unless IsOwner)
    ///   DamageOnOtherClients                observers   (inner of DamagePlayerClientRpc)
    ///   DamagePlayerFromOtherClientClientRpc every client (carries hitDirection)
    ///   KillPlayerClientRpc                 every client
    ///
    /// The director de-duplicates, so overlapping coverage costs nothing.
    /// </summary>
    [HarmonyPatch]
    internal static class BloodFXPatches
    {
        // Owner-side damage from every source that routes through DamagePlayer.
        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayer))]
        [HarmonyPrefix]
        private static void DamagePlayerPrefix(PlayerControllerB __instance, out int __state)
        {
            __state = __instance != null ? __instance.health : 0;
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayer))]
        [HarmonyPostfix]
        private static void DamagePlayerPostfix(
            PlayerControllerB __instance,
            int damageNumber,
            Vector3 force,
            int __state)
        {
            if (__instance == null || __instance.health >= __state)
                return;

            Vector3 direction = force;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                direction = BloodFXDirector.PendingHitDirection;
                BloodFXDirector.PendingHitDirection = Vector3.zero;
            }

            BloodFXDirector.OnPlayerDamaged(__instance, damageNumber, direction, "owner");
        }

        // Remote observers of an owner-side hit. Private; called only from the ClientRpc's
        // execute branch, so this never double-fires on the host.
        [HarmonyPatch(typeof(PlayerControllerB), "DamageOnOtherClients")]
        [HarmonyPostfix]
        private static void DamageOnOtherClientsPostfix(PlayerControllerB __instance, int damageNumber)
        {
            if (__instance == null || __instance.IsOwner)
                return;

            BloodFXDirector.OnPlayerDamaged(__instance, damageNumber, Vector3.zero, "observer");
        }

        // Player-vs-player and IHittable melee. Runs on every client and is the only vanilla
        // damage path that carries a direction, so stash it for the owner's inner DamagePlayer.
        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayerFromOtherClientClientRpc))]
        [HarmonyPrefix]
        private static void DamageFromOtherClientPrefix(Vector3 hitDirection)
        {
            BloodFXDirector.PendingHitDirection = hitDirection;
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayerFromOtherClientClientRpc))]
        [HarmonyPostfix]
        private static void DamageFromOtherClientPostfix(
            PlayerControllerB __instance,
            int damageAmount,
            Vector3 hitDirection)
        {
            // On the owner this already fired through DamagePlayer; the director's window
            // swallows the repeat, and it still covers the case where DamagePlayer bailed out.
            BloodFXDirector.OnPlayerDamaged(__instance, damageAmount, hitDirection, "fromOtherClient");
            BloodFXDirector.PendingHitDirection = Vector3.zero;
        }

        // Death, on every client.
        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayerClientRpc")]
        [HarmonyPostfix]
        private static void KillPlayerClientRpcPostfix(int playerId, bool spawnBody, Vector3 bodyVelocity)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.allPlayerScripts == null)
                return;
            if (playerId < 0 || playerId >= round.allPlayerScripts.Length)
                return;

            BloodFXDirector.OnPlayerKilled(round.allPlayerScripts[playerId], bodyVelocity, spawnBody);
        }

        // Vanilla blood pools. Suppressed by clearing leaveBlood rather than skipping the
        // method, because DropBlood also places snow footprints, which must keep working.
        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DropBlood))]
        [HarmonyPrefix]
        private static void DropBloodPrefix(ref bool leaveBlood)
        {
            if (!(BloodFXBootstrap.Enabled?.Value ?? false))
                return;
            if (!(BloodFXBootstrap.SuppressVanillaBlood?.Value ?? true))
                return;

            leaveBlood = false;
        }

        // Respawn: vanilla clears its own body decals here, so ours go with them.
        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.ResetPlayerBloodObjects))]
        [HarmonyPostfix]
        private static void ResetPlayerBloodObjectsPostfix(PlayerControllerB __instance, bool resetBodyBlood)
        {
            if (__instance == null || !resetBodyBlood)
                return;

            BloodFXDecalRegistry.ClearBody(__instance.GetInstanceID());
            BloodFXDecalRegistry.ClearSupplementalBodyChannels(__instance.GetInstanceID());
        }
    }

    /// <summary>
    /// Round lifecycle. Mirrors the hooks Contracts/_Shared/StartOfRoundPatches.cs already uses
    /// for per-round state, plus ReviveDeadPlayers, which is where vanilla itself resets blood.
    /// </summary>
    [HarmonyPatch]
    internal static class BloodFXRoundLifecyclePatches
    {
        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        [HarmonyPostfix]
        private static void OnShipLeave()
        {
            ClearRound();
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeaveAutomatically")]
        [HarmonyPostfix]
        private static void OnShipLeaveAutomatically()
        {
            ClearRound();
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ReviveDeadPlayers))]
        [HarmonyPostfix]
        private static void OnReviveDeadPlayers()
        {
            ClearRound();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void OnDisconnect()
        {
            ClearRound();
        }

        private static void ClearRound()
        {
            BloodFXDecalRegistry.ClearAll();
            BloodFXDirector.ResetTransientState();
        }
    }
}
