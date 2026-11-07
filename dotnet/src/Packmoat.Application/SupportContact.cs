namespace Packmoat.Application;

/// <summary>
/// Who runs this Packmoat server and how a person reaches them for help (#186): the operator's name (PACKMOAT_OPERATOR_NAME) and support
/// address (PACKMOAT_SUPPORT_EMAIL). The host reads and checks both at start and passes this down; nothing here reads the environment.
/// Either may be unset (a self-hosted server): the sentences then use neutral words, never another operator's name or address.
/// </summary>
public sealed record SupportContact
{
    /// <summary>Neither is set: every sentence uses the neutral words.</summary>
    public static readonly SupportContact None = new(null, null);

    /// <summary>The operator, when no name is set, in the middle of a sentence.</summary>
    public const string NeutralOperator = "whoever runs Packmoat for you";

    /// <param name="operatorName">A name already checked by the host, or null.</param>
    /// <param name="email">An address already checked by the host, or null.</param>
    public SupportContact(string? operatorName, string? email)
    {
        OperatorName = string.IsNullOrWhiteSpace(operatorName) ? null : operatorName;
        Email = string.IsNullOrWhiteSpace(email) ? null : email;
    }

    /// <summary>The operator's name (for example Packmoat, or Contoso IT), or null.</summary>
    public string? OperatorName { get; }

    /// <summary>The support address, or null.</summary>
    public string? Email { get; }

    /// <summary>Mid-sentence: "email support@example.com", else "contact Contoso IT", else "contact whoever runs Packmoat for you".</summary>
    public string Phrase => Email is { } e ? "email " + e : "contact " + Operator;

    /// <summary><see cref="Phrase"/> at the start of a sentence: "Email support@example.com", "Contact Contoso IT", ...</summary>
    public string CapitalPhrase => char.ToUpperInvariant(Phrase[0]) + Phrase[1..];

    /// <summary>The operator mid-sentence: the name, or "whoever runs Packmoat for you".</summary>
    public string Operator => OperatorName ?? NeutralOperator;

    /// <summary>The operator at the start of a sentence: the name, or "Whoever runs Packmoat for you".</summary>
    public string OperatorSubject => OperatorName ?? char.ToUpperInvariant(NeutralOperator[0]) + NeutralOperator[1..];
}
