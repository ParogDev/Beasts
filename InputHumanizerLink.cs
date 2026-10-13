#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.Shared;
using Vector2 = System.Numerics.Vector2;

namespace Beasts;

/// <summary>
/// Typed calls into the input controller InputHumanizer hands out through PluginBridge
/// ("InputHumanizer.GetInputController"). The controller's type lives in InputHumanizer's assembly, which this
/// plugin never references (no compile-linking between plugins), so the methods are found by name on the
/// controller's runtime type and compiled once into delegates: each click is then a direct call, not a C# runtime
/// binder lookup per call (the old <c>dynamic</c> sites). A controller of the same type reuses the bound link; a new
/// type (InputHumanizer reloaded or updated) is bound again.
/// <para>A method that is missing or whose signature no longer fits is broken: <see cref="Bind"/> returns null and
/// names the member and the overloads it did find, instead of a binder exception in the middle of a click.</para>
/// </summary>
internal sealed class InputHumanizerLink
{
    /// <summary><c>KeyDown(Keys)</c>; its result is not awaited (it never was).</summary>
    public required Action<object, Keys> KeyDown { get; init; }

    /// <summary><c>Click(Vector2)</c>, awaitable.</summary>
    public required Func<object, Vector2, SyncTask<bool>> Click { get; init; }

    /// <summary><c>KeyUp(Keys, bool releaseImmediately)</c>, awaitable.</summary>
    public required Func<object, Keys, bool, SyncTask<bool>> KeyUp { get; init; }

    public Type ControllerType { get; private init; } = typeof(object);

