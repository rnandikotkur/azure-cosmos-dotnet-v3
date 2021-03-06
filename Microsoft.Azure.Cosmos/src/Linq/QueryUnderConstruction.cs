//-----------------------------------------------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//-----------------------------------------------------------------------------------------------------------------------------------------

#define SUPPORT_SUBQUERIES

namespace Microsoft.Azure.Cosmos.Linq
{
    using System;
    using System.Text;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Collections.Generic;
    using Microsoft.Azure.Cosmos.Sql;
    using static FromParameterBindings;
    using static Microsoft.Azure.Cosmos.Linq.ExpressionToSql;

    /// <summary>
    /// Query that is being constructed.
    /// </summary>
    internal sealed class QueryUnderConstruction
    {
        // The SQLQuery class does not maintain enough information for optimizations.
        // so this class is a replacement which in the end should produce a SQLQuery.

        /// <summary>
        /// Binding for the FROM parameters.
        /// </summary>
        public FromParameterBindings fromParameters
        {
            get;
            set;
        }

        /// <summary>
        /// The parameter expression to be used as this query's alias.
        /// </summary>
        public ParameterExpression Alias
        {
            get
            {
                return this.alias.Value;
            }
        }

        private readonly Func<string, ParameterExpression> aliasCreatorFunc;

        public const string DefaultSubqueryRoot = "r";

        private SqlSelectClause selectClause;
        private SqlWhereClause whereClause;
        private SqlOrderbyClause orderByClause;
        private SqlTopSpec topSpec;
        private Lazy<ParameterExpression> alias;

        /// <summary>
        /// Input subquery.
        /// </summary>
        private QueryUnderConstruction inputQuery;

        public QueryUnderConstruction(Func<string, ParameterExpression> aliasCreatorFunc)
            : this(aliasCreatorFunc, inputQuery: null) { }

        public QueryUnderConstruction(Func<string, ParameterExpression> aliasCreatorFunc, QueryUnderConstruction inputQuery)
        {
            this.fromParameters = new FromParameterBindings();
            this.aliasCreatorFunc = aliasCreatorFunc;
            this.inputQuery = inputQuery;
            this.alias = new Lazy<ParameterExpression>(() => aliasCreatorFunc(QueryUnderConstruction.DefaultSubqueryRoot));
        }

        public void Bind(ParameterExpression parameter, SqlCollection collection)
        {
            this.AddBinding(new FromParameterBindings.Binding(parameter, collection, isInCollection: true));
        }

        public void AddBinding(Binding binding)
        {
            this.fromParameters.Add(binding);
        }

        public ParameterExpression GetInputParameterInContext(bool isInNewQuery)
        {
            return isInNewQuery ? this.Alias : this.fromParameters.GetInputParameter();
        }
   
        /// <summary>
        /// Create a FROM clause from a set of FROM parameter bindings.
        /// </summary>
        /// <returns>The created FROM clause.</returns>
        private SqlFromClause CreateFrom(SqlCollectionExpression inputCollectionExpression)
        {
            bool first = true;
            foreach (Binding paramDef in this.fromParameters.GetBindings())
            {
                // If input collection expression is provided, the first binding,
                // which is the input paramter name, should be omitted.
                if (first)
                {
                    first = false;
                    if (inputCollectionExpression != null) continue;
                }

                ParameterExpression parameter = paramDef.Parameter;
                SqlCollection paramBinding = paramDef.ParameterDefinition;

                SqlIdentifier identifier = SqlIdentifier.Create(parameter.Name);
                SqlCollectionExpression collExpr;
                if (!paramDef.IsInCollection)
                {
                    SqlCollection collection = paramBinding ?? SqlInputPathCollection.Create(identifier, null);
                    SqlIdentifier alias = paramBinding == null ? null : identifier;
                    collExpr = SqlAliasedCollectionExpression.Create(collection, alias);
                }
                else
                {
                    collExpr = SqlArrayIteratorCollectionExpression.Create(identifier, paramBinding);
                }

                if (inputCollectionExpression != null)
                {
                    inputCollectionExpression = SqlJoinCollectionExpression.Create(inputCollectionExpression, collExpr);
                }
                else
                {
                    inputCollectionExpression = collExpr;
                }
            }

            SqlFromClause fromClause = SqlFromClause.Create(inputCollectionExpression);
            return fromClause;
        }

