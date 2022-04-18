﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    /// <summary>
    /// Implements a nested loop join
    /// </summary>
    class NestedLoopNode : BaseJoinNode
    {
        /// <summary>
        /// The condition that must be true for two records to join
        /// </summary>
        [Category("Join")]
        [Description("The condition that must be true for two records to join")]
        [DisplayName("Join Condition")]
        public BooleanExpression JoinCondition { get; set; }

        /// <summary>
        /// Values from the outer query that should be passed as references to the inner query
        /// </summary>
        [Category("Join")]
        [Description("Values from the outer query that should be passed as references to the inner query")]
        [DisplayName("Outer References")]
        public Dictionary<string,string> OuterReferences { get; set; }

        protected override IEnumerable<Entity> ExecuteInternal(IDictionary<string, DataSource> dataSources, IQueryExecutionOptions options, IDictionary<string, DataTypeReference> parameterTypes, IDictionary<string, object> parameterValues)
        {
            var leftSchema = LeftSource.GetSchema(dataSources, parameterTypes);
            var innerParameterTypes = GetInnerParameterTypes(leftSchema, parameterTypes);
            if (OuterReferences != null)
            {
                if (parameterTypes == null)
                    innerParameterTypes = new Dictionary<string, DataTypeReference>(StringComparer.OrdinalIgnoreCase);
                else
                    innerParameterTypes = new Dictionary<string, DataTypeReference>(parameterTypes, StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in OuterReferences)
                    innerParameterTypes[kvp.Value] = leftSchema.Schema[kvp.Key];
            }

            var rightSchema = RightSource.GetSchema(dataSources, innerParameterTypes);
            var mergedSchema = GetSchema(dataSources, parameterTypes, true);
            var joinCondition = JoinCondition?.Compile(mergedSchema, parameterTypes);

            foreach (var left in LeftSource.Execute(dataSources, options, parameterTypes, parameterValues))
            {
                var innerParameters = parameterValues;

                if (OuterReferences != null)
                {
                    if (parameterValues == null)
                        innerParameters = new Dictionary<string, object>();
                    else
                        innerParameters = new Dictionary<string, object>(parameterValues);

                    foreach (var kvp in OuterReferences)
                    {
                        left.Attributes.TryGetValue(kvp.Key, out var outerValue);
                        innerParameters[kvp.Value] = outerValue;
                    }
                }

                var hasRight = false;

                foreach (var right in RightSource.Execute(dataSources, options, innerParameterTypes, innerParameters))
                {
                    var merged = Merge(left, leftSchema, right, rightSchema);

                    if (joinCondition == null || joinCondition(merged, parameterValues, options))
                        yield return merged;

                    hasRight = true;

                    if (SemiJoin)
                        break;
                }

                if (!hasRight && JoinType == QualifiedJoinType.LeftOuter)
                    yield return Merge(left, leftSchema, null, rightSchema);
            }
        }

        private IDictionary<string, DataTypeReference> GetInnerParameterTypes(INodeSchema leftSchema, IDictionary<string, DataTypeReference> parameterTypes)
        {
            var innerParameterTypes = parameterTypes;

            if (OuterReferences != null)
            {
                if (parameterTypes == null)
                    innerParameterTypes = new Dictionary<string, DataTypeReference>(StringComparer.OrdinalIgnoreCase);
                else
                    innerParameterTypes = new Dictionary<string, DataTypeReference>(parameterTypes, StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in OuterReferences)
                    innerParameterTypes[kvp.Value] = leftSchema.Schema[kvp.Key];
            }

            return innerParameterTypes;
        }

        public override IDataExecutionPlanNodeInternal FoldQuery(IDictionary<string, DataSource> dataSources, IQueryExecutionOptions options, IDictionary<string, DataTypeReference> parameterTypes, IList<OptimizerHint> hints)
        {
            var leftSchema = LeftSource.GetSchema(dataSources, parameterTypes);
            LeftSource = LeftSource.FoldQuery(dataSources, options, parameterTypes, hints);
            LeftSource.Parent = this;

            var innerParameterTypes = GetInnerParameterTypes(leftSchema, parameterTypes);
            RightSource = RightSource.FoldQuery(dataSources, options, innerParameterTypes, hints);
            RightSource.Parent = this;

            if (RightSource is TableSpoolNode spool)
            {
                LeftSource.EstimateRowsOut(dataSources, options, parameterTypes);
                if (LeftSource.EstimatedRowsOut <= 1)
                {
                    RightSource = spool.Source;
                    RightSource.Parent = this;
                }
            }
            else if (JoinType == QualifiedJoinType.LeftOuter &&
                SemiJoin &&
                DefinedValues.Count == 1 &&
                RightSource is AssertNode assert &&
                assert.Source is StreamAggregateNode aggregate &&
                aggregate.Aggregates.Count == 2 &&
                aggregate.Aggregates[DefinedValues.Single().Value].AggregateType == AggregateType.First &&
                aggregate.Source is IndexSpoolNode indexSpool &&
                indexSpool.Source is FetchXmlScan fetch)
            {
                LeftSource.EstimateRowsOut(dataSources, options, parameterTypes);
                fetch.EstimateRowsOut(dataSources, options, innerParameterTypes);
                if (LeftSource.EstimatedRowsOut < 100 && fetch.EstimatedRowsOut > 5000)
                {
                    // Scalar subquery was folded to use an index spool due to an expected large number of outer records,
                    // but the estimate has now changed (e.g. due to a TopNode being folded). Remove the index spool and replace
                    // with filter
                    var filter = new FilterNode
                    {
                        Source = fetch,
                        Filter = new BooleanComparisonExpression
                        {
                            FirstExpression = indexSpool.KeyColumn.ToColumnReference(),
                            ComparisonType = BooleanComparisonType.Equals,
                            SecondExpression = new VariableReference { Name = indexSpool.SeekValue }
                        },
                        Parent = aggregate
                    };
                    aggregate.Source = filter.FoldQuery(dataSources, options, innerParameterTypes, hints);
                }
            }

            return this;
        }

        public override void AddRequiredColumns(IDictionary<string, DataSource> dataSources, IDictionary<string, DataTypeReference> parameterTypes, IList<string> requiredColumns)
        {
            if (JoinCondition != null)
            {
                foreach (var col in JoinCondition.GetColumns())
                {
                    if (!requiredColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                        requiredColumns.Add(col);
                }
            }

            var leftSchema = LeftSource.GetSchema(dataSources, parameterTypes);
            var leftColumns = requiredColumns
                .Where(col => leftSchema.ContainsColumn(col, out _))
                .Concat((IEnumerable<string>) OuterReferences?.Keys ?? Array.Empty<string>())
                .Distinct()
                .ToList();
            var innerParameterTypes = GetInnerParameterTypes(leftSchema, parameterTypes);
            var rightSchema = RightSource.GetSchema(dataSources, innerParameterTypes);
            var rightColumns = requiredColumns
                .Where(col => rightSchema.ContainsColumn(col, out _))
                .Concat(DefinedValues.Values)
                .Distinct()
                .ToList();

            LeftSource.AddRequiredColumns(dataSources, parameterTypes, leftColumns);
            RightSource.AddRequiredColumns(dataSources, parameterTypes, rightColumns);
        }

        protected override INodeSchema GetRightSchema(IDictionary<string, DataSource> dataSources, IDictionary<string, DataTypeReference> parameterTypes)
        {
            var leftSchema = LeftSource.GetSchema(dataSources, parameterTypes);
            var innerParameterTypes = GetInnerParameterTypes(leftSchema, parameterTypes);
            return RightSource.GetSchema(dataSources, innerParameterTypes);
        }

        public override void EstimateRowsOut(IDictionary<string, DataSource> dataSources, IQueryExecutionOptions options, IDictionary<string, DataTypeReference> parameterTypes)
        {
            LeftSource.EstimateRowsOut(dataSources, options, parameterTypes);

            var leftSchema = LeftSource.GetSchema(dataSources, parameterTypes);
            var innerParameterTypes = GetInnerParameterTypes(leftSchema, parameterTypes);

            RightSource.EstimateRowsOut(dataSources, options, innerParameterTypes);

            EstimatedRowsOut = EstimateRowsOutInternal(dataSources, options, parameterTypes);
        }

        protected override int EstimateRowsOutInternal(IDictionary<string, DataSource> dataSources, IQueryExecutionOptions options, IDictionary<string, DataTypeReference> parameterTypes)
        {
            var leftEstimate = LeftSource.EstimatedRowsOut;

            // We tend to use a nested loop with an assert node for scalar subqueries - we'll return one record for each record in the outer loop
            if (RightSource is AssertNode)
                return leftEstimate;

            var rightEstimate = RightSource.EstimatedRowsOut;

            if (OuterReferences != null && OuterReferences.Count > 0)
                return leftEstimate * rightEstimate;
            else if (JoinType == QualifiedJoinType.Inner)
                return Math.Min(leftEstimate, rightEstimate);
            else
                return Math.Max(leftEstimate, rightEstimate);
        }

        protected override IEnumerable<string> GetVariablesInternal()
        {
            return JoinCondition.GetVariables();
        }

        public override object Clone()
        {
            var clone = new NestedLoopNode
            {
                JoinCondition = JoinCondition,
                JoinType = JoinType,
                LeftSource = (IDataExecutionPlanNodeInternal)LeftSource.Clone(),
                OuterReferences = OuterReferences,
                RightSource = (IDataExecutionPlanNodeInternal)RightSource.Clone(),
                SemiJoin = SemiJoin
            };

            foreach (var kvp in DefinedValues)
                clone.DefinedValues.Add(kvp);

            clone.LeftSource.Parent = clone;
            clone.RightSource.Parent = clone;

            return clone;
        }
    }
}
