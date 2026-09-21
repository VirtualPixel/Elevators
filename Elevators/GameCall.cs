using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Elevators
{
    // Calls into the game that bind at runtime instead of compile time.
    //
    // Semiwork moves optional parameters around between builds. A mod compiled against one build
    // carries the exact overload it saw, and the first time that method runs on a build where the
    // overload changed shape the game throws MissingMethodException and the mod is dead for the
    // session (ScalerCore 1.0.3 died exactly that way). Looking the method up by name once and
    // filling whatever trailing parameters this build has with their own defaults keeps one DLL
    // working across game versions.
    //
    // Cost is one reflection Invoke per call. Fine for anything that is not per frame per object;
    // for hot paths cache the result or call through a delegate instead.
    internal static class GameCall
    {
        private static readonly Dictionary<string, MethodInfo?> Methods = new Dictionary<string, MethodInfo?>();
        private static readonly Dictionary<string, FieldInfo?> Fields = new Dictionary<string, FieldInfo?>();
        private static readonly Dictionary<string, PropertyInfo?> Properties = new Dictionary<string, PropertyInfo?>();

        // Invoke type.name on target with the arguments the mod knows about. Missing trailing
        // parameters get the game's own defaults. Extra arguments the game no longer takes are
        // dropped. Returns null when the method does not exist in this build (logged once).
        public static object? Invoke(object? target, Type type, string name, params object?[] args)
        {
            return Invoke(target, type, name, args, out _);
        }

        // Same, but hands back the bound argument array so ref parameters can be read.
        public static object? Invoke(object? target, Type type, string name, object?[] args, out object?[] bound)
        {
            string key = type.FullName + "." + name + "/" + args.Length;
            if (!Methods.TryGetValue(key, out MethodInfo? method))
            {
                method = Resolve(type, name, args);
                Methods[key] = method;
                if (method == null)
                    Plugin.Logger.LogWarning($"{type.Name}.{name} is not in this game build, call skipped");
            }
            if (method == null)
            {
                bound = args;
                return null;
            }

            ParameterInfo[] pars = method.GetParameters();
            bound = args;
            if (pars.Length != args.Length)
            {
                bound = new object?[pars.Length];
                Array.Copy(args, bound, Math.Min(args.Length, pars.Length));
                for (int i = args.Length; i < pars.Length; i++)
                {
                    bound[i] = pars[i].HasDefaultValue ? pars[i].DefaultValue
                        : pars[i].ParameterType.IsValueType ? Activator.CreateInstance(pars[i].ParameterType)
                        : null;
                }
            }
            return method.Invoke(target, bound);
        }

        // Read a field or property by name, whichever this build has.
        public static T? Read<T>(object? target, Type type, string name) where T : class
        {
            string key = type.FullName + "." + name;
            if (!Fields.TryGetValue(key, out FieldInfo? field))
            {
                field = AccessTools.Field(type, name);
                Fields[key] = field;
            }
            if (field != null)
                return field.GetValue(target) as T;
            if (!Properties.TryGetValue(key, out PropertyInfo? property))
            {
                property = AccessTools.Property(type, name);
                Properties[key] = property;
            }
            return property != null ? property.GetValue(target) as T : null;
        }

        // Pick the overload whose leading parameters accept our arguments. Prefer one that takes
        // at least as many parameters as we pass (extra ones must have defaults), then the one
        // closest in length.
        private static MethodInfo? Resolve(Type type, string name, object?[] args)
        {
            MethodInfo? best = null;
            int bestRank = int.MaxValue;
            foreach (MethodInfo m in type.GetMethods(AccessTools.all))
            {
                if (m.Name != name)
                    continue;
                ParameterInfo[] p = m.GetParameters();
                if (!LeadingArgsFit(p, args))
                    continue;
                bool longEnough = p.Length >= args.Length;
                if (longEnough)
                {
                    bool tailHasDefaults = true;
                    for (int i = args.Length; i < p.Length; i++)
                    {
                        if (!p[i].HasDefaultValue && !p[i].ParameterType.IsValueType && !p[i].ParameterType.IsClass)
                            tailHasDefaults = false;
                    }
                    if (!tailHasDefaults)
                        continue;
                }
                int rank = (longEnough ? 0 : 1000) + Math.Abs(p.Length - args.Length);
                if (rank < bestRank)
                {
                    best = m;
                    bestRank = rank;
                }
            }
            return best;
        }

        private static bool LeadingArgsFit(ParameterInfo[] p, object?[] args)
        {
            int n = Math.Min(p.Length, args.Length);
            for (int i = 0; i < n; i++)
            {
                Type t = p[i].ParameterType;
                if (t.IsByRef)
                    t = t.GetElementType()!;
                if (args[i] == null)
                {
                    if (t.IsValueType)
                        return false;
                    continue;
                }
                if (!t.IsInstanceOfType(args[i]))
                    return false;
            }
            return true;
        }
    }
}
