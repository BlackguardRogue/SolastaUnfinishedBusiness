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
}
