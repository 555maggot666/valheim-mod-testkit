using System;
using System.Linq;
using System.Reflection;

namespace ValheimTestKit
{
    /// <summary>
    /// Reflection helpers for probing a mod or the game from a scenario. Everything here is read-mostly on
    /// purpose: suites should drive public entry points and use reflection only to read state or to reach the
    /// one internal method they want to exercise.
    /// </summary>
    public static class Reflect
    {
        /// <summary>Finds a type by full name in any loaded assembly (mods included).</summary>
        public static Type FindType(string fullName)
        {
            Type cached = TypeCache.Get(fullName);
            if (cached != null)
            {
                return cached;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type match = assembly.GetType(fullName, throwOnError: false);
                if (match != null)
                {
                    TypeCache.Put(fullName, match);
                    return match;
                }
            }

            throw new TypeLoadException("type '" + fullName + "' is not loaded; is the mod installed?");
        }

        public static object GetStatic(string typeName, string member)
        {
            Type type = FindType(typeName);
            FieldInfo field = type.GetField(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field.GetValue(null);
            }

            PropertyInfo property = type.GetProperty(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return property.GetValue(null);
            }

            throw new MissingMemberException(typeName + "." + member);
        }

        public static object Get(object target, string member)
        {
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    return field.GetValue(target);
                }

                PropertyInfo property = t.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                {
                    return property.GetValue(target);
                }
            }

            throw new MissingMemberException(target.GetType().FullName + "." + member);
        }

        /// <summary>Follows a path such as "Count" or "Entries" through nested members of <paramref name="target"/>.</summary>
        public static object Path(object target, string memberPath)
        {
            object current = target;
            foreach (string part in memberPath.Split('.'))
            {
                if (current == null)
                {
                    return null;
                }

                current = Get(current, part);
            }

            return current;
        }

        /// <summary>Invokes an instance method by name, wrapping the target exception so failures read clearly.</summary>
        public static object Invoke(object target, string methodName, params object[] args)
        {
            MethodInfo method = target.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);

            if (method == null)
            {
                throw new MissingMethodException(target.GetType().FullName + "." + methodName + "/" + args.Length);
            }

            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>Finds a live component of the given mod type, e.g. the mod's MonoBehaviour driver.</summary>
        public static UnityEngine.Object FindComponent(Type type)
        {
            return UnityEngine.Object.FindObjectOfType(type);
        }

        private static class TypeCache
        {
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type> Map =
                new System.Collections.Concurrent.ConcurrentDictionary<string, Type>();

            public static Type Get(string name)
            {
                Type value;
                return Map.TryGetValue(name, out value) ? value : null;
            }

            public static void Put(string name, Type type)
            {
                Map[name] = type;
            }
        }
    }
}
