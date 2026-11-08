using System.Collections;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class PowerBundleContext
{
    internal const string UseCustomRestPowerFunctorName = "UseCustomRestPower";

    internal static void Load()
    {
        ServiceRepository.GetService<IFunctorService>()
            .RegisterFunctor(UseCustomRestPowerFunctorName, new FunctorUseCustomRestPower());
        RestCommandContext.Register(nameof(UseCustomRestPowerTravelling), UseCustomRestPowerTravelling);
        RestCommandContext.Register(nameof(UseCustomRestPowerRetargeted), UseCustomRestPowerRetargeted);
    }

    // the action a self power from the rest screen runs as: used by the actor on itself, without animations
    private static CharacterActionParams BuildRestPowerAction(
        GameLocationCharacter actor, RulesetUsablePower usablePower)
    {
        return new CharacterActionParams(actor, ActionDefinitions.Id.PowerNoCost)
        {
            ActionModifiers = { new ActionModifier() },
            RulesetEffect = ServiceRepository.GetService<IRulesetImplementationService>()
                .InstantiateEffectPower(actor.RulesetCharacter, usablePower, true),
            UsablePower = usablePower,
            TargetCharacters = { actor },
            SkipAnimationsAndVFX = true
        };
    }

    // runs on every computer at the same tick (RestCommandContext): a rest power the hero owns but another character
    // uses (IRetargetCustomRestPower, the Innovation Weapon's Steel Defender). The game's networked action can't carry
    // it: it sends the power as an index into the acting character's own powers, and the power is the hero's, so the
    // other computers would get none (Codex's review of PR #28). Instead every computer starts the same action itself,
    // as the clicker alone used to.
    private static void UseCustomRestPowerRetargeted(RulesetCharacterHero ruleChar, string powerName)
    {
        if (!TryGetCustomRestPower(powerName, out var power) ||
            power.GetFirstSubFeatureOfType<IRetargetCustomRestPower>()?.GetTarget(ruleChar) is not { } actor)
        {
            Main.Error($"[RestCommand] {powerName} for {ruleChar.Name}: no character to use it");

            return;
        }

        ServiceRepository.GetService<IGameLocationActionService>()
            .ExecuteAction(BuildRestPowerAction(actor, PowerProvider.Get(power, ruleChar)), null, false);
    }

    private static bool TryGetCustomRestPower(string powerName, out FeatureDefinitionPower power)
    {
        power = PowerBundle.GetPower(powerName);

        return power || DatabaseHelper.TryGetDefinition(powerName, out power);
    }

    // runs on every computer at the same tick (RestCommandContext): a self power used from the rest screen while the
    // hero isn't on a map (travelling). It used to run on the clicker's computer only, so only that game spent the use.
    private static void UseCustomRestPowerTravelling(RulesetCharacterHero ruleChar, string powerName)
    {
        if (!TryGetCustomRestPower(powerName, out var power))
        {
            return;
        }

        var usablePower = PowerProvider.Get(power, ruleChar);
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();
        var formsParams = new RulesetImplementationDefinitions.ApplyFormsParams();

        formsParams.FillSourceAndTarget(ruleChar, ruleChar);
        formsParams.FillFromActiveEffect(implementationService
            .InstantiateEffectPower(ruleChar, usablePower, false));
        formsParams.effectSourceType = EffectSourceType.Power;

        ruleChar.UsePower(usablePower);
        ruleChar.LogCharacterUsedPower(power, $"Feedback/&{power.Name}UsedWhileTravellingFormat");
    }

    private sealed class FunctorUseCustomRestPower : Functor
    {
        private bool _powerUsed;

        public override IEnumerator Execute(
            [NotNull] FunctorParametersDescription functorParameters,
            FunctorExecutionContext context)
        {
            var functor = this;
            var powerName = functorParameters.StringParameter;
            if (!TryGetCustomRestPower(powerName, out var power))
            {
                yield break;
            }

            var ruleChar = functorParameters.RestingHero;
            var usablePower = PowerProvider.Get(power, ruleChar);

            if (power.EffectDescription.TargetType == TargetType.Self)
            {
                var retarget = power.GetFirstSubFeatureOfType<IRetargetCustomRestPower>();

                if (retarget?.GetTarget(ruleChar) != null)
                {
                    yield return RestCommandContext.Send(
                        ruleChar, nameof(UseCustomRestPowerRetargeted), power.Name, null);

                    yield break;
                }

                var fromActor = GameLocationCharacter.GetFromActor(ruleChar);

                if (fromActor != null)
                {
                    functor._powerUsed = false;

                    // through the command service, as the game's own FunctorUsePower does, so the power is used on
                    // every computer; IGameLocationActionService.ExecuteAction (used before) runs it on this one only
                    ServiceRepository.GetService<ICommandService>()
                        .ExecuteAction(BuildRestPowerAction(fromActor, usablePower), functor.ActionExecuted, false);

                    while (!functor._powerUsed)
                    {
                        yield return null;
                    }
                }
                else
                {
                    yield return RestCommandContext.Send(
                        ruleChar, nameof(UseCustomRestPowerTravelling), power.Name, null);
                }
            }

            Trace.LogWarning("Unable to assign targets to power");
        }

        private void ActionExecuted(CharacterAction action)
        {
            _powerUsed = true;
        }
    }
}