    private static readonly MethodInfo AwaitSyncTaskDef =
        typeof(InputHumanizerLink).GetMethod(nameof(AwaitSyncTask), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo AwaitTaskMethod =
        typeof(InputHumanizerLink).GetMethod(nameof(AwaitTask), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo FromResultBool =
        typeof(SyncTask).GetMethod(nameof(SyncTask.FromResult))!.MakeGenericMethod(typeof(bool));

    /// <summary>Bind the three calls on <paramref name="controllerType"/>. Null with <paramref name="broken"/> naming
    /// the first link that doesn't fit.</summary>
    public static InputHumanizerLink? Bind(Type controllerType, out string? broken)
    {
        broken = null;
        var name = controllerType.FullName ?? controllerType.Name;
        if (!typeof(IDisposable).IsAssignableFrom(controllerType))
        {
            broken = $"{name} is not IDisposable: the input lock could never be released";
            return null;
        }
        try
        {
            var ctl = Expression.Parameter(typeof(object), "controller");
            var key = Expression.Parameter(typeof(Keys), "key");
            var pos = Expression.Parameter(typeof(Vector2), "pos");
            var now = Expression.Parameter(typeof(bool), "releaseImmediately");

            var keyDown = Find(controllerType, "KeyDown", new[] { typeof(Keys) }, out broken);
            if (keyDown == null) return null;
            var click = Find(controllerType, "Click", new[] { typeof(Vector2) }, out broken);
            if (click == null) return null;
            var keyUp = Find(controllerType, "KeyUp", new[] { typeof(Keys), typeof(bool) }, out broken);
            if (keyUp == null) return null;

            var keyDownCall = CallWith(ctl, keyDown, key);
            var clickBody = Awaitable(CallWith(ctl, click, pos), "Click", out broken);
            if (clickBody == null) return null;
            var keyUpBody = Awaitable(CallWith(ctl, keyUp, key, now), "KeyUp", out broken);
            if (keyUpBody == null) return null;

            return new InputHumanizerLink
            {
                ControllerType = controllerType,
                KeyDown = Expression.Lambda<Action<object, Keys>>(keyDownCall, ctl, key).Compile(),
                Click = Expression.Lambda<Func<object, Vector2, SyncTask<bool>>>(clickBody, ctl, pos).Compile(),
                KeyUp = Expression.Lambda<Func<object, Keys, bool, SyncTask<bool>>>(keyUpBody, ctl, key, now).Compile(),
            };
        }
        catch (Exception ex)
        {
            broken = $"binding {name}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>A public instance method <paramref name="method"/> whose leading parameters take
    /// <paramref name="args"/> (same type, a base type, or Nullable of it) and whose other parameters are optional.
    /// Looked up on the type and its interfaces (explicit implementations are only visible there).</summary>
    private static MethodInfo? Find(Type type, string method, Type[] args, out string? broken)
    {
        broken = null;
        var candidates = new[] { type }.Concat(type.GetInterfaces())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.Name == method && !m.IsGenericMethodDefinition)
            .ToList();
        foreach (var m in candidates)
        {
            var ps = m.GetParameters();
            if (ps.Length < args.Length) continue;
            var fits = true;
            for (var i = 0; i < ps.Length && fits; i++)
                fits = i < args.Length ? Accepts(ps[i].ParameterType, args[i]) : ps[i].IsOptional;
            if (fits) return m;
        }
        var wanted = $"{method}({string.Join(", ", args.Select(a => a.Name))})";
        broken = candidates.Count == 0
            ? $"{type.FullName}: no public method {method} (wanted {wanted})"
            : $"{type.FullName}: no {method} overload takes {wanted}; found {string.Join("; ", candidates.Select(Signature).Distinct())}";
        return null;
    }

    private static bool Accepts(Type param, Type arg) =>
        param.IsAssignableFrom(arg) || Nullable.GetUnderlyingType(param) == arg;

    private static string Signature(MethodInfo m) =>
        $"{TypeName(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => TypeName(p.ParameterType) + (p.IsOptional ? " = default" : "")))})";

    private static string TypeName(Type t) =>
        Nullable.GetUnderlyingType(t) is { } u ? TypeName(u) + "?"
        : t.IsGenericType ? $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>"
        : t.Name;

    /// <summary>The call with the given leading arguments and every other parameter at its declared default.</summary>
    private static Expression CallWith(ParameterExpression ctl, MethodInfo m, params ParameterExpression[] given)
    {
        var ps = m.GetParameters();
        var args = new Expression[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            if (i < given.Length)
                args[i] = given[i].Type == pt ? given[i] : Expression.Convert(given[i], pt);
            else
            {
                var dv = ps[i].DefaultValue;
                args[i] = dv == null || dv == DBNull.Value || dv == Missing.Value
                    ? Expression.Default(pt)
                    : Expression.Convert(Expression.Constant(dv), pt);   // int -> enum, T -> T?
            }
        }
        return Expression.Call(Expression.Convert(ctl, m.DeclaringType!), m, args);
    }

    /// <summary>Wrap a call so it returns SyncTask&lt;bool&gt;: SyncTask&lt;T&gt; and Task are awaited, bool and void
    /// complete at once. Any other return type can't be awaited (the old dynamic await would have thrown).</summary>
    private static Expression? Awaitable(Expression call, string what, out string? broken)
    {
        broken = null;
        var rt = call.Type;
        if (rt == typeof(SyncTask<bool>)) return call;
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(SyncTask<>))
            return Expression.Call(AwaitSyncTaskDef.MakeGenericMethod(rt.GetGenericArguments()[0]), call);
        if (typeof(Task).IsAssignableFrom(rt))
            return Expression.Call(AwaitTaskMethod, Expression.Convert(call, typeof(Task)));
        if (rt == typeof(bool))
            return Expression.Call(FromResultBool, call);
        if (rt == typeof(void))
            return Expression.Block(call, Expression.Call(FromResultBool, Expression.Constant(true)));
        broken = $"{what} returns {TypeName(rt)}: expected SyncTask<T>, Task, bool or void";
        return null;
    }

    private static async SyncTask<bool> AwaitSyncTask<T>(SyncTask<T> task)
    {
        await task;
        return true;
    }

    private static async SyncTask<bool> AwaitTask(Task task)
    {
        await task;
        return true;
    }
}
