using System;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Opens the spell preparation screen on a given repertoire. The game's CharacterInspectionScreen.ShowSpellPreparation
// takes a repertoire but doesn't use it: it walks the hero's repertoires and starts preparation on the last prepared one
// (CIL of 1.5.97). On a Wizard multiclassed with a later prepared class (a Cleric), Memorize Spell, Spell Mastery and
// Signature Spells would then open the Cleric's spells (Codex's review of PR #28). This lets the game open the screen
// as usual and, if it chose another repertoire, cancels that preparation (which changes nothing: the panel goes back to
// its repertoire's own list) and starts it on the one asked for. Only the mod's Wizard features use it; the game's own
// preparation is unchanged.
internal static class SpellPreparationScreen
{
    internal static void Show(
        CharacterInspectionScreen screen, RulesetCharacterHero hero, GuiScreen previousScreen,
        RulesetSpellRepertoire repertoire)
    {
        screen.ShowSpellPreparation(hero, previousScreen, repertoire);

        // the screen's toggles: some fixed panels, then one per repertoire in the hero's order
        var toggle = hero.SpellRepertoires.IndexOf(repertoire) + screen.staticTogglesNumber;

        if (toggle < screen.staticTogglesNumber || toggle >= screen.inspectionPanels.Count ||
            screen.inspectionPanels[toggle] is not SpellRepertoirePanel wanted || screen.currentToggle == toggle)
        {
            return;
        }

        if (screen.currentToggle >= 0 && screen.currentToggle < screen.inspectionPanels.Count &&
            screen.inspectionPanels[screen.currentToggle] is SpellRepertoirePanel chosen)
        {
            chosen.OnCancelPreparationCb();
        }

        screen.ToggleValueChanged(toggle);
        wanted.OnPrepareSpellsCb();
    }

    // A Prepare press of these features is a rest command that may take a moment to come back. Until it has, the
    // feature's after-rest button stays off (user, 2026-11-09: "Use the button"): the feature's functor waits for it
    // (IsPending) after the screen closes, and the game keeps an after-rest button off while its functor runs
    // (AfterRestActionItem). So the screen can't be reopened to send another choice while one is on its way; the
    // screen itself isn't locked, and a second press on the same screen is refused by the command (see the features).
    // The command runs on the game's networking object rather than the panel, so closing the screen doesn't stop it.
    // A press that hasn't come back after RestCommandContext's wait frees the button again; it may still apply later,
    // alike on every computer.
    private static float _pendingUntil = -1f;

    // whether a Prepare press is on its way
    internal static bool IsPending => Time.realtimeSinceStartup < _pendingUntil;

    internal static void Submit(
        SpellRepertoirePanel panel, RulesetCharacterHero hero, string verb, string argument, Action ran)
    {
        // a little longer than the command's own wait, in case its coroutine stopped
        _pendingUntil = Time.realtimeSinceStartup + 35f;

        MonoBehaviour runner = ServiceRepository.GetService<INetworkingService>() as NetworkingManager;

        if (!runner)
        {
            runner = panel;
        }

        runner.StartCoroutine(RestCommandContext.Send(hero, verb, argument, outcome =>
        {
            _pendingUntil = -1f;

            if (outcome != RestCommandContext.Outcome.Ran)
            {
                return;
            }

            ran?.Invoke();

            // as the game's own Prepare closes the screen, if it's still open
            if (panel && panel.isActiveAndEnabled)
            {
                panel.SpellPreparationValidated();
            }
        }));
    }
}
