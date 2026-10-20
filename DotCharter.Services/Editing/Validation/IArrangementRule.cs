namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     A single arrangement validation rule. Each rule inspects the provided
///     <see cref="ArrangementValidationContext"/> and appends any issues it
///     detects to the supplied issues collection. Rules must be side-effect free with
///     respect to the arrangement — they produce issues, they do not mutate.
/// </summary>
public interface IArrangementRule
{
    void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues);
}
