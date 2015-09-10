﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Wivuu.DataSeed
{
    public static class Mapping
    {
        private static Dictionary<Type, object> _selfMappers
            = new Dictionary<Type, object>();

        private static Dictionary<Type, object> _dynamicMappers
            = new Dictionary<Type, object>();

        /// <summary>
        /// Map the source to the destination
        /// </summary>
        /// <returns>The destination</returns>
        public static T Map<T>(T destination, T source)
            where T : class, new()
        {
            var type = typeof(T);
            if (destination == null)
                destination = new T();

            object mappingBox;
            Action<T, T> mapping;
            if (!_selfMappers.TryGetValue(type, out mappingBox))
            {
                // Create Mapping logic
                mapping = CreateMap(source);
                _selfMappers[type] = mapping;
            }
            else
                mapping = mappingBox as Action<T, T>;

            mapping(destination, source);
            return destination;
        }

        /// <summary>
        /// Map the source to the destination
        /// </summary>
        /// <returns>The destination</returns>
        public static T Map<T, K>(T destination, K source)
            where T : class, new()
        {
            var type = typeof(K);
            if (destination == null)
                destination = new T();

            object mappingBox;
            Action<T, K> mapping;
            if (!_selfMappers.TryGetValue(type, out mappingBox))
            {
                // Create Mapping logic
                mapping = CreateMap(destination, source);
                _selfMappers[type] = mapping;
            }
            else
                mapping = mappingBox as Action<T, K>;

            mapping(destination, source);
            return destination;
        }

        /// <summary>
        /// Map the source dictionary to the destination
        /// </summary>
        /// <returns>The destination</returns>
        public static T MapDictionary<T>(T dest, IDictionary<string, object> value)
            where T : class, new()
        {
            var type  = typeof(T);
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .ToDictionary(t => t.Name);

            foreach (var pair in value)
            {
                PropertyInfo prop;
                if (props.TryGetValue(pair.Key, out prop) == false)
                    continue;

                prop.SetValue(dest, pair.Value);
            }

            return dest;
        }

        /// <summary>
        /// Map the input keys to primary keys to the destination
        /// </summary>
        /// <returns>The destination</returns>
        public static T MapKeys<T>(DbContext db, T value, object[] keys)
            where T : class, new()
        {
            // Map key to dest
            var objectContext = (db as IObjectContextAdapter).ObjectContext;
            var set           = objectContext.CreateObjectSet<T>();
            var keyMembers    = set.EntitySet.ElementType.KeyMembers;
            var properties    = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            for (var i = 0; i < keys.Length; ++i)
            {
                var member = keyMembers[i]?.Name;
                var prop = properties.Where(p => p.Name == member).SingleOrDefault();

                if (prop != null)
                    prop.SetMethod.Invoke(value, new[] { keys[i] });
            }

            return value;
        }

        private static Action<T, T> CreateMap<T>(T value)
        {
            var owner = typeof(T);
            var props = owner.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            ParameterExpression
                destination = Expression.Parameter(owner),
                source = Expression.Parameter(owner);

            var variables   = new List<ParameterExpression>(capacity: props.Length);
            var expressions = new List<Expression>(capacity: props.Length);

            // Loop through properties and assign them one by one
            for (var i = 0; i < props.Length; ++i)
            {
                var prop     = props[i];
                var propType = prop.PropertyType;

                if (ShouldCopy(propType) == false)
                    continue;

                // Create copy 
                var cached = Expression.Variable(propType);
                variables.Add(cached);

                var doAssign = Expression.Call(destination, prop.SetMethod, cached);
                var test = Expression.NotEqual(cached, Expression.Default(propType));

                expressions.Add(Expression.Assign(
                    cached, Expression.Call(source, prop.GetMethod)));
                expressions.Add(Expression.IfThen(
                    test, doAssign));
            }

            // Build body of lambda
            var body = Expression.Block(
                variables,
                expressions
            );

            var action = Expression.Lambda<Action<T, T>>(
                body, destination, source
            );

            return action.Compile();
        }

        private static Action<T, K> CreateMap<T, K>(T destValue, K sourceValue)
        {
            var destType    = typeof(T);
            var sourceType  = typeof(K);
            var destProps   = destType.GetProperties(BindingFlags.Public | BindingFlags.Instance).ToDictionary(t => t.Name.ToLower());
            var sourceProps = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            ParameterExpression
                destination = Expression.Parameter(destType),
                source      = Expression.Parameter(sourceType);

            var variables   = new List<ParameterExpression>(capacity: sourceProps.Length);
            var expressions = new List<Expression>(capacity: sourceProps.Length);

            // Loop through properties and assign them one by one
            for (var i = 0; i < sourceProps.Length; ++i)
            {
                var prop     = sourceProps[i];
                var propType = prop.PropertyType;

                // Check if this matches a destination property
                PropertyInfo destProp;
                if (destProps.TryGetValue(prop.Name.ToLower(), out destProp) == false)
                    continue;

                expressions.Add(
                    // dest.set_Prop =
                    Expression.Call(destination, destProp.SetMethod, 
                    // source.get_Prop`()
                    Expression.Call(source, prop.GetMethod)));
            }

            // Build body of lambda
            var body = Expression.Block(
                variables,
                expressions
            );

            var action = Expression.Lambda<Action<T, K>>(
                body, destination, source
            );

            return action.Compile();
        }

        private static bool ShouldCopy(Type t)
        {
            if (t.IsPrimitive)
                return true;

            reprocess:
            switch (t.Name)
            {
                case nameof(Nullable):
                case "Nullable`1":
                    t = Nullable.GetUnderlyingType(t);
                    goto reprocess;

                case "String":
                case "DateTime":
                case "DateTimeOffset":
                case "Guid":
                    return true;

                default:
                    return false;
            }
        }
    }

    public static class IDbExtensions
    {
        /// <summary>
        /// Add or update the entity matching the input keys. The parameters in the `value`
        /// object will be mapped to the destination
        /// </summary>
        /// <param name="table">The table containing the entities</param>
        /// <param name="value">The source object</param>
        /// <returns>The matching (or newly created) object</returns>
        public static T AddOrUpdate<T>(this DbContext db, IDbSet<T> table, 
            T value, params object[] keys)
            where T : class, new()
        {
            var dest = table.Find(keys);
            if (dest == null)
            { 
                table.Add(value);

                // Map key to dest
                return Mapping.MapKeys(db, value, keys);
            }
            else
                // Map values
                return Mapping.Map<T, T>(dest, value);
        }

        /// <summary>
        /// Add or update the entity matching the input keys. The parameters in the `value`
        /// object will be mapped to the destination
        /// </summary>
        /// <param name="table">The table containing the entities</param>
        /// <param name="value">The source object</param>
        /// <returns>The matching (or newly created) object</returns>
        public static T AddOrUpdateEx<T, K>(this DbContext db, IDbSet<T> table,
            K value, params object[] keys)
            where T : class, new()
        {
            Contract.Assert(typeof(T) != typeof(K), "The type of the source passed should NOT match the destination.");

            var dest = table.Find(keys);
            if (dest == null)
            {
                table.Add(dest = new T());

                // Map values & keys to dest
                Mapping.Map(dest, value);
                return Mapping.MapKeys(db, dest, keys);
            }
            else
                // Map values
                return Mapping.Map(dest, value);
        }

        /// <summary>
        /// Add or update the entity matching the input keys. The parameters in the `values` 
        /// dictionary will be mapped to the destination
        /// </summary>
        /// <param name="table">The table containing the entities</param>
        /// <param name="values">The dictionary containing the source values</param>
        /// <param name="keys"></param>
        /// <returns>The matching (or newly created) object</returns>
        public static T AddOrUpdate<T>(this DbContext db, IDbSet<T> table,
            IDictionary<string, object> values, params object[] keys)
            where T : class, new()
        {
            var dest = table.Find(keys);
            if (dest == null)
            {
                table.Add(dest = new T());

                // Map values & keys to dest
                Mapping.MapDictionary(dest, values);
                return Mapping.MapKeys(db, dest, keys);
            }
            else
                // Map values
                return Mapping.MapDictionary(dest, values);
        }
    }
}