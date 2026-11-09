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

    // A Prepare press of these features is a rest command that may take a moment to come back. Until it has, or for
    // LockSeconds at most (user, 2026-11-09: 10 seconds instead of 30):
    // - the feature's after-rest button stays off (user: "Use the button"): the feature's functor waits for it
    //   (IsPending) after the screen closes, and the game keeps an after-rest button off while its functor runs
    //   (AfterRestActionItem);
    // - on the screen that sent it, the spells, Prepare, Revert, Clear and Auto are off (user: "Yes, please do";
    //   IsPendingOn, SpellRepertoirePanelPatcher), and they come back with the choice still there if it's refused or
    //   gets no answer; the screen can still be closed;
    // - any other Prepare, from that screen or another one, does nothing but warn the player that their previous
    //   changes haven't been synced yet and to wait (user: "simply give them a warning"; WarnIfPending).
    // When the answer comes or LockSeconds pass, the press and its session are forgotten (Clear; user: "Ensure that
    // lock session ids are cleared"). A press with no answer may still apply later, alike on every computer.
    // The command runs on the game's networking object rather than the panel, so closing the screen doesn't stop it.
    private const float LockSeconds = 10f;

    private static float _pendingUntil = -1f;
    private static SpellRepertoirePanel _pendingPanel;
    private static int _pendingSession = -1;

    // numbers presses, so an answer only clears the press it belongs to
    private static int _press;

    // counts preparations started and cancelled on any panel, so a late answer only closes the preparation it came
    // from: one cancelled and another opened on the same panel meanwhile must be left alone (Codex's review of 8e6b4bbd)
    private static int _session;

    // OnPrepareSpellsCb and OnCancelPreparationCb postfixes
    internal static void NewSession()
    {
        _session++;
    }

    // whether a Prepare press is on its way; one past LockSeconds is forgotten here
    internal static bool IsPending
    {
        get
        {
            if (_pendingUntil >= 0f && Time.realtimeSinceStartup >= _pendingUntil)
            {
                Clear();
            }

            return _pendingUntil >= 0f;
        }
    }

    private static void Clear()
    {
        _pendingUntil = -1f;
        _pendingPanel = null;
        _pendingSession = -1;
    }

    // whether this panel's current preparation sent the press on its way, so its spells and buttons are off. The
    // session, not only the panel: a preparation opened on the same panel after a cancel is a new session and isn't
    // locked, so nothing has to turn its controls back on when the old answer arrives (Codex's review of 1518501c)
    internal static bool IsPendingOn(SpellRepertoirePanel panel)
    {
        return IsPending && panel && panel == _pendingPanel && _session == _pendingSession;
    }

    // a Prepare while a press is on its way: the warning, and true so the caller does nothing else
    internal static bool WarnIfPending()
    {
        if (!IsPending)
        {
            return false;
        }

        Gui.GuiService?.ShowAlert(Gui.Localize("Message/&SpellPreparationNotSynced"), Gui.ColorFailure, 6f);

        return true;
    }

    // RefreshPreparation postfix: the panel's selection buttons off while its press is out (Cancel stays, so the
    // screen can still be closed). Only the buttons the game's own refresh turns back on: the gamepad Clear and Auto
    // buttons only have their visibility set there, so turning them off would leave them off; their actions are
    // blocked by the Prepare hooks instead (Codex's review of 8e6b4bbd)
    internal static void RefreshButtons(SpellRepertoirePanel panel)
    {
        if (!IsPendingOn(panel))
        {
            return;
        }

        foreach (var button in new[]
                 {
                     panel.validatePreparationButton, panel.revertPreparationButton, panel.clearPreparationButton,
                     panel.automatePreparationButton
                 })
        {
            if (button)
            {
                button.interactable = false;
            }
        }
    }

    internal static void Submit(
        SpellRepertoirePanel panel, RulesetCharacterHero hero, string verb, string argument, Action ran)
    {
        var press = ++_press;
        var session = _session;
        var repertoire = panel.SpellRepertoire;

        _pendingUntil = Time.realtimeSinceStartup + LockSeconds;
        _pendingPanel = panel;
        _pendingSession = session;
        panel.RefreshPreparation(false);

        MonoBehaviour runner = ServiceRepository.GetService<INetworkingService>() as NetworkingManager;

        if (!runner)
        {
            runner = panel;
        }

        runner.StartCoroutine(RestCommandContext.Send(hero, verb, argument, outcome =>
        {
            // this press's answer (or its timeout): forget it, unless a later press has replaced it
            if (press == _press)
            {
                Clear();
            }

            if (outcome != RestCommandContext.Outcome.Ran)
            {
                // the spells and buttons come back, with the choice still on screen
                if (panel && panel.isActiveAndEnabled && _session == session)
                {
                    panel.RefreshPreparation(false);
                }

                return;
            }

            ran?.Invoke();

            // as the game's own Prepare closes the screen, if that preparation is still the one on screen
            if (panel && panel.isActiveAndEnabled && _session == session && panel.SpellRepertoire == repertoire)
            {
                panel.SpellPreparationValidated();
            }
        }, LockSeconds));
    }
}