        private SqlFromClause CreateSubqueryFromClause()
        {
            SqlQuery subquery = this.inputQuery.GetSqlQuery();
            var collection = SqlSubqueryCollection.Create(subquery);
            var inputParam = this.inputQuery.Alias;
            SqlIdentifier identifier = SqlIdentifier.Create(inputParam.Name);
            var colExp = SqlAliasedCollectionExpression.Create(collection, identifier);
            var fromClause = this.CreateFrom(colExp);
            return fromClause;
        }

        /// <summary>
        /// Convert the entire query to a SQL Query.
        /// </summary>
        /// <returns>The corresponding SQL Query.</returns>
        public SqlQuery GetSqlQuery()
        {
            SqlFromClause fromClause;
            if (this.inputQuery != null)
            {
#if SUPPORT_SUBQUERIES
                
                fromClause = this.CreateSubqueryFromClause();
#else
                throw new DocumentQueryException("SQL subqueries currently not supported");
#endif
            }
            else
            {
                fromClause = this.CreateFrom(inputCollectionExpression: null);
            }

            // Create a SqlSelectClause with the topSpec.
            // If the query is flatten then selectClause may have a topSpec. It should be taken in that case.
            // If the query doesn't have a selectClause, use SELECT v0 where v0 is the input param.
            SqlSelectClause selectClause = this.selectClause;
            if (selectClause == null)
            {
                string parameterName = this.fromParameters.GetInputParameter().Name;
                SqlScalarExpression parameterExpression = SqlPropertyRefScalarExpression.Create(null, SqlIdentifier.Create(parameterName));
                selectClause = this.selectClause = SqlSelectClause.Create(SqlSelectValueSpec.Create(parameterExpression));
            }
            selectClause = SqlSelectClause.Create(selectClause.SelectSpec, selectClause.TopSpec ?? this.topSpec, selectClause.HasDistinct);
            SqlQuery result = SqlQuery.Create(selectClause, fromClause, this.whereClause, this.orderByClause, offsetLimitClause: null);
            return result;
        }

        private QueryUnderConstruction PackageQuery(HashSet<ParameterExpression> inScope, Collection currentCollection)
        {
            QueryUnderConstruction result = new QueryUnderConstruction(this.aliasCreatorFunc);
            result.fromParameters.SetInputParameter(typeof(object), this.Alias.Name, inScope);
            result.inputQuery = this;
            return result;
        }

        /// <summary>
        /// Find and flatten the prefix set of queries into a single query by substituting their expressions.
        /// </summary>
        /// <returns>The query that has been flatten</returns>
        public QueryUnderConstruction FlattenAsPossible()
        {
            // Flatten should be done when the current query can be translated without the need of using sub query
            // The cases that need to use sub query are: 
            //     1. Select clause appears after Distinct
            //     2. There are any operations after Take
            //     3. There are nested Select, Where or OrderBy
            QueryUnderConstruction parentQuery = null;
            QueryUnderConstruction flattenQuery = null;
            bool seenSelect = false;
            bool seenAnyOp = false;
            for (QueryUnderConstruction query = this; query != null; query = query.inputQuery)
            {
                foreach (Binding binding in query.fromParameters.GetBindings())
                {
                    if ((binding.ParameterDefinition != null) && (binding.ParameterDefinition is SqlSubqueryCollection))
                    {
                        flattenQuery = this;
                        break;
                    }
                }

                if (flattenQuery != null) break;

                if ((query.topSpec != null && seenAnyOp) ||
                    (query.selectClause != null && query.selectClause.HasDistinct && seenSelect))
                {
                    parentQuery.inputQuery = query.FlattenAsPossible();
                    flattenQuery = this;
                    break;
                }

                seenSelect = seenSelect || ((query.selectClause != null) && !(query.selectClause.HasDistinct));
                seenAnyOp = true;
                parentQuery = query;
            }

            if (flattenQuery == null) flattenQuery = this.Flatten();

            return flattenQuery;
        }

