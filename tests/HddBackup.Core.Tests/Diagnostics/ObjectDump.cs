using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>
    /// Renders every instance field (private ones too) of an object graph, so a test can assert
    /// that a planted secret appears nowhere in a result, however it is stored.
    /// </summary>
    internal static class ObjectDump
    {
        public static string Of(object root)
        {
            var sb = new StringBuilder();
            Walk(root, sb, new HashSet<object>(new ReferenceComparer()), 0);
            return sb.ToString();
        }

        private static void Walk(object o, StringBuilder sb, HashSet<object> seen, int depth)
        {
            if (o == null || depth > 24)
            {
                return;
            }

            var type = o.GetType();
            if (o is string s)
            {
                sb.Append(s).Append('\n');
                return;
            }

            if (type.IsPrimitive || type.IsEnum || o is decimal || o is DateTime || o is DateTimeOffset || o is TimeSpan || o is Guid)
            {
                sb.Append(o).Append('\n');
                return;
            }

            if (type.IsClass && !seen.Add(o))
            {
                return;
            }

            sb.Append(o.ToString()).Append('\n');
            if (o is IEnumerable items)
            {
                foreach (var item in items)
                {
                    Walk(item, sb, seen, depth + 1);
                }
            }

            if (type.Namespace != null && type.Namespace.StartsWith("System", StringComparison.Ordinal))
            {
                return;
            }

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Walk(f.GetValue(o), sb, seen, depth + 1);
                }
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
