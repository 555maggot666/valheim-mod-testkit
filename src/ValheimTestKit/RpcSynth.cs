using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace ValheimTestKit
{
    /// <summary>
    /// Synthetic peer: delivers a routed RPC into the game's own handler without a second Steam account.
    ///
    /// <para><see cref="Send"/> uses the real send path (<c>ZRoutedRpc.InvokeRoutedRPC</c>).</para>
    /// <para><see cref="Deliver"/> mirrors the real receive path: it looks the method up in the game's registry,
    /// builds the parameter package with <c>ZRpc.Serialize</c> exactly like <c>InvokeRoutedRPC</c> does, and
    /// passes it directly to <c>RoutedMethodBase.Invoke(long, ZPackage)</c>, the same entry point
    /// <c>ZRoutedRpc.HandleRoutedRPC</c> uses. Typed registrations (<c>RoutedMethod&lt;bool&gt;</c>,
    /// <c>RoutedMethod&lt;Vector3&gt;</c>, …) deserialize their arguments inside that method, so calling it is
    /// what a real peer's packet does. This holds for <c>Register&lt;ZPackage&gt;</c> handlers too: the
    /// <see cref="ZPackage"/> value is nested into the parameter package, as on the network.</para>
    /// </summary>
    public static class RpcSynth
    {
        private static FieldInfo _functions;
        private static MethodInfo _stableHash;
        private static bool _resolved;
        private static bool _hashFallback;

        /// <summary>Sends a payload through the normal routed path (Everybody = all peers, or one peer id).</summary>
        public static void Send(string rpcName, ZPackage payload, long target = 0L)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                throw new InvalidOperationException("ZRoutedRpc.instance is null (not connected)");
            }

            rpc.InvokeRoutedRPC(target, rpcName, payload);
        }

        /// <summary>
        /// Delivers <paramref name="values"/> to the handler registered as <paramref name="rpcName"/>, as if they
        /// had arrived from <paramref name="sender"/>. The parameter package is built with <c>ZRpc.Serialize</c>
        /// (exactly what <c>ZRoutedRpc.InvokeRoutedRPC</c> sends: typed values raw, a <see cref="ZPackage"/>
        /// nested) and handed directly to <c>RoutedMethodBase.Invoke</c>, so typed handlers and
        /// <c>Register&lt;ZPackage&gt;</c> handlers see what a real peer's packet would give them.
        /// Returns false when no handler is registered under that name.
        /// </summary>
        public static bool Deliver(string rpcName, long sender, params object[] values)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                throw new InvalidOperationException("ZRoutedRpc.instance is null (not connected)");
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            object routedMethod = FindMethod(rpc, rpcName);
            if (routedMethod == null)
            {
                return false;
            }

            // What the network hands to RoutedMethodBase.Invoke: data.m_parameters itself, built exactly like
            // ZRoutedRpc.InvokeRoutedRPC builds it (ZRpc.Serialize: typed values raw, a ZPackage nested).
            var parameters = new ZPackage();
            ZRpc.Serialize(values, ref parameters);
            parameters.SetPos(0);

            MethodInfo invoke = FindInvoke(routedMethod.GetType());
            if (invoke == null)
            {
                throw new InvalidOperationException(
                    "RoutedMethodBase.Invoke(long, ZPackage) not found on " + routedMethod.GetType().FullName +
                    "; the game's RPC layer changed");
            }

            try
            {
                invoke.Invoke(routedMethod, new object[] { sender, parameters });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }

            return true;
        }

        /// <summary>True when a handler is registered under <paramref name="rpcName"/>.</summary>
        public static bool IsRegistered(string rpcName)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            return rpc != null && FindMethod(rpc, rpcName) != null;
        }

        /// <summary>The game's own string hash, resolved from the assembly that actually defines it.</summary>
        public static int StableHash(string text)
        {
            Resolve();
            if (_stableHash != null)
            {
                return (int)_stableHash.Invoke(null, new object[] { text });
            }

            _hashFallback = true;
            return FallbackHash(text);
        }

        /// <summary>True when <see cref="StableHash"/> had to use its own copy of the algorithm.</summary>
        public static bool UsesHashFallback
        {
            get
            {
                Resolve();
                return _hashFallback || _stableHash == null;
            }
        }

        /// <summary>Builds a package from raw bytes, positioned at the start.</summary>
        public static ZPackage FromBytes(byte[] bytes)
        {
            var package = new ZPackage(bytes);
            package.SetPos(0);
            return package;
        }

        /// <summary>Rewinds a package so it can be read again. Not needed before <see cref="Deliver"/>.</summary>
        public static ZPackage Rewind(ZPackage package)
        {
            package.SetPos(0);
            return package;
        }

        /// <summary>Dumps a package as base64 so a scenario can persist or compare it.</summary>
        public static string ToBase64(ZPackage package)
        {
            return Convert.ToBase64String(package.GetArray());
        }

        public static string Describe(ZPackage package)
        {
            var sb = new StringBuilder();
            sb.Append("size=").Append(package.Size()).Append(" pos=").Append(package.GetPos());
            return sb.ToString();
        }

        // ---------------------------------------------------------------- internals

        private static object FindMethod(ZRoutedRpc rpc, string rpcName)
        {
            Resolve();
            if (_functions == null)
            {
                throw new InvalidOperationException("ZRoutedRpc.m_functions not found; the game may have changed");
            }

            var registry = (IDictionary)_functions.GetValue(rpc);
            int hash = StableHash(rpcName);
            return registry.Contains(hash) ? registry[hash] : null;
        }

        private static MethodInfo FindInvoke(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                MethodInfo method = t.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(long), typeof(ZPackage) }, null);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _functions = typeof(ZRoutedRpc).GetField("m_functions", BindingFlags.Instance | BindingFlags.NonPublic);
            _stableHash = FindStableHash();
            _resolved = true;
        }

        /// <summary>GetStableHashCode is an extension method on StringExtensionMethods in assembly_utils.</summary>
        private static MethodInfo FindStableHash()
        {
            var assemblies = new List<Assembly>();

            Type utils = typeof(ZRoutedRpc).Assembly.GetType("Utils");
            if (utils != null)
            {
                assemblies.Add(utils.Assembly);
            }

            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.GetName().Name == "assembly_utils" && !assemblies.Contains(loaded))
                {
                    assemblies.Add(loaded);
                }
            }

            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }

                if (types == null)
                {
                    continue;
                }

                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type == null)
                    {
                        continue;
                    }

                    MethodInfo method = type.GetMethod("GetStableHashCode",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(string) }, null);
                    if (method != null && method.ReturnType == typeof(int))
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        private static int FallbackHash(string text)
        {
            // Same algorithm as the game's StringExtensionMethods.GetStableHashCode (djb2 with a second stream).
            unchecked
            {
                int hash1 = 5381;
                int hash2 = hash1;
                for (int i = 0; i < text.Length && text[i] != '\0'; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ text[i];
                    if (i == text.Length - 1 || text[i + 1] == '\0')
                    {
                        break;
                    }

                    hash2 = ((hash2 << 5) + hash2) ^ text[i + 1];
                }

                return hash1 + (hash2 * 1566083941);
            }
        }
    }
}