        /// <summary>
        /// Flatten subqueries into a single query by substituting their expressions in the current query.
        /// </summary>
        /// <returns>A flattened query.</returns>
        private QueryUnderConstruction Flatten()
        {
            // SELECT fo(y) FROM y IN (SELECT fi(x) FROM x WHERE gi(x)) WHERE go(y)
            // is translated by substituting fi(x) for y in the outer query
            // producing
            // SELECT fo(fi(x)) FROM x WHERE gi(x) AND (go(fi(x))
            if (this.inputQuery == null)
            {
                // we are flat already
                if (this.selectClause == null)
                {
                    // If selectClause doesn't exists, use SELECT v0 where v0 is the input parameter, instead of SELECT *.
                    string parameterName = this.fromParameters.GetInputParameter().Name;
                    SqlScalarExpression parameterExpression = SqlPropertyRefScalarExpression.Create(null, SqlIdentifier.Create(parameterName));
                    this.selectClause = SqlSelectClause.Create(SqlSelectValueSpec.Create(parameterExpression));
                }
                else
                {
                    this.selectClause = SqlSelectClause.Create(this.selectClause.SelectSpec, this.topSpec, this.selectClause.HasDistinct);
                }

                return this;
            }

            var flatInput = this.inputQuery.Flatten();
            var inputSelect = flatInput.selectClause;
            var inputwhere = flatInput.whereClause;

            // Determine the paramName to be replaced in the current query
            // It should be the top input parameter name which is not binded in this collection.
            // That is because if it has been binded before, it has global scope and should not be replaced.
            string paramName = null;
            HashSet<string> inputQueryParams = new HashSet<string>();
            foreach (Binding binding in this.inputQuery.fromParameters.GetBindings())
            {
                inputQueryParams.Add(binding.Parameter.Name);
            }

            foreach (Binding binding in this.fromParameters.GetBindings())
            {
                if (binding.ParameterDefinition == null || inputQueryParams.Contains(binding.Parameter.Name))
                {
                    paramName = binding.Parameter.Name;
                }
            }

            SqlIdentifier replacement = SqlIdentifier.Create(paramName);
            var composedSelect = Substitute(inputSelect, inputSelect.TopSpec ?? this.topSpec, replacement, this.selectClause);
            var composedWhere = Substitute(inputSelect.SelectSpec, replacement, this.whereClause);
            var composedOrderBy = Substitute(inputSelect.SelectSpec, replacement, this.orderByClause);
            var and = QueryUnderConstruction.CombineWithConjunction(inputwhere, composedWhere);
            var fromParams = QueryUnderConstruction.CombineInputParameters(flatInput.fromParameters, this.fromParameters);
            QueryUnderConstruction result = new QueryUnderConstruction(this.aliasCreatorFunc)
            {
                selectClause = composedSelect,
                whereClause = and,
                inputQuery = null,
                fromParameters = flatInput.fromParameters,
                orderByClause = composedOrderBy ?? this.inputQuery.orderByClause,
                alias = new Lazy<ParameterExpression>(() => this.Alias)
            };
            return result;
        }

