using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// After-rest buttons (Memorize Spell, Spell Mastery, Signature Spells and the mod's custom rest powers) run as functors
// on the computer of the player who clicks them only: the game's AfterRestActionItem calls IFunctorService locally,
// and only the functor itself can tell the other computers. The game's own functors do that (FunctorUsePower sends
// its power through ICommandService, the spell preparation screen sends PrepareSpells), the mod's didn't: they added
// and removed conditions and changed prepared spells directly, so only the clicker's game had them. A condition added
// that way also takes an object ID on one computer only, which is how desync issue 04 was found (a
// ConditionMemorizeSpell on one client, 2026-11-08).
//
// The changes those functors make to the hero now go through this command: it rides on the game's own
// AssignClassLevelRPC in place of a class name (like the level down and the end-infusion commands), so every computer
// runs it at the same tick. The functor on the clicker sends it, waits until its own copy has run, and only then
// carries on (opening the preparation screen, for example). Single player runs the change at once.
internal static class RestCommandContext
{
    private const string Marker = "UbRestCommand:";

    // a command that hasn't come back by then is given up, so a lost connection can't keep the rest screen waiting
    private const float WaitSeconds = 30f;

    // nonces of commands this computer has handled and whether they ran, so the sender can tell when its own copy has
    // run; bounded, as only the last few are ever waited for
    private static readonly Queue<string> AppliedOrder = new();
    private static readonly Dictionary<string, bool> Applied = new();
    private const int AppliedLimit = 64;

    private static readonly Dictionary<string, Action<RulesetCharacterHero, string>> Handlers = new();

    private static long _sequence;

    // a handler gets the hero and the command's argument; it runs on every computer at the command's tick, so it
    // must only use game state (never this computer's screens or input)
    internal static void Register(string verb, Action<RulesetCharacterHero, string> handler)
    {
        Handlers[verb] = handler;
    }

    // runs the command on every computer and waits (as a coroutine step) until it has run on this one; the result
    // says whether it did, so a caller can stop rather than carry on from a state the others don't have
    internal static IEnumerator Send(RulesetCharacterHero hero, string verb, string argument, Action<bool> done)
    {
        if (!Handlers.TryGetValue(verb, out var handler))
        {
            Main.Error($"[RestCommand] unknown command {verb}");
            done?.Invoke(false);
            yield break;
        }

        if (!Global.IsMultiplayer ||
            ServiceRepository.GetService<INetworkingService>() is not NetworkingManager { view: not null } networking)
        {
            handler(hero, argument ?? string.Empty);
            done?.Invoke(true);
            yield break;
        }

        // unique per sender: the actor number keeps two players' commands apart
        var nonce = $"{PhotonNetwork.LocalPlayer?.ActorNumber ?? 0}-{++_sequence}";

        networking.view.RPC(nameof(NetworkingManager.AssignClassLevelRPC), RpcTarget.AllViaServer,
            networking.CommandTick, (long)hero.Guid, $"{Marker}{nonce}:{verb}:{argument}", false);
        PhotonNetwork.SendAllOutgoingCommands();

        var deadline = Time.realtimeSinceStartup + WaitSeconds;

        while (!Applied.ContainsKey(nonce) && Global.IsMultiplayer && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        var ran = Applied.TryGetValue(nonce, out var succeeded) && succeeded;

        if (!ran)
        {
            Main.Error($"[RestCommand] {verb} for {hero.Name} didn't run here; not carrying on");
        }

        done?.Invoke(ran);
    }

    // AssignClassLevelAsync replacement for this command's marker (see MultiplayerPatcher)
    internal static bool TryHandleCommand(
        NetworkingManager networking, long heroGuid, string classDefinitionName, ref IEnumerator result)
    {
        if (classDefinitionName == null || !classDefinitionName.StartsWith(Marker))
        {
            return false;
        }

        // nonce:verb:argument, where the argument (a power name) may itself contain colons
        var parts = classDefinitionName.Substring(Marker.Length).Split([':'], 3);

        if (parts.Length != 3)
        {
            return false;
        }

        result = RunAsync(networking, heroGuid, parts[0], parts[1], parts[2]);

        return true;
    }

    private static IEnumerator RunAsync(
        NetworkingManager networking, long heroGuid, string nonce, string verb, string argument)
    {
        var hero = networking.GetEntityFromGuid<RulesetCharacterHero>(heroGuid, false);
        var ran = false;

        if (hero != null && Handlers.TryGetValue(verb, out var handler))
        {
            try
            {
                handler(hero, argument);
                ran = true;
            }
            catch (Exception ex)
            {
                Main.Error(ex);
            }
        }
        else
        {
            Main.Error($"[RestCommand] {verb} for hero {heroGuid} not run: hero or command not found");
        }

        // recorded even when it couldn't run, so the sender stops waiting; every computer skipped it alike
        Applied[nonce] = ran;
        AppliedOrder.Enqueue(nonce);

        while (AppliedOrder.Count > AppliedLimit)
        {
            Applied.Remove(AppliedOrder.Dequeue());
        }

        yield break;
    }
}
