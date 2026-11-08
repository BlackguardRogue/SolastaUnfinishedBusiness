using System.Collections;
using System.Collections.Generic;
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

    // retargeted rest powers finished, per hero and power: counted on every computer alike, so the clicker can wait
    // for the one it started
    private static readonly Dictionary<(ulong, string), int> RetargetedFinished = new();

    // runs on every computer at the same tick (RestCommandContext): a rest power the hero owns but another character
    // uses (IRetargetCustomRestPower, the Innovation Weapon's Steel Defender). The game's networked action can't carry
    // it: it sends the power as an index into the acting character's own powers, and the power is the hero's, so the
    // other computers would get none (Codex's review of PR #28). Instead every computer starts the same action itself,
    // as the clicker alone used to, on the character the clicker chose (by ID), and only if that is still the power's
    // target here.
    private static bool UseCustomRestPowerRetargeted(RulesetCharacterHero ruleChar, string argument)
    {
        var parts = argument.Split('|');

        if (parts.Length != 2 || !ulong.TryParse(parts[1], out var actorGuid) ||
            !TryGetCustomRestPower(parts[0], out var power) ||
            power.GetFirstSubFeatureOfType<IRetargetCustomRestPower>()?.GetTarget(ruleChar) is not { } actor ||
            actor.Guid != actorGuid)
        {
            Main.Error($"[RestCommand] {argument} for {ruleChar.Name}: not its target any more, power not used");

            return false;
        }

        var key = (ruleChar.Guid, power.Name);

        ServiceRepository.GetService<IGameLocationActionService>()
            .ExecuteAction(BuildRestPowerAction(actor, PowerProvider.Get(power, ruleChar)), _ =>
            {
                RetargetedFinished.TryGetValue(key, out var count);
                RetargetedFinished[key] = count + 1;
            }, false);

        return true;
    }

    private static bool TryGetCustomRestPower(string powerName, out FeatureDefinitionPower power)
    {
        power = PowerBundle.GetPower(powerName);

        return power || DatabaseHelper.TryGetDefinition(powerName, out power);
    }

    // runs on every computer at the same tick (RestCommandContext): a self power used from the rest screen while the
    // hero isn't on a map (travelling). It used to run on the clicker's computer only, so only that game spent the use.
    private static bool UseCustomRestPowerTravelling(RulesetCharacterHero ruleChar, string powerName)
    {
        if (!TryGetCustomRestPower(powerName, out var power))
        {
            return false;
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

        return true;
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

                if (retarget?.GetTarget(ruleChar) is { } target)
                {
                    // like the action path below, the rest screen carries on only once the power has been used
                    var key = (ruleChar.Guid, power.Name);
                    var finishedBefore = RetargetedFinished.TryGetValue(key, out var count) ? count : 0;
                    var outcome = RestCommandContext.Outcome.Failed;

                    yield return RestCommandContext.Send(ruleChar, nameof(UseCustomRestPowerRetargeted),
                        $"{power.Name}|{target.Guid}", x => outcome = x);

                    // bounded, like the command itself, so an action that never reports back can't hold the screen
                    var deadline = UnityEngine.Time.realtimeSinceStartup + 30f;

                    while (outcome == RestCommandContext.Outcome.Ran &&
                           UnityEngine.Time.realtimeSinceStartup < deadline &&
                           (RetargetedFinished.TryGetValue(key, out count) ? count : 0) == finishedBefore)
                    {
                        yield return null;
                    }

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
