namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// What becomes of each place that names an entity once the entity is removed — the apply's own reason where it
/// refuses, and the build's plain state where nothing does (docs/todo-admin.md §8d item 24).
/// </summary>
/// <remarks>
/// Each sentence is checked against the core file that owns it: a ref is refused by <c>DescriptorValidator</c>
/// ("Field references unknown entity"), a rollup's <c>from</c> by <c>RollupResolver.ChildEntity</c>; a hook's
/// <c>entity.update</c> is refused by <c>AfterHookCompiler.RefuseAction</c> whatever it names; the <c>automation</c>
/// and <c>functions</c> blocks are never evaluated or invoked, and are warned about as a whole
/// (<c>UnhonouredSubsystems.All</c>).
/// </remarks>
internal static class EntityRemovalWords
{
    /// <summary>A ref field that points at the entity.</summary>
    public const string Ref = "The apply refuses a ref to an entity the descriptor does not declare.";

    /// <summary>A rollup whose <c>from</c> is the entity.</summary>
    public const string Rollup = "The apply refuses a rollup from an entity the descriptor does not declare.";

    /// <summary>An <c>entity.update</c> action in a hook.</summary>
    public const string HookAction =
        "It is left pointing at nothing. The apply already refuses an entity.update action in a hook, whatever entity it "
        + "names, so the removal adds no refusal of its own — remove or retarget it.";

    /// <summary>An <c>entity.update</c> action in an automation rule (the controller's wording, Task 7 fix round 1).</summary>
    public const string AutomationAction =
        "It is left pointing at nothing; this build does not run automations, so nothing refuses it now — remove or "
        + "retarget it before automations ship.";

    /// <summary>An automation rule's trigger pattern.</summary>
    public const string AutomationTrigger =
        "Its trigger would never match; this build does not run automations, so nothing refuses it now — remove or "
        + "retarget it before automations ship.";

    /// <summary>A function's trigger pattern.</summary>
    public const string FunctionTrigger =
        "Its trigger would never match; this build does not invoke functions, so nothing refuses it now — remove or "
        + "retarget it before functions ship.";

    /// <summary>A place the schema does not put an action or a trigger in, which nothing in this build reads.</summary>
    public const string Elsewhere = "It is left pointing at nothing.";

    /// <summary>The sentence for an <c>entity.update</c> action found at <paramref name="path"/>.</summary>
    /// <param name="path">Its dotted path from the root.</param>
    public static string Action(string path) => Block(path) switch
    {
        "entities" => HookAction,
        "automation" => AutomationAction,
        _ => Elsewhere,
    };

    /// <summary>The sentence for a trigger pattern found at <paramref name="path"/>.</summary>
    /// <param name="path">Its dotted path from the root.</param>
    public static string Trigger(string path) => Block(path) switch
    {
        "automation" => AutomationTrigger,
        "functions" => FunctionTrigger,
        _ => Elsewhere,
    };

    /// <summary>The top-level block a dotted path starts in.</summary>
    private static string Block(string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? path : path[..dot];
    }
}
