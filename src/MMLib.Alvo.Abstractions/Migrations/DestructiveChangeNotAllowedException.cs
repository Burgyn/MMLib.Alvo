namespace MMLib.Alvo.Migrations;

/// <summary>
/// Thrown when a runtime apply/rollback would destroy data but <see cref="MigrationOptions.AllowDestructive"/>
/// is <see langword="false"/>.
/// </summary>
public sealed class DestructiveChangeNotAllowedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DestructiveChangeNotAllowedException"/> class.</summary>
    public DestructiveChangeNotAllowedException()
    {
        Project = "";
        Plan = new MigrationPlan { Steps = [] };
    }

    /// <summary>Initializes a new instance of the <see cref="DestructiveChangeNotAllowedException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public DestructiveChangeNotAllowedException(string message)
        : base(message)
    {
        Project = "";
        Plan = new MigrationPlan { Steps = [] };
    }

    /// <summary>Initializes a new instance of the <see cref="DestructiveChangeNotAllowedException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public DestructiveChangeNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Project = "";
        Plan = new MigrationPlan { Steps = [] };
    }

    /// <summary>Initializes a new instance of the <see cref="DestructiveChangeNotAllowedException"/> class from a refused plan.</summary>
    /// <param name="project">The project whose change was refused.</param>
    /// <param name="plan">The refused plan (inspect its steps for the destructive changes).</param>
    public DestructiveChangeNotAllowedException(string project, MigrationPlan plan)
        : base($"The change to project '{project}' is destructive and was refused. Re-issue with AllowDestructive=true after reviewing the dry-run.")
    {
        Project = project;
        Plan = plan;
    }

    /// <summary>Gets the project whose change was refused.</summary>
    public string Project { get; }

    /// <summary>Gets the refused plan (inspect its steps for the destructive changes).</summary>
    public MigrationPlan Plan { get; }
}
