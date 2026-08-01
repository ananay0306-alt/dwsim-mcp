using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace DwsimMcp
{
    // Reflection helpers behind set_object_property / call_object_method /
    // get_object. These are the escape hatches that keep the named-tool list
    // from becoming a ceiling on DWSIM's several hundred properties.
    public static class Reflect
    {
        public static PropertyInfo FindProperty(object target, string name)
        {
            var t = target.GetType();
            return t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static void SetProperty(object target, string name, JToken value)
        {
            var prop = FindProperty(target, name)
                ?? throw new ToolException(
                    "No property '" + name + "' on " + target.GetType().Name + ". " + NearMisses(target, name));
            if (!prop.CanWrite)
                throw new ToolException("Property '" + prop.Name + "' on " + target.GetType().Name + " is read-only.");
            object coerced;
            try { coerced = Coerce(value, prop.PropertyType); }
            catch (Exception ex)
            {
                throw new ToolException("Cannot convert " + value.ToString(Newtonsoft.Json.Formatting.None)
                    + " to " + Describe(prop.PropertyType) + " for '" + prop.Name + "': " + ex.Message);
            }
            prop.SetValue(target, coerced);
        }

        public static object CallMethod(object target, string name, JArray args)
        {
            args = args ?? new JArray();
            var methods = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                .Where(m => {
                    var ps = m.GetParameters();
                    var required = ps.Count(p => !p.IsOptional);
                    return args.Count >= required && args.Count <= ps.Length;
                })
                .ToList();
            if (methods.Count == 0)
                throw new ToolException("No method '" + name + "' taking " + args.Count
                    + " argument(s) on " + target.GetType().Name + ".");

            Exception last = null;
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                var vals = new object[ps.Length];
                try
                {
                    for (int i = 0; i < ps.Length; i++)
                        vals[i] = i < args.Count ? Coerce(args[i], ps[i].ParameterType)
                                                 : ps[i].DefaultValue;
                }
                catch (Exception ex) { last = ex; continue; }
                return m.Invoke(target, vals);
            }
            throw new ToolException("Arguments did not match any overload of '" + name + "': "
                + (last?.Message ?? "type mismatch"));
        }

        // JSON -> CLR with the conversions DWSIM's API actually needs:
        // enums by name, Nullable<double>, numeric widening, object tags
        // resolved by the caller before we get here.
        public static object Coerce(JToken value, Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                if (value == null || value.Type == JTokenType.Null) return null;
                return Coerce(value, underlying);
            }
            if (value == null || value.Type == JTokenType.Null)
                return type.IsValueType ? Activator.CreateInstance(type) : null;

            if (type.IsEnum)
            {
                var s = value.Type == JTokenType.String ? (string)value : value.ToString();
                try { return Enum.Parse(type, s, ignoreCase: true); }
                catch
                {
                    if (long.TryParse(s, out var n)) return Enum.ToObject(type, n);
                    throw new ToolException("'" + s + "' is not one of: "
                        + string.Join(", ", Enum.GetNames(type)));
                }
            }
            if (type == typeof(string)) return value.Type == JTokenType.String ? (string)value : value.ToString();
            if (type == typeof(double)) return (double)value;
            if (type == typeof(float)) return (float)value;
            if (type == typeof(int)) return (int)value;
            if (type == typeof(long)) return (long)value;
            if (type == typeof(bool)) return (bool)value;
            if (type == typeof(decimal)) return (decimal)value;

            if (type == typeof(double[]) && value is JArray da)
                return da.Select(x => (double)x).ToArray();
            if (type == typeof(string[]) && value is JArray sa)
                return sa.Select(x => (string)x).ToArray();
            if (type == typeof(Dictionary<string, double>) && value is JObject dobj)
                return dobj.Properties().ToDictionary(p => p.Name, p => (double)p.Value);

            // last resort: Newtonsoft's own converter
            return value.ToObject(type);
        }

        // Readable scalar snapshot of an object for get_object.
        public static JObject DumpProperties(object target, int cap = 120)
        {
            var result = new JObject();
            int n = 0;
            foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .OrderBy(p => p.Name))
            {
                if (n >= cap) { result["_truncated"] = true; break; }
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                var t = p.PropertyType;
                var ut = Nullable.GetUnderlyingType(t) ?? t;
                if (!(ut.IsPrimitive || ut.IsEnum || ut == typeof(string)
                      || ut == typeof(double) || ut == typeof(decimal))) continue;
                object v;
                try { v = p.GetValue(target); } catch { continue; }
                result[p.Name] = v == null ? JValue.CreateNull()
                    : ut.IsEnum ? new JValue(v.ToString())
                    : JToken.FromObject(v);
                n++;
            }
            return result;
        }

        public static string NearMisses(object target, string name)
        {
            var names = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(n => n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                         || name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(6).ToList();
            return names.Count > 0 ? "Similar: " + string.Join(", ", names) : "";
        }

        public static string Describe(Type t)
        {
            var u = Nullable.GetUnderlyingType(t);
            if (u != null) return Describe(u) + "?";
            if (t.IsEnum) return t.Name + " (" + string.Join("|", Enum.GetNames(t).Take(12)) + ")";
            return t.Name;
        }

        // JSON-safe rendering of arbitrary method return values.
        public static JToken Render(object value, int depth = 0)
        {
            if (value == null) return JValue.CreateNull();
            var t = value.GetType();
            var ut = Nullable.GetUnderlyingType(t) ?? t;
            if (ut.IsPrimitive || ut == typeof(string) || ut == typeof(decimal)) return JToken.FromObject(value);
            if (ut.IsEnum) return new JValue(value.ToString());
            if (depth >= 2) return new JValue(value.ToString());
            if (value is IDictionary dict)
            {
                var o = new JObject();
                foreach (var k in dict.Keys) o[k.ToString()] = Render(dict[k], depth + 1);
                return o;
            }
            if (value is IEnumerable seq)
            {
                var a = new JArray();
                int n = 0;
                foreach (var item in seq)
                {
                    if (n++ >= 100) { a.Add("...truncated"); break; }
                    a.Add(Render(item, depth + 1));
                }
                return a;
            }
            return new JValue(value.ToString());
        }
    }
}