        private SqlSelectClause Substitute(SqlSelectClause inputSelectClause, SqlTopSpec topSpec, SqlIdentifier inputParam, SqlSelectClause selectClause)
        {
            var selectSpec = inputSelectClause.SelectSpec;

            if (selectClause == null)
            {
                return selectSpec != null ? SqlSelectClause.Create(selectSpec, topSpec, inputSelectClause.HasDistinct) : null;
            }

            if (selectSpec is SqlSelectStarSpec)
            {
                return SqlSelectClause.Create(selectSpec, topSpec, inputSelectClause.HasDistinct);
            }

            var selValue = selectSpec as SqlSelectValueSpec;
            if (selValue != null)
            {
                var intoSpec = selectClause.SelectSpec;
                if (intoSpec is SqlSelectStarSpec)
                {
                    return SqlSelectClause.Create(selectSpec, topSpec, selectClause.HasDistinct || inputSelectClause.HasDistinct);
                }

                var intoSelValue = intoSpec as SqlSelectValueSpec;
                if (intoSelValue != null)
                {
                    var replacement = SqlExpressionManipulation.Substitute(selValue.Expression, inputParam, intoSelValue.Expression);
                    SqlSelectValueSpec selValueReplacement = SqlSelectValueSpec.Create(replacement);
                    return SqlSelectClause.Create(selValueReplacement, this.topSpec, selectClause.HasDistinct || inputSelectClause.HasDistinct);
                }

                throw new DocumentQueryException("Unexpected SQL select clause type: " + intoSpec.Kind);
            }

            throw new DocumentQueryException("Unexpected SQL select clause type: " + selectSpec.Kind);
        }

        private SqlWhereClause Substitute(SqlSelectSpec spec, SqlIdentifier inputParam, SqlWhereClause whereClause)
        {
            if (whereClause == null)
            {
                return null;
            }

            if (spec is SqlSelectStarSpec)
            {
                return whereClause;
            }
            else
            {
                var selValue = spec as SqlSelectValueSpec;
                if (selValue != null)
                {
                    SqlScalarExpression replaced = selValue.Expression;
                    SqlScalarExpression original = whereClause.FilterExpression;
                    SqlScalarExpression substituted = SqlExpressionManipulation.Substitute(replaced, inputParam, original);
                    SqlWhereClause result = SqlWhereClause.Create(substituted);
                    return result;
                }
            }
         
            throw new DocumentQueryException("Unexpected SQL select clause type: " + spec.Kind);
        }

        private SqlOrderbyClause Substitute(SqlSelectSpec spec, SqlIdentifier inputParam, SqlOrderbyClause orderByClause)
        {
            if (orderByClause == null)
            {
                return null;
            }

            if (spec is SqlSelectStarSpec)
            {
                return orderByClause;
            }

            var selValue = spec as SqlSelectValueSpec;
            if (selValue != null)
            {
                SqlScalarExpression replaced = selValue.Expression;
                var substitutedItems = new SqlOrderByItem[orderByClause.OrderbyItems.Count];
                for (int i = 0; i < substitutedItems.Length; ++i)
                {
                    var substituted = SqlExpressionManipulation.Substitute(replaced, inputParam, orderByClause.OrderbyItems[i].Expression);
                    substitutedItems[i] = SqlOrderByItem.Create(substituted, orderByClause.OrderbyItems[i].IsDescending);
                }
                var result = SqlOrderbyClause.Create(substitutedItems);
                return result;
            }

            throw new DocumentQueryException("Unexpected SQL select clause type: " + spec.Kind);
        }

