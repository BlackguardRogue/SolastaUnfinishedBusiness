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

    private static readonly Dictionary<string, Func<RulesetCharacterHero, string, bool>> Handlers = new();

    private static long _sequence;

    // a handler gets the hero and the command's argument and says whether it did what was asked; it runs on every
    // computer at the command's tick, so it must only use game state (never this computer's screens or input), and
    // every computer gets the same answer
    internal static void Register(string verb, Func<RulesetCharacterHero, string, bool> handler)
    {
        Handlers[verb] = handler;
    }

    // a fresh ID for something several commands belong to (a preparation started and ended), unique per sender
    internal static string NewTransactionId()
    {
        return $"{PhotonNetwork.LocalPlayer?.ActorNumber ?? 0}-t{++_sequence}";
    }

    // the actor number of the player whose computer made a transaction ID (0 in single player)
    internal static int SenderOf(string transactionId)
    {
        var dash = transactionId?.IndexOf('-') ?? -1;

        return dash > 0 && int.TryParse(transactionId.Substring(0, dash), out var actor) ? actor : 0;
    }

    // sends a command without waiting for it: the host uses this to clean up after a player who left
    internal static void Post(RulesetCharacterHero hero, string verb, string argument)
    {
        if (!Global.IsMultiplayer)
        {
            if (Handlers.TryGetValue(verb, out var handler))
            {
                handler(hero, argument ?? string.Empty);
            }

            return;
        }

        if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager { view: not null } networking)
        {
            Main.Error($"[RestCommand] {verb} for {hero.Name} not sent: no network in a multiplayer game");

            return;
        }

        var nonce = $"{PhotonNetwork.LocalPlayer?.ActorNumber ?? 0}-{++_sequence}";

        networking.view.RPC(nameof(NetworkingManager.AssignClassLevelRPC), RpcTarget.AllViaServer,
            networking.CommandTick, (long)hero.Guid, $"{Marker}{nonce}:{verb}:{argument}", false);
        PhotonNetwork.SendAllOutgoingCommands();
    }

    // the outcome of a command on this computer
    internal enum Outcome
    {
        // ran here, so it ran (or will run) on every computer at the same tick
        Ran,

        // not run here and not anywhere: never sent, or every computer skipped it alike (hero or command not found)
        Failed,

        // sent but not back after WaitSeconds: it may still run later on every computer, so a caller that started a
        // change must send whatever undoes it (commands from one sender run in the order sent)
        TimedOut
    }

    // runs the command on every computer and waits (as a coroutine step) until it has run on this one; the outcome
    // tells the caller whether to carry on, stop, or stop and undo
    internal static IEnumerator Send(RulesetCharacterHero hero, string verb, string argument, Action<Outcome> done)
    {
        if (!Handlers.TryGetValue(verb, out var handler))
        {
            Main.Error($"[RestCommand] unknown command {verb}");
            done?.Invoke(Outcome.Failed);
            yield break;
        }

        if (!Global.IsMultiplayer)
        {
            done?.Invoke(handler(hero, argument ?? string.Empty) ? Outcome.Ran : Outcome.Failed);
            yield break;
        }

        // multiplayer without the game's networking: running it here alone is exactly the desync this avoids
        if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager { view: not null } networking)
        {
            Main.Error($"[RestCommand] {verb} for {hero.Name} not sent: no network in a multiplayer game");
            done?.Invoke(Outcome.Failed);
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

        Outcome outcome;

        if (!Applied.TryGetValue(nonce, out var ran))
        {
            outcome = Outcome.TimedOut;
            Main.Error($"[RestCommand] {verb} for {hero.Name} hasn't run here after {WaitSeconds} s; not carrying on");
        }
        else if (!ran)
        {
            outcome = Outcome.Failed;
            Main.Error($"[RestCommand] {verb} for {hero.Name} couldn't run; not carrying on");
        }
        else
        {
            outcome = Outcome.Ran;
        }

        done?.Invoke(outcome);
    }

    // AssignClassLevelAsync replacement for this command's marker (see MultiplayerPatcher)
    internal static bool TryHandleCommand(
        NetworkingManager networking, long heroGuid, string classDefinitionName, ref IEnumerator result)
    {
        if (classDefinitionName == null || !classDefinitionName.StartsWith(Marker))
        {
            return false;
        }

        // nonce:verb:argument, where the argument may itself contain colons
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
                ran = handler(hero, argument);
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

        // recorded even when it didn't run, so the sender stops waiting; every computer got the same answer
        Applied[nonce] = ran;
        AppliedOrder.Enqueue(nonce);

        while (AppliedOrder.Count > AppliedLimit)
        {
            Applied.Remove(AppliedOrder.Dequeue());
        }

        yield break;
    }
}
