using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Data.EntityFrameworkCore;
using System.Data.Common;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// Binds a rendered <c>SqlPredicate</c>'s parameter bag for a test that runs the predicate as a whole
/// <c>WHERE</c> clause of its own.
/// </summary>
/// <remarks>
/// It goes through <c>PredicateParameterBinder.Bind</c> — the binder's one entry point — by declaring each
/// value's origin, rather than through a member of the binder's own that only tests called. The binder used
/// to expose exactly such a member (<c>BindPolicyPredicate</c>) and it had no production call site, which is
/// the defect that class's own remarks record having already happened once. Keeping the convenience here, in
/// test code, is what stops it looking like a production seam.
/// </remarks>
internal static class PolicyPredicateParameters
{
    internal static DbParameter[] Bind(
        AlvoDataContext context, IEntityType rows, IReadOnlyDictionary<string, object?> parameters) =>
        new PredicateParameterBinder(context).Bind(
            rows,
            parameters.ToDictionary(
                pair => pair.Key, pair => BoundValue.FromPolicyPredicate(pair.Value), StringComparer.Ordinal));
}
