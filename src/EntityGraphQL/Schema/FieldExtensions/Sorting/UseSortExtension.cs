using System;
using System.Linq.Expressions;
using EntityGraphQL.Extensions;

namespace EntityGraphQL.Schema.FieldExtensions
{
    public static class UseSortExtension
    {
        /// <summary>
        /// Update field to implement a sort argument that takes options to sort a collection
        /// Only call on a field that returns an IEnumerable
        /// </summary>
        /// <param name="field"></param>
        /// <param name="fieldSelection">Select the fields you want available for sorting.
        /// T must be the context of the collection you are applying the sort to</param>
        /// <returns></returns>
        public static Field UseSort<ElementType, ReturnType>(this Field field, Expression<Func<ElementType, ReturnType>> fieldSelection)
        {
            if (!field.Resolve.Type.IsEnumerableOrArray())
                throw new ArgumentException($"UseSort must only be called on a field that returns an IEnumerable");
            field.AddExtension(new SortExtension(fieldSelection.ReturnType));
            return field;
        }

        /// <summary>
        /// Update field to implement a sort argument that takes options to sort a collection
        /// Only call on a field that returns an IEnumerable
        /// </summary>
        /// <param name="field"></param>
        /// <returns></returns>
        public static Field UseSort(this Field field)
        {
            if (!field.Resolve.Type.IsEnumerableOrArray())
                throw new ArgumentException($"UseSort must only be called on a field that returns an IEnumerable");
            field.AddExtension(new SortExtension(null));
            return field;
        }
    }

    public class UseSortAttribute : FieldExtensionAttribute
    {
        public UseSortAttribute() { }

        public UseSortAttribute(Func<Expression> fields)
        {

        }

        public override void ApplyExtension(Field field)
        {
            field.UseSort();
        }
    }
}