        /// <summary>
        /// Determine if the current method call should create a new QueryUnderConstruction node or not.
        /// </summary>
        /// <param name="methodName">The current method name</param>
        /// <param name="argumentCount">The method's parameter count</param>
        /// <returns>True if the current method should be in a new query node</returns>
        public bool ShouldBeOnNewQuery(string methodName, int argumentCount)
        {
            bool shouldPackage = false;

            switch (methodName)
            {
                case LinqMethods.Select:
                case LinqMethods.Min:
                case LinqMethods.Max:
                case LinqMethods.Sum:
                case LinqMethods.Average:
                    // New query is needed when adding a Select to an existing Select
                    // Aggregation except Count adds a new Select so they are treated the same way.
                    shouldPackage = this.selectClause != null;
                    break;

                case LinqMethods.Count:
                    // When Count has 2 arguments, it calls into AddWhereClause consider it as a Where in that case.
                    // Otherwise, treat it as Select.
                    shouldPackage = (argumentCount == 2 && this.ShouldBeOnNewQuery(LinqMethods.Where, 2)) || 
                        this.ShouldBeOnNewQuery(LinqMethods.Select, 1);
                    break;

                case LinqMethods.Where:
                    // Where expression parameter needs to be substitued if necessary so
                    // It is not needed in Select distinct because the Select distinct would have the necessary parameter name adjustment.
                case LinqMethods.Any:
                case LinqMethods.OrderBy:
                case LinqMethods.OrderByDescending:
                case LinqMethods.Distinct:
                    // New query is needed when there is already a Take or a non-distinct Select
                    shouldPackage = this.topSpec != null || (this.selectClause != null && !this.selectClause.HasDistinct);
                    break;

                default:
                    break;
            }

            return shouldPackage;
        }

        /// <summary>
        /// Add a Select clause to a query; may need to create a new subquery.
        /// </summary>
        /// <param name="select">Select clause to add.</param>
        /// <param name="context">The translation context.</param>
        /// <returns>A new query containing a select clause.</returns>
        public QueryUnderConstruction AddSelectClause(SqlSelectClause select, TranslationContext context)
        {
            return AddSelectClause(select, context.InScope, context.PeekCollection(), context.CurrentSubqueryBinding);
        }

        /// <summary>
        /// Add a Select clause to a query; may need to create a new subquery.
        /// </summary>
        /// <param name="select">Select clause to add.</param>
        /// <param name="inScope">Set of parameter names in scope.</param>
        /// <param name="currentCollection">Current input collection.</param>
        /// <param name="subqueryBinding">The subquery binding information.</param>
        /// <returns>A new query containing a select clause.</returns>
        public QueryUnderConstruction AddSelectClause(SqlSelectClause select, HashSet<ParameterExpression> inScope, Collection currentCollection, TranslationContext.SubqueryBinding subqueryBinding)
        {
            QueryUnderConstruction result = this;
            if (subqueryBinding.ShouldBeOnNewQuery)
            {
                result = this.PackageQuery(inScope, currentCollection);
                subqueryBinding.ShouldBeOnNewQuery = false;
            }

            // If result SelectClause is not null, or both result selectClause and select has Distinct
            // then it is unexpected since the SelectClause will be overwritten.
            if (!((result.selectClause != null && result.selectClause.HasDistinct && selectClause.HasDistinct) ||
                result.selectClause == null))
            {
                throw new DocumentQueryException("Internal error: attempting to overwrite SELECT clause");
            }

            result.selectClause = select;
            foreach (Binding binding in subqueryBinding.TakeBindings()) result.AddBinding(binding);

            return result;
        }

        public QueryUnderConstruction AddOrderByClause(SqlOrderbyClause orderBy, TranslationContext context)
        {
            QueryUnderConstruction result = this;
            if (context.CurrentSubqueryBinding.ShouldBeOnNewQuery)
            {
                result = this.PackageQuery(context.InScope, context.PeekCollection());
                context.CurrentSubqueryBinding.ShouldBeOnNewQuery = false;
            }

            result.orderByClause = orderBy;
            foreach (Binding binding in context.CurrentSubqueryBinding.TakeBindings()) result.AddBinding(binding);

            return result;
        }

