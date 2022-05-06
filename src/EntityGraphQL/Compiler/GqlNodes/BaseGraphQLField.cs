using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using EntityGraphQL.Compiler.Util;
using EntityGraphQL.Extensions;
using EntityGraphQL.Schema;

namespace EntityGraphQL.Compiler
{
    /// <summary>
    /// Once we parse the GQL document we get a graph of BaseGraphQLField objects where each one is ObjectProjectionField
    /// ListSelectionField, ScalarField or FragmentField. Example of each below in a GQL document
    /// {
    ///     singleEntity { # ObjectProjectionField
    ///         this # ScalarField
    ///         that # ScalarField
    ///     }
    ///     listOfThings { # ListSelectionField
    ///         ...someFrag # FragmentField
    ///     }
    /// }
    /// </summary>
    public abstract class BaseGraphQLField : IGraphQLNode
    {
        protected readonly ISchemaProvider schema;
        protected readonly List<GraphQLDirective> directives = new();
        /// <summary>
        /// Any values for a parameter that had a constant value in the query document.
        /// They are extracted out to parameters instead of inline ConstantExpression for future query caching possibilities
        /// </summary>
        protected Dictionary<ParameterExpression, object> constantParameters = new();
        internal Dictionary<ParameterExpression, object> ConstantParameters { get => constantParameters; }

        /// <summary>
        /// Name of the field
        /// </summary>
        /// <value></value>
        public string Name { get; }
        public IField? Field { get; }
        public List<BaseGraphQLField> QueryFields { get; } = new();
        public Expression? NextFieldContext { get; set; }
        public IGraphQLNode? ParentNode { get; set; }
        public ParameterExpression? RootParameter { get; set; }
        /// <summary>
        /// Arguments from inline in the query - not $ variables
        /// </summary>
        public IReadOnlyDictionary<string, object> Arguments { get; }
        public BaseGraphQLField(ISchemaProvider schema, IField? field, string name, Expression? nextFieldContext, ParameterExpression? rootParameter, IGraphQLNode? parentNode, Dictionary<string, object>? arguments)
        {
            Name = name;
            NextFieldContext = nextFieldContext;
            RootParameter = rootParameter;
            ParentNode = parentNode;
            this.Arguments = arguments ?? new Dictionary<string, object>();
            this.schema = schema;
            Field = field;
        }

        /// <summary>
        /// Field is a complex expression (using a method or function) that returns a single object (not IEnumerable)
        /// We wrap this is a function that does a null check and avoid duplicate calls on the method/service
        /// </summary>
        /// <value></value>
        public virtual bool HasAnyServices(IEnumerable<GraphQLFragmentStatement> fragments)
        {
            return Field?.Services.Any() == true || QueryFields.Any(f => f.HasAnyServices(fragments)) == true;
        }

        /// <summary>
        /// The dotnet Expression for this node. Could be as simple as (Person p) => p.Name
        /// Or as complex as (DbContext ctx) => ctx.People.Where(...).Select(p => new {...}).First()
        /// If there is a object selection (new {} in a Select() or not) we will build the NodeExpression on
        /// Execute() so we can look up any query fragment selections
        /// </summary>
        /// <param name="serviceProvider">Service provider to resolve services </param>
        /// <param name="fragments">Fragments in the query document</param>
        /// <param name="docParam">ParameterExpression for the variables defined in the request document</param>
        /// <param name="docVariables">Resolved values of the variables passed in the request document</param>
        /// <param name="schemaContext">ParameterExpression of the schema's Query context</param>
        /// <param name="withoutServiceFields">If true the expression builds without fields that require services</param>
        /// <param name="replacementNextFieldContext">A replacement context from a selection without service fields</param>
        /// <param name="isRoot">If this field is a Query root field</param>
        /// <param name="contextChanged">If true the context has changed. This means we are compiling/executing against the result ofa pre-selection without service fields</param>
        /// <param name="replacer">Replace used to make changes to expressions</param>
        /// <returns></returns>
        public abstract Expression? GetNodeExpression(IServiceProvider? serviceProvider, List<GraphQLFragmentStatement> fragments, ParameterExpression? docParam, object? docVariables, ParameterExpression schemaContext, bool withoutServiceFields, Expression? replacementNextFieldContext, bool isRoot, bool contextChanged, ParameterReplacer replacer);

        public abstract IEnumerable<BaseGraphQLField> Expand(List<GraphQLFragmentStatement> fragments, bool withoutServiceFields, Expression fieldContext, ParameterExpression? docParam, object? docVariables);

        public void AddField(BaseGraphQLField field)
        {
            QueryFields.Add(field);
            AddConstantParameters(field.ConstantParameters);
        }

        protected (Expression, ParameterExpression?) ProcessExtensionsPreSelection(Expression baseExpression, ParameterExpression? listTypeParam, ParameterReplacer parameterReplacer)
        {
            if (Field == null)
                return (baseExpression, listTypeParam);
            foreach (var extension in Field.Extensions)
            {
                (baseExpression, listTypeParam) = extension.ProcessExpressionPreSelection(baseExpression, listTypeParam, parameterReplacer);
            }
            return (baseExpression, listTypeParam);
        }

        protected (Expression baseExpression, Dictionary<string, CompiledField> selectionExpressions, ParameterExpression? selectContextParam) ProcessExtensionsSelection(Expression baseExpression, Dictionary<string, CompiledField> selectionExpressions, ParameterExpression? selectContextParam, bool servicesPass, ParameterReplacer parameterReplacer)
        {
            if (Field == null)
                return (baseExpression, selectionExpressions, selectContextParam);
            foreach (var extension in Field.Extensions)
            {
                (baseExpression, selectionExpressions, selectContextParam) = extension.ProcessExpressionSelection(baseExpression, selectionExpressions, selectContextParam, servicesPass, parameterReplacer);
            }
            return (baseExpression, selectionExpressions, selectContextParam);
        }
        protected Expression ProcessScalarExpression(Expression expression, ParameterReplacer parameterReplacer)
        {
            if (Field == null)
                return expression;
            foreach (var extension in Field.Extensions)
            {
                expression = extension.ProcessScalarExpression(expression, parameterReplacer);
            }
            return expression;
        }

        public void AddConstantParameters(IReadOnlyDictionary<ParameterExpression, object> constantParameters)
        {
            foreach (var item in constantParameters)
            {
                // replace them as new doc variables may be coming through
                this.constantParameters[item.Key] = item.Value;
            }
        }

        public void AddDirectives(IEnumerable<GraphQLDirective> graphQLDirectives)
        {
            directives.AddRange(graphQLDirectives);
        }
        protected BaseGraphQLField? ProcessFieldDirectives(BaseGraphQLField field, ParameterExpression? docParam, object? docVariables)
        {
            BaseGraphQLField? result = field;
            foreach (var directive in directives)
            {
                result = directive.ProcessField(schema, field, Arguments, docParam, docVariables);
            }
            return result;
        }
        protected Dictionary<string, object> ResolveArguments(IReadOnlyDictionary<string, object> arguments)
        {
            var result = new Dictionary<string, object>(arguments);
            if (Field == null)
                return result;

            if (Field.UseArgumentsFromField == null)
                return result;

            var node = ParentNode;
            while (node != null)
            {
                if (node.Field != null && node.Field == Field.UseArgumentsFromField)
                {
                    result = result.MergeNew(node.Arguments);
                    break;
                }
                node = node.ParentNode;
            }
            return result;
        }
    }
}