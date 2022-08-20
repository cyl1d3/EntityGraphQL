using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using EntityGraphQL.Compiler;
using EntityGraphQL.Compiler.Util;
using EntityGraphQL.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace EntityGraphQL.Schema
{
    public class SubscriptionField : BaseField
    {
        public override GraphQLQueryFieldType FieldType { get; } = GraphQLQueryFieldType.Subscription;
        private readonly MethodInfo method;
        private readonly bool isAsync;

        public SubscriptionField(ISchemaProvider schema, string methodName, GqlTypeInfo returnType, MethodInfo method, string description, RequiredAuthorization requiredAuth, bool isAsync, Func<string, string> fieldNamer, bool autoAddInputTypes)
            : base(schema, methodName, description, returnType)
        {
            if (method.ReturnType.GetGenericTypeDefinition() != typeof(IObservable<>))
                throw new EntityGraphQLCompilerException($"Subscription {methodName} should return an IObservable<>");

            Services = new List<Type>();
            this.method = method;
            RequiredAuthorization = requiredAuth;
            this.isAsync = isAsync;

            ArgumentsType = method.GetParameters()
                .SingleOrDefault(p => p.GetCustomAttribute(typeof(MutationArgumentsAttribute)) != null || p.ParameterType.GetTypeInfo().GetCustomAttribute(typeof(MutationArgumentsAttribute)) != null)?.ParameterType;
            if (ArgumentsType != null)
            {
                foreach (var item in ArgumentsType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (GraphQLIgnoreAttribute.ShouldIgnoreMemberFromInput(item))
                        continue;
                    Arguments.Add(fieldNamer(item.Name), ArgType.FromProperty(schema, item, null));
                    AddInputTypesInArguments(schema, autoAddInputTypes, item.PropertyType);

                }
                foreach (var item in ArgumentsType.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (GraphQLIgnoreAttribute.ShouldIgnoreMemberFromInput(item))
                        continue;
                    Arguments.Add(fieldNamer(item.Name), ArgType.FromField(schema, item, null));
                    AddInputTypesInArguments(schema, autoAddInputTypes, item.FieldType);
                }
            }
            else
            {
                foreach (var item in method.GetParameters())
                {
                    if (GraphQLIgnoreAttribute.ShouldIgnoreMemberFromInput(item))
                        continue;

                    var inputType = item.ParameterType.GetEnumerableOrArrayType() ?? item.ParameterType;
                    if (item.ParameterType.IsPrimitive || (schema.HasType(inputType) && schema.Type(inputType).IsInput))
                    {
                        Arguments.Add(fieldNamer(item.Name!), ArgType.FromParameter(schema, item, null));
                        AddInputTypesInArguments(schema, autoAddInputTypes, item.ParameterType);
                    }
                }
            }
        }

        private static void AddInputTypesInArguments(ISchemaProvider schema, bool autoAddInputTypes, Type propType)
        {
            var inputType = propType.GetEnumerableOrArrayType() ?? propType;
            if (autoAddInputTypes && !schema.HasType(inputType))
                schema.AddInputType(inputType, inputType.Name, null).AddAllFields();
        }

        public void Deprecate(string reason)
        {
            IsDeprecated = true;
            DeprecationReason = reason;
        }

        public async Task<object?> CallAsync(object? context, IReadOnlyDictionary<string, object>? gqlRequestArgs, GraphQLValidator validator, IServiceProvider? serviceProvider, ParameterExpression? variableParameter, object? docVariables)
        {
            if (context == null)
                return null;

            // args in the mutation method
            var allArgs = new List<object>();
            object? argInstance = null;
            var validationErrors = new List<string>();

            // add parameters and any DI services
            foreach (var p in method.GetParameters())
            {
                if (p.GetCustomAttribute(typeof(MutationArgumentsAttribute)) != null || p.ParameterType.GetTypeInfo().GetCustomAttribute(typeof(MutationArgumentsAttribute)) != null)
                {
                    argInstance = ArgumentUtil.BuildArgumentsObject(Schema, Name, this, gqlRequestArgs ?? new Dictionary<string, object>(), Arguments.Values, ArgumentsType, variableParameter, docVariables, validationErrors);
                    allArgs.Add(argInstance!);
                }
                else if (docVariables != null && gqlRequestArgs != null && gqlRequestArgs.ContainsKey(p.Name!))
                {
                    var argField = Arguments[p.Name!];
                    var value = ArgumentUtil.BuildArgumentFromMember(Schema, gqlRequestArgs ?? new Dictionary<string, object>(), argField.Name, argField.RawType, argField.DefaultValue, validationErrors);
                    // this could be int to RequiredField<int>
                    if (value != null && value.GetType() != argField.RawType)
                    {
                        value = ExpressionUtil.ChangeType(value, argField.RawType, Schema);
                    }

                    if (value == null)
                    {
                        var field = docVariables.GetType().GetField(p.Name!);
                        if (field != null)
                        {
                            value = field.GetValue(docVariables);
                        }
                    }

                    argField.Validate(value, p.Name!, validationErrors);

                    allArgs.Add(value!);
                }
                else if (p.ParameterType == context.GetType())
                {
                    allArgs.Add(context);
                }
                // todo we should put this in the IServiceCollection actually...
                else if (p.ParameterType == typeof(GraphQLValidator))
                {
                    allArgs.Add(validator);
                }
                else if (serviceProvider != null)
                {
                    var service = serviceProvider.GetService(p.ParameterType);
                    if (service == null)
                    {
                        throw new EntityGraphQLExecutionException($"Service {p.ParameterType.Name} not found for dependency injection for mutation {method.Name}");
                    }
                    allArgs.Add(service);
                }
            }

            if (argumentValidators.Count > 0)
            {
                var validatorContext = new ArgumentValidatorContext(this, argInstance ?? allArgs, method);
                foreach (var argValidator in argumentValidators)
                {
                    argValidator(validatorContext);
                    argInstance = validatorContext.Arguments;
                }
                if (validatorContext.Errors != null && validatorContext.Errors.Count > 0)
                {
                    validationErrors.AddRange(validatorContext.Errors);
                }
            }

            if (validationErrors.Count > 0)
            {
                throw new EntityGraphQLValidationException(validationErrors);
            }

            object? instance = null;
            // we create an instance _per request_ injecting any parameters to the constructor
            // We kind of treat a mutation class like an asp.net controller
            // and we do not want to register them in the service provider to avoid the same issues controllers would have
            // with different lifetime objects
            if (instance == null)
            {
                instance = serviceProvider != null ?
                    ActivatorUtilities.CreateInstance(serviceProvider, method.DeclaringType!) :
                    Activator.CreateInstance(method.DeclaringType!);
            }

            object? result;
            if (isAsync)
            {
                result = await (dynamic?)method.Invoke(instance, allArgs.Any() ? allArgs.ToArray() : null);
            }
            else
            {
                try
                {
                    result = method.Invoke(instance, allArgs.ToArray());
                }
                catch (TargetInvocationException ex)
                {
                    if (ex.InnerException != null)
                        throw ex.InnerException;
                    throw;
                }
            }
            return result;
        }

        public override (Expression? expression, object? argumentValues) GetExpression(Expression fieldExpression, Expression? fieldContext, IGraphQLNode? parentNode, ParameterExpression? schemaContext, Dictionary<string, object> args, ParameterExpression? docParam, object? docVariables, IEnumerable<GraphQLDirective> directives, bool contextChanged, ParameterReplacer replacer)
        {
            var result = fieldExpression;

            if (schemaContext != null)
            {
                result = replacer.ReplaceByType(result, schemaContext.Type, schemaContext);
            }
            return (result, null);
        }
    }
}