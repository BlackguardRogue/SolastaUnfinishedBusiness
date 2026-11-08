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
                GameLocationCharacter fromActor = null;
                var retarget = power.GetFirstSubFeatureOfType<IRetargetCustomRestPower>();
                if (retarget != null)
                {
                    fromActor = retarget.GetTarget(ruleChar);
                }

                fromActor ??= GameLocationCharacter.GetFromActor(ruleChar);

                var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();

                if (fromActor != null)
                {
                    functor._powerUsed = false;

                    var actionParams = new CharacterActionParams(fromActor, ActionDefinitions.Id.PowerNoCost)
                    {
                        ActionModifiers = { new ActionModifier() },
                        RulesetEffect = implementationService
                            .InstantiateEffectPower(fromActor.RulesetCharacter, usablePower, true),
                        UsablePower = usablePower,
                        TargetCharacters = { fromActor },
                        SkipAnimationsAndVFX = true
                    };

                    // through the command service, as the game's own FunctorUsePower does, so the power is used on
                    // every computer; IGameLocationActionService.ExecuteAction (used before) runs it on this one only
                    ServiceRepository.GetService<ICommandService>()
                        .ExecuteAction(actionParams, functor.ActionExecuted, false);

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