        public QueryUnderConstruction AddTopSpec(SqlTopSpec topSpec, HashSet<ParameterExpression> inScope, Collection currentCollection)
        {
            QueryUnderConstruction result = this;

            if (result.topSpec != null)
            {
                // Set the topSpec to the one with minimum Count value
                result.topSpec = (this.topSpec.Count < topSpec.Count) ? this.topSpec : topSpec;
            }
            else
            {
                result.topSpec = topSpec;
            }
            if (result.topSpec.Count < 0)
            {
                result.topSpec = SqlTopSpec.Create(0);
            }

            return result;
        }

        private static SqlWhereClause CombineWithConjunction(SqlWhereClause first, SqlWhereClause second)
        {
            if (first == null)
            {
                return second;
            }

            if (second == null)
            {
                return first;
            }

            var previousFilter = first.FilterExpression;
            var currentFilter = second.FilterExpression;
            var and = SqlBinaryScalarExpression.Create(SqlBinaryScalarOperatorKind.And, previousFilter, currentFilter);
            var result = SqlWhereClause.Create(and);
            return result;
        }

        private static object CombineInputParameters(FromParameterBindings inputQueryParams, FromParameterBindings currentQueryParams)
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (Binding binding in inputQueryParams.GetBindings())
            {
                seen.Add(binding.Parameter.Name);
            }

            var fromParams = inputQueryParams;
            foreach (FromParameterBindings.Binding binding in currentQueryParams.GetBindings())
            {
                if (binding.ParameterDefinition != null && !seen.Contains(binding.Parameter.Name)) {
                    fromParams.Add(binding);
                    seen.Add(binding.Parameter.Name);
                }
            }

            return fromParams;
        }

        /// <summary>
        /// Add a Where clause to a query; may need to create a new query.
        /// </summary>
        /// <param name="whereClause">Clause to add.</param>
        /// <param name="context">The translation context.</param>
        /// <returns>A new query containing the specified Where clause.</returns>
        public QueryUnderConstruction AddWhereClause(SqlWhereClause whereClause, TranslationContext context)
        {
            QueryUnderConstruction result = this;
            if (this.ShouldBeOnNewQuery(LinqMethods.Where, 2))
            {
                result = this.PackageQuery(context.InScope, context.PeekCollection());
                context.CurrentSubqueryBinding.ShouldBeOnNewQuery = false;
            }

            whereClause = QueryUnderConstruction.CombineWithConjunction(result.whereClause, whereClause);
            result.whereClause = whereClause;
            foreach (Binding binding in context.CurrentSubqueryBinding.TakeBindings()) result.AddBinding(binding);

            return result;
        }

        /// <summary>
        /// Separate out the query branch, which makes up a subquery and is built on top of the parent query chain.
        /// E.g. Let the query chain at some point in time be q0 - q1 - q2. When a subquery is recognized, its expression is visited.
        /// Assume that adds 2 queries to the chain to q0 - q1 - q2 - q3 - q4. Invoking q4.GetSubquery(q2) would return q3 - q4
        /// after it's isolated from the rest of the chain.
        /// </summary>
        /// <param name="queryBeforeVisit">The last query in the chain before the collection expression is visited</param>
        /// <returns>The subquery that has been decoupled from the parent query chain</returns>
        public QueryUnderConstruction GetSubquery(QueryUnderConstruction queryBeforeVisit)
        {
            QueryUnderConstruction parentQuery = null;
            for (QueryUnderConstruction query = this;
                query != queryBeforeVisit;
                query = query.inputQuery)
            {
                parentQuery = query;
            }

            parentQuery.inputQuery = null;
            return this;
        }

        /// <summary>
        /// Debugging string.
        /// </summary>
        /// <returns>Query representation as a string (not legal SQL).</returns>
        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            if (this.inputQuery != null)
            {
                builder.Append(this.inputQuery);
            }
            
            if (this.whereClause != null)
            {
                builder.Append("->");
                builder.Append(this.whereClause);
            }

            if (this.selectClause != null)
            {
                builder.Append("->");
                builder.Append(this.selectClause);
            }

            return builder.ToString();
        }
    }
}
