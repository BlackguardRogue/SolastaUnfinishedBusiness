using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ActiveCharacterPanelPatcher
{
    // What each panel subscribed to, and on which character. Unbind removes exactly these, from that character, so it
    // doesn't depend on the character's state or on what the panel points at by then. Before, Unbind tested the health
    // Bind tested: a hero downed while bound kept the handlers, and rebinding after recovery could add a second set.
    // Unbind removes the entry, which matters: on the game's runtime this table never lets go of what's left in it. The
    // game has only a few panels, so one destroyed without an Unbind costs little.
    private static readonly ConditionalWeakTable<ActiveCharacterPanel, PanelSubscription> Subscriptions = new();

    private static void Subscribe(ActiveCharacterPanel panel, RulesetCharacter character)
    {
        Unsubscribe(panel);

        var subscription = new PanelSubscription
        {
            Character = character,
            Refreshed = panel.ConcentrationChanged,
            // Upstream added this to update IconsOnPortrait on power use (its commit's title), by calling RefreshAll.
            // RefreshAll isn't only a redraw: it recalculates the character and, through the light condition, can use an
            // object ID. This handler runs only on a computer whose panel shows the hero, so that computer's IDs moved
            // ahead of the others' (object-ID desyncs, cause 2). The counters themselves are written on every computer
            // by UsePower, so only this computer's display needs updating here.
            // Marked for redraw, not redrawn now: PowerActivated fires inside UsePower, before the mod's postfix spends
            // a transformed hero's Rage or Ki. GuiPanel.Update redraws a marked panel at its next update.
            PowerActivated = (_, _, _) => panel.Dirty = true
        };

        character.CharacterRefreshed += subscription.Refreshed;
        character.PowerActivated += subscription.PowerActivated;
        Subscriptions.Add(panel, subscription);
        ObjectIdDiagnosticsContext.RecordPanelHandlers(character, "panel bound");
    }

    private static void Unsubscribe(ActiveCharacterPanel panel)
    {
        if (!Subscriptions.TryGetValue(panel, out var subscription))
        {
            return;
        }

        Subscriptions.Remove(panel);
        subscription.Character.CharacterRefreshed -= subscription.Refreshed;
        subscription.Character.PowerActivated -= subscription.PowerActivated;
        ObjectIdDiagnosticsContext.RecordPanelHandlers(subscription.Character, "panel unbound");
    }

    private sealed class PanelSubscription
    {
        internal RulesetCharacter Character;
        internal RulesetCharacter.PowerActivatedHandler PowerActivated;
        internal RulesetCharacter.CharacterRefreshedHandler Refreshed;
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ActiveCharacterPanel __instance)
        {
            //prevent null check issues
            return __instance.GuiCharacter?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false };
        }

        [UsedImplicitly]
        public static void Postfix(ActiveCharacterPanel __instance)
        {
            if (__instance.GuiCharacter?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false })
            {
                return;
            }

            //PATCH: support for custom point pools and concentration powers on portrait
            IconsOnPortrait.CharacterPanelRefresh(__instance);

            //PATCH: support for button that shows info about non-Hero characters
            if (Main.Settings.ShowButtonWithControlledMonsterInfo &&
                __instance.GuiCharacter.RulesetCharacter is RulesetCharacterMonster)
            {
                CustomCharacterStatsPanel.Instance.Refresh();
            }
        }
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ActiveCharacterPanel __instance)
        {
            //PATCH: properly update IconsOnPortrait
            var character = __instance.GuiCharacter?.RulesetCharacter;

            if (character is { IsDeadOrDyingOrUnconscious: false } and not RulesetCharacterEffectProxy)
            {
                Subscribe(__instance, character);
            }

            //PATCH: support a better ratio with custom portraits
            if (Main.Settings.EnableCustomPortraits &&
                PortraitsContext.HasCustomPortrait(__instance.GuiCharacter?.RulesetCharacter))
            {
                __instance.characterPortrait.rectTransform.sizeDelta = new Vector2(164, 247);
                __instance.characterPortrait.rectTransform.anchoredPosition = new Vector2(-48, 0);
            }
            else
            {
                __instance.characterPortrait.rectTransform.sizeDelta = new Vector2(212, 247);
                __instance.characterPortrait.rectTransform.anchoredPosition = new Vector2(0, 0);
            }

            //PATCH: support for button that shows info about non-Hero characters
            if (!Main.Settings.ShowButtonWithControlledMonsterInfo
                || __instance.GuiCharacter?.RulesetCharacter is not RulesetCharacterMonster)
            {
                return;
            }

            CustomCharacterStatsPanel.Instance.Bind(__instance.GuiCharacter.RulesetCharacter);
        }
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ActiveCharacterPanel __instance)
        {
            //PATCH: properly update IconsOnPortrait
            Unsubscribe(__instance);
        }

        [UsedImplicitly]
        public static void Postfix()
        {
            //PATCH: support for button that shows info about non-Hero characters
            if (Main.Settings.ShowButtonWithControlledMonsterInfo)
            {
                CustomCharacterStatsPanel.Instance.Unbind();
            }
        }
    }
}
