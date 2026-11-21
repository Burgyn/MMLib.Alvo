using Microsoft.Extensions.Options;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Host.Internal;

/// <summary>
/// How the container's process ends: which failures are an operator's to fix, what they read, and the exit
/// code they get.
/// </summary>
/// <remarks>
/// <para>
/// <b>78, not 1, and not a crash artefact.</b> <c>sysexits.h</c>'s <c>EX_CONFIG</c> is the established code for
/// "something was found in an unconfigured or misconfigured state", and Alvo adopts known conventions rather
/// than inventing a variant. A fixed <c>1</c> would be indistinguishable from every other failure, which is
/// precisely the information #132 says was lost; 78 lets a deployment script, a CI job or an orchestrator hook
/// branch on "an operator has to change something" versus "retrying might help". It also sits below the shell's
/// reserved range (126 not executable, 127 not found, 128+n a signal), so it cannot be misread as a signal the
/// way the observed 139 was misread as a segmentation fault.
/// </para>
/// <para>
/// <b>A named predicate, not a general catch.</b> #132 is explicit that everything else keeps propagating, and
/// that is worth keeping: an unhandled exception still produces the runtime's own report and whatever crash
/// dump the deployment configured, which a blanket <c>catch</c> would take away for every genuine defect. So
/// exactly three shapes are recognized, and the fact that pins this asserts an unrelated exception is
/// <em>not</em> one of them.
/// </para>
/// </remarks>
internal static class AlvoHostExit
{
    /// <summary>The process exited because it was asked to stop.</summary>
    internal const int Success = 0;

    /// <summary>
    /// <c>EX_CONFIG</c> from <c>sysexits.h</c>: the configuration is wrong and no retry will fix it.
    /// </summary>
    internal const int ConfigurationFailure = 78;

    /// <summary>
    /// The <see cref="Exception.Data"/> key the boot tags a stage-0 descriptor refusal with — spelled here rather than
    /// read from the core, whose internals the host does not reference; a fact pins it to the core's spelling.
    /// </summary>
    internal const string StartupRefusalDataKey = "MMLib.Alvo.StartupRefusal";

    /// <summary>
    /// Whether <paramref name="failure"/> is a misconfiguration an operator can act on, rather than a defect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OptionsValidationException"/> covers every option value the host or the framework refused —
    /// the descriptor path, the driver name, a dev-key scope, the startup mode — because that is the one type
    /// <c>ValidateOnStart</c> raises and the one
    /// <see cref="AlvoHostConfiguration.Refuse"/> raises for the driver chosen during registration.
    /// </para>
    /// <para>
    /// <see cref="AlvoStartupRefusedException"/> covers the boot's own refusals: drift under
    /// <c>Alvo__Schema__Startup=Verify</c>, and a plan that would discard data. That type exists precisely so
    /// its <see cref="Exception.Message"/> is written for the operator reading a container log, so letting it
    /// print a stack trace instead would defeat the reason it exists.
    /// </para>
    /// <para>
    /// <b>A <see cref="DescriptorValidationException"/> only when the boot tagged it as stage 0's</b> (#340, #547): a
    /// mounted descriptor the boot refuses before touching the database — mistyped, or written for a newer Alvo. The
    /// boot puts the operator's text, naming the running build and both fixes, in its <see cref="Exception.Data"/> under
    /// <see cref="StartupRefusalDataKey"/>, and that text is what is printed. One the apply raises — rows a new check
    /// would break, an engine that declines a feature, an overlap the database could not count — carries no tag: it
    /// depends on the database and can be transient, so it is not claimed as <c>EX_CONFIG</c>.
    /// </para>
    /// <para>
    /// <b>And an <see cref="AggregateException"/> over nothing but those, because that is the shape two
    /// refused option types take.</b> <c>StartupValidator.Validate()</c> collects every failing
    /// <c>ValidateOnStart</c> registration and throws one aggregate over them, and the host now has two —
    /// its own <see cref="AlvoHostOptions"/> and the identity package's. Today
    /// <see cref="AlvoHost.BuildAsync"/> reads <c>IOptions&lt;AlvoHostOptions&gt;</c> before anything
    /// starts, so the plain refusal always wins; that ordering is an implementation detail of the
    /// composition, not a guarantee, and a deployment must not lose its exit code the day it changes.
    /// </para>
    /// <para>
    /// <b>All of the inner exceptions, never any.</b> An aggregate carrying a genuine defect beside a
    /// refusal is a defect, and keeps the runtime's own report and crash dump — otherwise this widening
    /// would quietly undo the rule the paragraphs above state. An <em>empty</em> aggregate is rejected for
    /// the same reason: "all of nothing" is vacuously true, and a failure naming nothing is not something
    /// an operator can fix.
    /// </para>
    /// </remarks>
    /// <param name="failure">What escaped the start.</param>
    internal static bool IsConfigurationFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure switch
        {
            OptionsValidationException or AlvoStartupRefusedException => true,
            DescriptorValidationException refused => OperatorRefusal(refused) is not null,
            AggregateException collected => collected.InnerExceptions.Count > 0
                && collected.InnerExceptions.All(IsConfigurationFailure),
            _ => false,
        };
    }

    /// <summary>What the operator reads on stderr before the process exits.</summary>
    /// <remarks>
    /// <see cref="OptionsValidationException.Message"/> is deliberately not used: it joins its failures with
    /// <c>"; "</c>, which runs two multi-line refusals into one unreadable line. Reporting
    /// <see cref="OptionsValidationException.Failures"/> separately is what lets a container with two things
    /// wrong be fixed in one restart.
    /// <para>
    /// An <see cref="AggregateException"/> is unwrapped for exactly that reason: its own
    /// <see cref="Exception.Message"/> is "One or more errors occurred", which names neither of the two
    /// option types that were refused.
    /// </para>
    /// </remarks>
    /// <param name="failure">A failure <see cref="IsConfigurationFailure"/> accepted.</param>
    internal static string Describe(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure switch
        {
            OptionsValidationException validation => string.Join(ParagraphBreak, validation.Failures),
            DescriptorValidationException refused => OperatorRefusal(refused) ?? refused.Message,
            AggregateException collected => string.Join(
                ParagraphBreak, collected.InnerExceptions.Select(Describe)),
            _ => failure.Message,
        };
    }

    private static string ParagraphBreak => Environment.NewLine + Environment.NewLine;

    /// <summary>
    /// The operator's text, only when the tag is a string: anything else under the key — absent, or put there by code that
    /// is not the boot — is not a refusal the boot wrote for an operator, and is not claimed as <c>EX_CONFIG</c>.
    /// </summary>
    /// <param name="refused">The refusal.</param>
    private static string? OperatorRefusal(DescriptorValidationException refused) =>
        refused.Data[StartupRefusalDataKey] as string;
}
