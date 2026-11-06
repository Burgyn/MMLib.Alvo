using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>The relation arms of the type checker: a forward path, a reverse <c>exists</c>, and an outer read from a body.</summary>
internal static partial class CelTypeChecker
{
    private sealed partial class Visitor
    {
        private string RelationProfileMessage =>
            $"A relation is legal only in a rule or a lookup check; the {profile} profile reads this row alone.";

        private const string RelationProfileFix =
            "Store the related value in a field of this entity, or move the condition into a rule.";

        /// <summary>
        /// The <c>exists</c> body being checked: its variable names the child row, and a bare field reads
        /// <paramref name="Outer"/>.
        /// </summary>
        /// <param name="Variable">The macro's variable.</param>
        /// <param name="Receiver">The macro's receiver as written.</param>
        /// <param name="Outer">The entity the rule belongs to.</param>
        internal sealed record RelationFrame(string Variable, string Receiver, EntitySchema Outer);

        private (CelNode, CelValueType, bool, int) CheckPath(CelPath path)
        {
            if (RefusesRelation(path.Position))
            {
                return (path, CelValueType.Null, true, path.Position);
            }

            if (frame is not null)
            {
                return path.Receiver == frame.Variable
                    ? CheckFieldRef(new CelFieldRef(path.Member, CelValueType.Null, CelRecordState.Current))
                    : RefuseSecondHop(path, $"{path.Receiver}.{path.Member}", path.Position);
            }

            return RefTarget(path.Receiver) is { } related
                ? CheckHop(path, related)
                : RefuseFollow(path, path.Receiver, path.Member, path.Position);
        }

        private (CelNode, CelValueType, bool, int) CheckHop(CelPath path, EntitySchema related)
        {
            var target = Nested(related, bodyFrame: null);
            var (field, type, error, position) = target.CheckFieldRef(new CelFieldRef(path.Member, CelValueType.Null, CelRecordState.Current));
            Adopt(target);
            return (new CelHop(path.Receiver, related, (CelFieldRef)field), type, error, position);
        }

        private (CelNode, CelValueType, bool, int) CheckExists(CelExistsMacro macro)
        {
            if (RefusesRelation(macro.Position))
            {
                return Refused(macro, macro.Position);
            }

            if (frame is not null)
            {
                return RefuseSecondHop(macro, $"{macro.Receiver}.exists(…)", macro.Position);
            }

            return relations!.Reverse(entity.Name, macro.Receiver) is { } reverse
                ? CheckBody(macro, reverse.Child, reverse.Via)
                : RefuseFollow(macro, macro.Receiver, null, macro.Position);
        }

        private (CelNode, CelValueType, bool, int) CheckBody(CelExistsMacro macro, EntitySchema child, string via)
        {
            var body = Nested(child, new RelationFrame(macro.Variable, macro.Receiver, entity));
            var (checkedBody, bodyType, bodyError, bodyPosition) = body.CheckNode(macro.Body);
            var notBool = body.RequireBool(bodyType, bodyError, "An exists body", bodyPosition);
            Adopt(body);
            var relation = new CelRelation(
                CelRelationKind.Reverse, child, via, AlvoManagedColumns.Id, macro.Receiver, checkedBody);
            return (relation, CelValueType.Bool, notBool, macro.Position);
        }

        private (CelNode, CelValueType, bool, int) CheckOuterFieldRef(CelFieldRef fieldRef)
        {
            var outer = Nested(frame!.Outer, bodyFrame: null);
            var (field, type, error, position) = outer.CheckFieldRef(fieldRef);
            Adopt(outer);
            return (new CelOuterFieldRef(((CelFieldRef)field).FieldName, type), type, error, position);
        }

        private (CelNode, CelValueType, bool, int) CheckOuterHas(CelHas has)
        {
            var outer = Nested(frame!.Outer, bodyFrame: null);
            var (presence, type, error, position) = outer.CheckHas(has);
            Adopt(outer);
            var field = ((CelHas)presence).Field;
            return (new CelOuterHas(new CelOuterFieldRef(field.FieldName, field.Type)), type, error, position);
        }

        /// <summary>Refuses relation syntax outside a rule, or in a compile given no catalog to resolve it.</summary>
        private bool RefusesRelation(int position)
        {
            if (relations is not null)
            {
                return CheckConstruct(CelConstructKind.Relation, RelationProfileMessage, RelationProfileFix, position);
            }

            Errors.Add(new CelCompilationError(RelationProfileMessage, RelationProfileFix, position));
            return true;
        }

        /// <summary>The readable entity a ref field of this entity points at, or <see langword="null"/>.</summary>
        private EntitySchema? RefTarget(string fieldName) =>
            ResolveField(fieldName) is { Type: FieldType.Ref, Reference: { } reference }
                ? relations!.Entity(reference.TargetEntity)
                : null;

        /// <summary>The related or outer field a hop or an outer reference reads, for the enum-literal check.</summary>
        private FieldSchema? RelatedFieldOf(CelNode node) => node switch
        {
            CelHop hop => FieldOf(hop.Related, hop.Field.FieldName),
            CelOuterFieldRef outer when frame is not null => FieldOf(frame.Outer, outer.FieldName),
            _ => null,
        };

        private static FieldSchema? FieldOf(EntitySchema schema, string fieldName) =>
            schema.Fields.FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));

        private (CelNode, CelValueType, bool, int) RefuseFollow(CelNode node, string member, string? field, int position)
        {
            Errors.Add(relations!.Refusal(entity.Name, member, field) with { Position = position });
            return Refused(node, position);
        }

        private (CelNode, CelValueType, bool, int) RefuseSecondHop(CelNode node, string written, int position)
        {
            Errors.Add(new CelCompilationError(
                $"'{written}' reads a second relation inside '{frame!.Receiver}.exists(…)'; a rule term follows one relation.",
                $"Read the fields of '{frame.Variable}' only, and let '{entity.Name}''s own list rule follow the next relation.",
                position));
            return Refused(node, position);
        }

        /// <summary>A refused relation term; an <c>exists</c> is still a predicate, so the refusal is not doubled by a result-type one.</summary>
        private static (CelNode, CelValueType, bool, int) Refused(CelNode node, int position) =>
            (node, node is CelExistsMacro ? CelValueType.Bool : CelValueType.Null, true, position);

        private Visitor Nested(EntitySchema schema, RelationFrame? bodyFrame) =>
            new(source, schema, profile, catalog, relations, bodyFrame, Errors) { _cursor = _cursor };

        private void Adopt(Visitor nested) => _cursor = Math.Max(_cursor, nested._cursor);
    }
}
