using System.Reflection;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>MainWindowViewModel</c> is the shell's god class: every menu verb and its
///     services hang off one constructor. It is being split into per-surface view
///     models one cluster at a time, and this budget only moves DOWN — a new
///     dependency belongs on the surface that uses it, not on the shell.
/// </summary>
public sealed class MainWindowViewModelDependencyBudgetTests
{
    private const int ConstructorParameterCeiling = 35;

    [Fact]
    public void TheShellTakesNoMoreDependenciesThanItsBudget()
    {
        var count = ConstructorParameterCount();

        Assert.True(count <= ConstructorParameterCeiling,
            $"MainWindowViewModel takes {count} constructor parameters, over its budget of " +
            $"{ConstructorParameterCeiling}. Give the new dependency to the view model of the surface " +
            "that uses it (as StemSeparationViewModel, HelpMenuViewModel, ImportMenuViewModel, ToneRigViewModel, ExportMenuViewModel and ProjectMenuViewModel took theirs) instead of the shell.");
        Assert.True(count == ConstructorParameterCeiling,
            $"MainWindowViewModel now takes {count} constructor parameters; lower " +
            $"{nameof(ConstructorParameterCeiling)} from {ConstructorParameterCeiling} to {count} so it cannot creep back.");
    }

    private static int ConstructorParameterCount()
    {
        var constructors = typeof(MainWindowViewModel).GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        Assert.Single(constructors);
        return constructors[0].GetParameters().Length;
    }
}
