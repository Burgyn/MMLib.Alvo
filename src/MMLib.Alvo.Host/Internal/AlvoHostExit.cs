using Microsoft.Extensions.Options;
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
/// exactly two shapes are recognized, and the fact that pins this asserts an unrelated exception is
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
    /// </remarks>
    /// <param name="failure">What escaped the start.</param>
    internal static bool IsConfigurationFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure is OptionsValidationException or AlvoStartupRefusedException;
    }

    /// <summary>What the operator reads on stderr before the process exits.</summary>
    /// <remarks>
    /// <see cref="OptionsValidationException.Message"/> is deliberately not used: it joins its failures with
    /// <c>"; "</c>, which runs two multi-line refusals into one unreadable line. Reporting
    /// <see cref="OptionsValidationException.Failures"/> separately is what lets a container with two things
    /// wrong be fixed in one restart.
    /// </remarks>
    /// <param name="failure">A failure <see cref="IsConfigurationFailure"/> accepted.</param>
    internal static string Describe(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure is OptionsValidationException validation
            ? string.Join(ParagraphBreak, validation.Failures)
            : failure.Message;
    }

    private static string ParagraphBreak => Environment.NewLine + Environment.NewLine;
}
