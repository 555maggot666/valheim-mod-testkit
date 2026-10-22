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
    /// Two directions are covered:
    /// <list type="bullet">
    /// <item><see cref="Send"/> goes through the real send path (<c>ZRoutedRpc.InvokeRoutedRPC</c>).</item>
    /// <item><see cref="Deliver"/> calls the handler registered under a name directly, with a sender id of your
    /// choice — that is exactly what a remote peer's packet would trigger after deserialization.</item>
    /// </list>
    /// </summary>
    public static class RpcSynth
    {
        private static MethodInfo _stableHash;
        private static FieldInfo _functions;
        private static bool _resolved;

        /// <summary>Sends a package through the normal routed path (Everybody = all peers, or one peer id).</summary>
        public static void Send(string rpcName, ZPackage package, long target = 0L)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                throw new InvalidOperationException("ZRoutedRpc.instance is null (not connected)");
            }

            rpc.InvokeRoutedRPC(target, rpcName, package);
        }

        /// <summary>
        /// Delivers a package to the handler registered as <paramref name="rpcName"/>, as if it had arrived from
        /// <paramref name="sender"/>. Returns false when no handler is registered under that name.
        /// </summary>
        public static bool Deliver(string rpcName, ZPackage package, long sender)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                throw new InvalidOperationException("ZRoutedRpc.instance is null (not connected)");
            }

            Resolve();
            if (_functions == null)
            {
                throw new InvalidOperationException("ZRoutedRpc.m_functions not found; the game may have changed");
            }

            var registry = (IDictionary)_functions.GetValue(rpc);
            int hash = StableHash(rpcName);
            if (!registry.Contains(hash))
            {
                return false;
            }

            object routedMethod = registry[hash];
            Delegate handler = FindHandlerDelegate(routedMethod);
            if (handler == null)
            {
                throw new InvalidOperationException("no delegate found on the routed method for " + rpcName);
            }

            try
            {
                handler.DynamicInvoke(sender, package);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }

            return true;
        }

        /// <summary>The game's own string hash, resolved from assembly_valheim so it cannot drift.</summary>
        public static int StableHash(string text)
        {
            Resolve();
            if (_stableHash != null)
            {
                return (int)_stableHash.Invoke(null, new object[] { text });
            }

            // Fallback: the well known Unity "stable hash" variant used by Valheim.
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

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            Type rpcType = typeof(ZRoutedRpc);
            _functions = rpcType.GetField("m_functions", BindingFlags.Instance | BindingFlags.NonPublic);

            Type utils = rpcType.Assembly.GetType("Utils");
            if (utils != null)
            {
                _stableHash = utils.GetMethod("GetStableHashCode", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
            }
        }

        private static Delegate FindHandlerDelegate(object routedMethod)
        {
            for (Type t = routedMethod.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (typeof(Delegate).IsAssignableFrom(fields[i].FieldType))
                    {
                        return fields[i].GetValue(routedMethod) as Delegate;
                    }
                }
            }

            return null;
        }

        /// <summary>Builds a package from raw bytes, positioned at the start.</summary>
        public static ZPackage FromBytes(byte[] bytes)
        {
            var package = new ZPackage(bytes);
            package.SetPos(0);
            return package;
        }

        /// <summary>Rewinds a package so it can be decoded again (encoding leaves it at the end).</summary>
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
            StringBuilder sb = new StringBuilder();
            sb.Append("size=").Append(package.Size()).Append(" pos=").Append(package.GetPos());
            return sb.ToString();
        }
    }
}
