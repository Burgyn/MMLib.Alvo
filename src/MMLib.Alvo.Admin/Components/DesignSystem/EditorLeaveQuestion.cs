namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// Something an editor would lose on any way out — its submit included — that its unsaved-changes question does not
/// name, asked instead of "Discard your changes?".
/// </summary>
/// <remarks>
/// <para>
/// <b>Cascaded to <c>AlvoEditor</c>, not a parameter of it.</b> The one case today is a credential token shown once
/// (final review M14): Save, Cancel and Escape all close the person editor, and a token not yet copied is gone with it.
/// A cascade of an internal type reaches the editor through a private property, so the public component gains no
/// member for it (spec D5's reason) and the page that knows whether the token was copied says so without the person
/// editor between them growing a parameter.
/// </para>
/// <para>
/// While one is cascaded the editor counts as holding a loss, so every way out asks it, and its submit runs only after
/// the operator chose to leave: a submit closes the editor too.
/// </para>
/// </remarks>
/// <param name="Title">The question, such as "Leave without copying the token?".</param>
/// <param name="Body">What is lost, first.</param>
/// <param name="Leave">The button that leaves anyway, naming what it gives up.</param>
internal sealed record EditorLeaveQuestion(string Title, string Body, string Leave)
{
    /// <summary>A credential token that was shown and not copied (final review M14).</summary>
    public static EditorLeaveQuestion UncopiedToken { get; } = new(
        "Leave without copying the token?",
        "It is shown only once. Closing this editor loses it, and a new one has to be issued.",
        "Leave without copying");
}
