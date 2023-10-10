using System.Linq.Expressions;
using System.Reflection;
using Remora.Rest.Core;

// taken from https://github.com/VelvetToroyashi/RemoraPolyfillCachePOC
namespace Iodine.PolyfillHelper;

public static class CacheHelper<T>
{
    private static readonly Func<T, T, T> _polyfill = (Func<T, T, T>)PolyfillBuilder.Build<T>();
    public static T FastPolyFill(T old, T @new) => _polyfill(old, @new);
}

file static class PolyfillBuilder
{
    public static Delegate Build<T>()
    {
        var typeInfo = typeof(T);
        var delegateTypeInfo = typeof(Func<T, T, T>);
        
        var ctorInfo = typeInfo.GetConstructors()[0];
        var ctorParams = ctorInfo.GetParameters();
        var propertyInfo = typeInfo.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        if (ctorParams.Length != propertyInfo.Length)
        {
            throw new InvalidOperationException($"{typeInfo.FullName} cannot be poly-filled as there is a " +
                                                $"mismatch between properties and constructor arguments.");
        }

        var arguments = new Expression[ctorParams.Length];
        var oldArg = Expression.Parameter(typeInfo, "old");
        var newArg = Expression.Parameter(typeInfo, "new");
        
        for (var i = 0; i < propertyInfo.Length; i++)
        {
            var property = propertyInfo[i];
            var argumentType = propertyInfo[i].PropertyType;

            var left = Expression.Property(oldArg, property);
            var right = Expression.Property(newArg, property);

            // Check that the provider of the polyfill has a value for us. If it does, always update.
            Expression check =
                argumentType.IsGenericType && argumentType.GetGenericTypeDefinition() == typeof(Optional<>)
                    ? Expression.Property(right, argumentType.GetProperty(nameof(IOptional.HasValue))!)
                    : Expression.NotEqual(left, right);

            arguments[i] = Expression.Condition(check, right, left, argumentType);
        }

        var instance = Expression.New(ctorInfo, arguments);

        var lambda = Expression.Lambda(delegateTypeInfo, instance, oldArg, newArg);

        return lambda.Compile();
    }
}