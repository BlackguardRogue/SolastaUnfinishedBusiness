using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameManagerPatcher
{
    //PATCH: nothing static keeps a finished game in memory (leaving a game, or a load that replaces it)
    // DestroyGame only starts the teardown, which goes on to read effects' descriptions as it unloads the location, so this
    // has to wait for the game's GameDestroyed event, the last thing that teardown does.
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.DestroyGame))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DestroyGame_Patch
    {
        private static bool _subscribed;

        [UsedImplicitly]
        public static void Prefix(GameManager __instance)
        {
            // the game manager lasts as long as the application, so once is enough
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
            __instance.GameDestroyed += OnGameDestroyed;
        }

        private static void OnGameDestroyed()
        {
            PowerBundle.ClearLiveEffects();
            MultiplayerDesyncContext.GameDestroyed();
            ActiveCharacterPanelPatcher.ForgetDestroyedPanels();
            AiMoveLogContext.ForgetGame();
            PowerBundleContext.ForgetGame();
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.BindPostDatabase))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindPostDatabase_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            //PATCH: loads all mod contexts
            BootContext.Startup();
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.BindServiceSettings))]
    internal static class BindServiceSettings_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameManager __instance)
        {
            //PATCH: add unofficial languages before game tries to load the game settings xml
            var languageByCode = __instance.languageByCode;

            if (languageByCode == null)
            {
                return;
            }

            foreach (var language in TranslatorContext.Languages
                         .Where(language => !languageByCode.ContainsKey(language.Code)))
            {
                languageByCode.Add(language.Code, language.Text);
            }
        }
    }
}
