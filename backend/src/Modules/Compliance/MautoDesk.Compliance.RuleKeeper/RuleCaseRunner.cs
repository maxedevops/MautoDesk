using System.Text.Json;
using MautoDesk.Compliance.Domain;

namespace MautoDesk.Compliance.RuleKeeper;

/// <summary>
/// Runs one stored test case through the real evaluator.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as the integration harness in
/// <c>RegulatoryRuleTests</c>, including the fictional calendar: a rule carrying
/// a business-day deadline must be runnable before the jurisdiction's real
/// holidays exist, and these cases never assert on the computed date. Whether the
/// real calendar is loaded is a separate check, and it is the one that blocks the
/// approval.
/// </para>
/// <para>
/// This exists so the tool's verdict and CI's verdict come from one engine. A
/// second implementation of "does this rule fire" is a second answer waiting to
/// disagree with the first.
/// </para>
/// </remarks>
public static class RuleCaseRunner
{
    /// <summary>The worst status the rule produces for one case's facts.</summary>
    public static ComplianceStatus Worst(RegulatoryRuleVersion rule, string inputJson)
    {
        var run = RuleEvaluator.Evaluate(new EvaluationRequest
        {
            TriggerEvent = rule.TriggerEvent,
            JurisdictionCode = rule.JurisdictionCode,
            EvaluatedOn = rule.EffectiveFrom.AddDays(1),
            RulePackVersion = "rulekeeper",
            Context = BuildContext(inputJson),
            Rules = [rule],
            Calendar = BusinessDayCalendar.ForJurisdiction(
                rule.JurisdictionCode, [new DateOnly(2026, 1, 1)]),
        });

        var worst = ComplianceStatus.Pass;

        foreach (var result in run.Results)
        {
            if (Rank(result.Status) > Rank(worst))
            {
                worst = result.Status;
            }
        }

        return worst;
    }

    /// <summary>
    /// Turns a case's flat fact map into a context.
    /// </summary>
    /// <remarks>
    /// <c>_satisfied</c> is the one reserved key: an array of requirement codes
    /// the case treats as already met. Everything else is a fact path.
    /// </remarks>
    private static RegulatoryContext BuildContext(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        var builder = RegulatoryContext.Create();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "_satisfied")
            {
                foreach (var code in property.Value.EnumerateArray())
                {
                    builder.Satisfying(code.GetString()!);
                }

                continue;
            }

            builder.With(property.Name, property.Value.ToString());
        }

        return builder.Build();
    }

    private static int Rank(ComplianceStatus status) => status switch
    {
        ComplianceStatus.Block => 3,
        ComplianceStatus.Review => 2,
        ComplianceStatus.Warning => 1,
        _ => 0,
    };
}
