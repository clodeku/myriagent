using System.Reflection;
using System.Reflection.Emit;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 以 IL 列出一個型別（含編譯器產生的巢狀型別：lambda 的閉包、迭代器、非同步狀態機）裡呼叫、建構與取用的成員：
/// call、callvirt、newobj、ldftn、ldvirtftn 的目標。給「這個類別只准經某個入口開連線」這類結構性的測試用（不執行程式碼）。
/// 解析不了的權杖（泛型的開放型別等）略過。
/// </summary>
internal static class IlCalls
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => f.GetValue(null))
        .OfType<OpCode>()
        .ToDictionary(o => o.Value);

    public static IReadOnlyList<MemberInfo> Of(Type type)
    {
        var members = new List<MemberInfo>();
        foreach (var t in WithNested(type))
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var method in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
                members.AddRange(Scan(method));
        }
        return members;
    }

    private static IEnumerable<Type> WithNested(Type type)
    {
        yield return type;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var t in WithNested(nested))
                yield return t;
        }
    }

    private static IEnumerable<MemberInfo> Scan(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            yield break;
        var typeArgs = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE)
                value = (short)(0xFE00 | il[i++]);
            if (!OpCodesByValue.TryGetValue(value, out var op))
                throw new InvalidOperationException($"{method.DeclaringType}.{method.Name} 的 IL 在 {i} 有不認得的指令 0x{value:X}");
            var operand = i;
            i += OperandSize(op, il, i);
            if (op.OperandType != OperandType.InlineMethod)
                continue;
            if (op != OpCodes.Call && op != OpCodes.Callvirt && op != OpCodes.Newobj && op != OpCodes.Ldftn && op != OpCodes.Ldvirtftn)
                continue;
            MemberInfo? member;
            try
            {
                member = method.Module.ResolveMember(BitConverter.ToInt32(il, operand), typeArgs, methodArgs);
            }
            catch (ArgumentException)
            {
                member = null;
            }
            if (member is not null)
                yield return member;
        }
    }

    private static int OperandSize(OpCode op, byte[] il, int at) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
        _ => 4,
    };
}
