using System.Text.RegularExpressions;

namespace Zazi.IntegrationTests;

/// <summary>
/// That every form on a statically-rendered page can actually receive what was typed into it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the "add a worker" form never worked. Its model was a plain field rather
/// than one bound with <c>[SupplyParameterFromForm]</c>, and these pages render statically:
/// submitting rebuilds the component from scratch, so the field came back empty and the typed
/// values were gone before the handler ran. Every attempt was refused with "Choose a branch for
/// this worker" regardless of the branch chosen, and no worker was ever created in production.
/// </para>
/// <para>
/// No existing test could catch it. The bUnit tests render <i>interactively</i>, where a plain
/// field does persist across a submit — so they exercised behaviour that cannot occur in a
/// browser and passed against a form that was completely broken.
/// </para>
/// <para>
/// Checked against the source rather than a rendered page, deliberately. The defect is the
/// absence of an attribute, and the only way to see an absence is to look where it should be.
/// </para>
/// </remarks>
public class StaticFormBindingTests
{
    private static string PagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Zazi.Web")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Zazi.Web", "Components", "Pages");
    }

    public static TheoryData<string> PagesWithForms()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(PagesDirectory(), "*.razor"))
        {
            if (File.ReadAllText(file).Contains("<EditForm", StringComparison.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PagesWithForms))]
    public void EveryEditFormModelIsBoundToThePostedForm(string page)
    {
        var source = File.ReadAllText(Path.Combine(PagesDirectory(), page));

        // The model each form renders, e.g. Model="_newWorker".
        var models = Regex.Matches(source, @"<EditForm[^>]*Model=""(?<model>[^""]+)""")
            .Select(m => m.Groups["model"].Value.TrimStart('@'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(models);

        foreach (var model in models)
        {
            // The declaration must carry [SupplyParameterFromForm]. Without it the value is a
            // fresh empty object by the time the handler runs, and the form silently discards
            // everything typed into it.
            var declaration = Regex.Match(
                source,
                @"\[SupplyParameterFromForm[^\]]*\]\s*(?:private|public|internal)[^;{]*\b"
                + Regex.Escape(model) + @"\b");

            Assert.True(
                declaration.Success,
                $"{page}: the form bound to '{model}' does not declare it with "
                + "[SupplyParameterFromForm]. These pages render statically, so submitting the "
                + "form rebuilds the component and an unbound model arrives empty — the form "
                + "will appear to work and silently discard everything typed into it.");
        }
    }

    [Theory]
    [MemberData(nameof(PagesWithForms))]
    public void EveryFormBindsToADistinctFormName(string page)
    {
        var source = File.ReadAllText(Path.Combine(PagesDirectory(), page));

        var formNames = Regex.Matches(source, @"<EditForm[^>]*FormName=""(?<name>[^""]+)""")
            .Select(m => m.Groups["name"].Value)
            .ToList();

        var boundNames = Regex.Matches(source, @"\[SupplyParameterFromForm\(FormName = ""(?<name>[^""]+)""\)\]")
            .Select(m => m.Groups["name"].Value)
            .ToList();

        // A page with two forms needs two names, and each model must name the form it belongs
        // to — otherwise submitting one form rehydrates the other's model.
        Assert.Equal(formNames.Count, formNames.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in formNames)
        {
            Assert.Contains(name, boundNames);
        }
    }
}
