using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Myriagent.Tests.Sync.Isolation;

/// <summary>
/// S6「未配對不載入 SignalR、SQLite、ASP.NET Core、Microsoft.Extensions」的第一道防線（D6）：以 System.Reflection.Metadata 讀組件，
/// 從根型別出發算可達閉包，回報閉包裡參考到禁止組件的地方。
/// <list type="bullet">
/// <item>一般型別：只要可達就掃整個型別——基底型別、介面、明確介面實作、泛型條件、屬性（custom attribute 的建構式）、欄位、屬性、事件、
/// 所有方法的簽章、區域變數簽章、例外處理子句的 catch 型別，以及 IL 裡所有 TypeRef／TypeDef／TypeSpec／MemberRef／MethodDef／MethodSpec／
/// FieldDef／StandAloneSig 運算元。參考到同一組件的型別、方法、欄位就把它的宣告型別加進閉包。</item>
/// <item>編譯器產生的型別（名稱以 &lt; 開頭：lambda 的 <c>&lt;&gt;c</c>、closure、async 狀態機）：以方法為單位，只掃被參考到的那個方法
/// （<c>&lt;&gt;c</c> 收容整個型別所有方法的 lambda，整個掃會把邊界方法本體裡的 lambda 也算進來）。狀態機與 iterator
/// （有實作介面的編譯器產生型別）由執行階段經介面呼叫 MoveNext，所以型別一被參考就掃它全部的方法。</item>
/// <item>標了 <c>[SyncLoadBoundary]</c> 的方法：只掃簽章，不掃本體（D7）。</item>
/// </list>
/// 不跟進其他組件（Protocol 有自己的 ProtocolAssemblyTests）；參考的解析範圍是禁止組件就回報。
/// </summary>
internal static class AssemblyIsolationScanner
{
    public static readonly IReadOnlyList<string> ForbiddenPrefixes =
        ["Microsoft.AspNetCore.", "Microsoft.Extensions.", "Microsoft.Data.Sqlite", "SQLitePCLRaw"];

    /// <summary>SQLite 的原生模組（子程序探針與實機探針另外檢查）</summary>
    public const string ForbiddenNativeModule = "e_sqlite3.dll";

    public const string BoundaryAttributeName = "SyncLoadBoundaryAttribute";

    public static bool IsForbidden(string assemblyName) =>
        ForbiddenPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>從根型別出發的可達閉包裡，參考到禁止組件的地方（「型別.成員 [管道] → 組件：被參考的型別」）</summary>
    public static IReadOnlyList<string> FindForbidden(string assemblyPath, IReadOnlyList<string> rootTypeNames) =>
        Scan(assemblyPath, rootTypeNames).Findings;

    /// <summary>同 <see cref="FindForbidden"/>，另外回傳掃過的型別與方法（確認掃描不是空轉）</summary>
    public static ScanResult Scan(string assemblyPath, IReadOnlyList<string> rootTypeNames)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var scan = new ClosureScan(pe);
        foreach (var name in rootTypeNames)
        {
            var handle = scan.FindType(name) ?? throw new ArgumentException($"組件 {Path.GetFileName(assemblyPath)} 裡沒有根型別 {name}", nameof(rootTypeNames));
            scan.ReachType(handle);
        }

        scan.Run();
        return scan.Result();
    }

    /// <summary>標了 [SyncLoadBoundary] 的方法（型別.方法）與它們的 MethodImplAttributes、是否有 AsyncStateMachineAttribute</summary>
    public static IReadOnlyList<(string Method, MethodImplAttributes Impl, bool IsAsync)> FindBoundaries(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var found = new List<(string, MethodImplAttributes, bool)>();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var attributes = method.GetCustomAttributes().Select(a => AttributeTypeName(md, md.GetCustomAttribute(a))).ToList();
                if (!attributes.Contains(BoundaryAttributeName))
                    continue;
                found.Add(($"{TypeFullName(md, typeHandle)}.{md.GetString(method.Name)}", method.ImplAttributes, attributes.Contains("AsyncStateMachineAttribute")));
            }
        }

        return found;
    }

    /// <summary>某型別底下編譯器產生的巢狀型別（名稱以 &lt; 開頭）</summary>
    public static IReadOnlyList<string> CompilerGeneratedNestedTypes(string assemblyPath, string typeName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var handle = FindTypeHandle(md, typeName) ?? throw new ArgumentException($"沒有型別 {typeName}", nameof(typeName));
        return md.GetTypeDefinition(handle).GetNestedTypes()
            .Select(h => md.GetString(md.GetTypeDefinition(h).Name))
            .Where(name => name.StartsWith('<'))
            .ToList();
    }

    /// <summary>
    /// 某型別自己身上編譯器產生的方法（名稱以 &lt; 開頭：只捕捉 this 的 lambda、區域函式）：它們是型別本身的方法，一般型別整個掃時會連同
    /// 邊界方法裡的 lambda 一起算進來，所以載入邊界的型別也不准有
    /// </summary>
    public static IReadOnlyList<string> CompilerGeneratedMethods(string assemblyPath, string typeName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var handle = FindTypeHandle(md, typeName) ?? throw new ArgumentException($"沒有型別 {typeName}", nameof(typeName));
        return md.GetTypeDefinition(handle).GetMethods()
            .Select(h => md.GetString(md.GetMethodDefinition(h).Name))
            .Where(name => name.StartsWith('<'))
            .ToList();
    }

    internal sealed record ScanResult(IReadOnlyList<string> Findings, IReadOnlyList<string> ScannedTypes, int ScannedMethods);

    // ── 中繼資料的名稱 ──

    internal static string TypeFullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return TypeFullName(md, declaring) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string TypeRefFullName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var name = md.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeRefFullName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static TypeDefinitionHandle? FindTypeHandle(MetadataReader md, string fullName)
    {
        foreach (var handle in md.TypeDefinitions)
        {
            if (TypeFullName(md, handle) == fullName)
                return handle;
        }

        return null;
    }

    private static string AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MethodDefinition:
                var method = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return md.GetString(md.GetTypeDefinition(method.GetDeclaringType()).Name);
            case HandleKind.MemberReference:
                var member = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                return member.Parent.Kind switch
                {
                    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)member.Parent).Name),
                    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)member.Parent).Name),
                    _ => string.Empty,
                };
            default:
                return string.Empty;
        }
    }

    /// <summary>IL 的運算元型別（從 System.Reflection.Emit.OpCodes 建表：單位元組與 0xFE 前綴的兩位元組）</summary>
    private static class IlOpCodes
    {
        public static readonly OperandType?[] OneByte = new OperandType?[256];
        public static readonly OperandType?[] TwoByte = new OperandType?[256];

        static IlOpCodes()
        {
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not OpCode code)
                    continue;
                var value = (ushort)code.Value;
                if (code.Size == 1)
                    OneByte[value & 0xFF] = code.OperandType;
                else if ((value >> 8) == 0xFE)
                    TwoByte[value & 0xFF] = code.OperandType;
            }
        }
    }

    /// <summary>簽章解碼時只收集出現的型別 handle（回傳值沒有意義）</summary>
    private sealed class HandleCollector : ISignatureTypeProvider<bool, object?>
    {
        public List<EntityHandle> Handles { get; } = new();

        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;

        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            Handles.Add(handle);
            return false;
        }

        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            Handles.Add(handle);
            return false;
        }

        public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            Handles.Add(handle);
            return false;
        }

        public bool GetSZArrayType(bool elementType) => false;

        public bool GetArrayType(bool elementType, ArrayShape shape) => false;

        public bool GetByReferenceType(bool elementType) => false;

        public bool GetPointerType(bool elementType) => false;

        public bool GetPinnedType(bool elementType) => false;

        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) => false;

        public bool GetGenericMethodParameter(object? genericContext, int index) => false;

        public bool GetGenericTypeParameter(object? genericContext, int index) => false;

        public bool GetFunctionPointerType(MethodSignature<bool> signature) => false;

        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => false;
    }

    /// <summary>一次掃描的狀態：工作佇列（整個型別、單一方法、單一欄位）與已掃過的集合</summary>
    private sealed class ClosureScan
    {
        private readonly PEReader pe;
        private readonly MetadataReader md;
        private readonly HashSet<TypeDefinitionHandle> wholeTypes = new();
        private readonly HashSet<TypeDefinitionHandle> shells = new();
        private readonly HashSet<MethodDefinitionHandle> methods = new();
        private readonly HashSet<FieldDefinitionHandle> fields = new();
        private readonly HashSet<TypeSpecificationHandle> specs = new();
        private readonly HashSet<MemberReferenceHandle> memberRefs = new();
        private readonly HashSet<MethodSpecificationHandle> methodSpecs = new();
        private readonly Queue<Action> work = new();
        private readonly SortedSet<string> findings = new(StringComparer.Ordinal);
        private readonly HashSet<string> boundaryAttributeTypes;
        private int scannedMethods;

        public ClosureScan(PEReader pe)
        {
            this.pe = pe;
            md = pe.GetMetadataReader();
            boundaryAttributeTypes = new HashSet<string>(StringComparer.Ordinal) { BoundaryAttributeName };
        }

        public TypeDefinitionHandle? FindType(string fullName) => FindTypeHandle(md, fullName);

        public void Run()
        {
            while (work.Count > 0)
                work.Dequeue()();
        }

        public ScanResult Result() =>
            new(findings.ToList(), wholeTypes.Select(h => TypeFullName(md, h)).Order(StringComparer.Ordinal).ToList(), scannedMethods);

        // ── 加進閉包 ──

        /// <summary>參考到同一組件的型別：一般型別整個掃；編譯器產生的只掃外殼（狀態機另外掃全部方法）</summary>
        public void ReachType(TypeDefinitionHandle handle)
        {
            if (IsCompilerGenerated(handle))
            {
                ReachShell(handle);
                return;
            }

            if (wholeTypes.Add(handle))
                work.Enqueue(() => ScanWholeType(handle));
        }

        private void ReachMethod(MethodDefinitionHandle handle)
        {
            var declaring = md.GetMethodDefinition(handle).GetDeclaringType();
            if (!IsCompilerGenerated(declaring))
            {
                ReachType(declaring);
                return;
            }

            ReachShell(declaring);
            if (methods.Add(handle))
                work.Enqueue(() => ScanMethod(handle));
        }

        private void ReachField(FieldDefinitionHandle handle)
        {
            var declaring = md.GetFieldDefinition(handle).GetDeclaringType();
            if (!IsCompilerGenerated(declaring))
            {
                ReachType(declaring);
                return;
            }

            ReachShell(declaring);
            if (fields.Add(handle))
                work.Enqueue(() => ScanField(handle));
        }

        private void ReachShell(TypeDefinitionHandle handle)
        {
            if (!shells.Add(handle))
                return;
            work.Enqueue(() =>
            {
                var type = md.GetTypeDefinition(handle);
                var context = TypeFullName(md, handle);
                Visit(type.BaseType, context, "base");
                var implementations = type.GetInterfaceImplementations();
                foreach (var implementation in implementations)
                    Visit(md.GetInterfaceImplementation(implementation).Interface, context, "interface");
                ScanGenericParameters(type.GetGenericParameters(), context);
                // 狀態機與 iterator：執行階段經介面呼叫它們的方法（MoveNext 等），IL 裡看不到直接參考
                if (implementations.Count > 0)
                {
                    foreach (var method in type.GetMethods())
                    {
                        if (methods.Add(method))
                            work.Enqueue(() => ScanMethod(method));
                    }
                }
            });
        }

        // ── 掃描 ──

        private void ScanWholeType(TypeDefinitionHandle handle)
        {
            var type = md.GetTypeDefinition(handle);
            var context = TypeFullName(md, handle);
            Visit(type.BaseType, context, "base");
            foreach (var implementation in type.GetInterfaceImplementations())
                Visit(md.GetInterfaceImplementation(implementation).Interface, context, "interface");
            foreach (var implementation in type.GetMethodImplementations())
            {
                var impl = md.GetMethodImplementation(implementation);
                Visit(impl.MethodDeclaration, context, "methodimpl");
                Visit(impl.MethodBody, context, "methodimpl");
            }

            ScanGenericParameters(type.GetGenericParameters(), context);
            ScanAttributes(type.GetCustomAttributes(), context);
            foreach (var field in type.GetFields())
            {
                if (fields.Add(field))
                    ScanField(field);
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = md.GetPropertyDefinition(propertyHandle);
                var member = context + "." + md.GetString(property.Name);
                VisitSignature(property.Signature, member, "property", static (decoder, blob) => decoder.DecodeMethodSignature(ref blob));
                ScanAttributes(property.GetCustomAttributes(), member);
            }

            foreach (var eventHandle in type.GetEvents())
            {
                var ev = md.GetEventDefinition(eventHandle);
                Visit(ev.Type, context + "." + md.GetString(ev.Name), "event");
            }

            foreach (var method in type.GetMethods())
            {
                if (methods.Add(method))
                    ScanMethod(method);
            }
        }

        private void ScanField(FieldDefinitionHandle handle)
        {
            var field = md.GetFieldDefinition(handle);
            var member = TypeFullName(md, field.GetDeclaringType()) + "." + md.GetString(field.Name);
            VisitSignature(field.Signature, member, "field", static (decoder, blob) => decoder.DecodeFieldSignature(ref blob));
            ScanAttributes(field.GetCustomAttributes(), member);
        }

        private void ScanMethod(MethodDefinitionHandle handle)
        {
            scannedMethods++;
            var method = md.GetMethodDefinition(handle);
            var member = TypeFullName(md, method.GetDeclaringType()) + "." + md.GetString(method.Name);
            VisitSignature(method.Signature, member, "signature", static (decoder, blob) => decoder.DecodeMethodSignature(ref blob));
            ScanGenericParameters(method.GetGenericParameters(), member);
            var isBoundary = false;
            foreach (var attributeHandle in method.GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(attributeHandle);
                if (boundaryAttributeTypes.Contains(AttributeTypeName(md, attribute)))
                    isBoundary = true;
            }

            ScanAttributes(method.GetCustomAttributes(), member);
            foreach (var parameterHandle in method.GetParameters())
                ScanAttributes(md.GetParameter(parameterHandle).GetCustomAttributes(), member);

            // 載入邊界只看簽章（D7）；抽象方法、extern 方法沒有本體
            if (isBoundary || method.RelativeVirtualAddress == 0)
                return;
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil)
            {
                var signature = md.GetStandaloneSignature(body.LocalSignature);
                var collector = new HandleCollector();
                var decoder = new SignatureDecoder<bool, object?>(collector, md, null);
                var blob = md.GetBlobReader(signature.Signature);
                decoder.DecodeLocalSignature(ref blob);
                foreach (var found in collector.Handles)
                    Visit(found, member, "local");
            }

            foreach (var region in body.ExceptionRegions)
            {
                if (region.Kind == ExceptionRegionKind.Catch)
                    Visit(region.CatchType, member, "catch");
            }

            ScanIl(body.GetILReader(), member);
        }

        private void ScanIl(BlobReader il, string member)
        {
            while (il.RemainingBytes > 0)
            {
                OperandType? operand;
                var first = il.ReadByte();
                if (first == 0xFE)
                    operand = IlOpCodes.TwoByte[il.ReadByte()];
                else
                    operand = IlOpCodes.OneByte[first];
                if (operand is null)
                    throw new InvalidOperationException($"{member}：不認得的 IL 運算碼 0x{first:X2}");
                switch (operand.Value)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        il.Offset += 1;
                        break;
                    case OperandType.InlineVar:
                        il.Offset += 2;
                        break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.InlineString:
                    case OperandType.ShortInlineR:
                        il.Offset += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        il.Offset += 8;
                        break;
                    case OperandType.InlineSwitch:
                        var count = il.ReadInt32();
                        il.Offset += count * 4;
                        break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineSig:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        Visit(MetadataTokens.EntityHandle(il.ReadInt32()), member, "il");
                        break;
                    default:
                        throw new InvalidOperationException($"{member}：沒有處理的運算元型別 {operand.Value}");
                }
            }
        }

        private void ScanAttributes(CustomAttributeHandleCollection attributes, string member)
        {
            foreach (var handle in attributes)
                Visit(md.GetCustomAttribute(handle).Constructor, member, "attribute");
        }

        private void ScanGenericParameters(GenericParameterHandleCollection parameters, string member)
        {
            foreach (var parameterHandle in parameters)
            {
                foreach (var constraintHandle in md.GetGenericParameter(parameterHandle).GetConstraints())
                    Visit(md.GetGenericParameterConstraint(constraintHandle).Type, member, "constraint");
            }
        }

        private void VisitSignature(BlobHandle signature, string member, string channel, Action<SignatureDecoder<bool, object?>, BlobReader> decode)
        {
            var collector = new HandleCollector();
            decode(new SignatureDecoder<bool, object?>(collector, md, null), md.GetBlobReader(signature));
            foreach (var found in collector.Handles)
                Visit(found, member, channel);
        }

        /// <summary>一個中繼資料參考：外部型別看解析範圍、同組件的加進閉包、組合型的（TypeSpec、MemberRef、MethodSpec）拆開再看</summary>
        private void Visit(EntityHandle handle, string member, string channel)
        {
            if (handle.IsNil)
                return;
            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                    CheckTypeReference((TypeReferenceHandle)handle, member, channel);
                    break;
                case HandleKind.TypeDefinition:
                    ReachType((TypeDefinitionHandle)handle);
                    break;
                case HandleKind.TypeSpecification:
                    var spec = (TypeSpecificationHandle)handle;
                    if (specs.Add(spec))
                        VisitSignature(md.GetTypeSpecification(spec).Signature, member, channel, static (decoder, blob) => decoder.DecodeType(ref blob));
                    break;
                case HandleKind.MethodDefinition:
                    ReachMethod((MethodDefinitionHandle)handle);
                    break;
                case HandleKind.FieldDefinition:
                    ReachField((FieldDefinitionHandle)handle);
                    break;
                case HandleKind.MemberReference:
                    var memberRefHandle = (MemberReferenceHandle)handle;
                    if (!memberRefs.Add(memberRefHandle))
                        break;
                    var memberRef = md.GetMemberReference(memberRefHandle);
                    Visit(memberRef.Parent, member, channel);
                    if (memberRef.GetKind() == MemberReferenceKind.Field)
                        VisitSignature(memberRef.Signature, member, channel, static (decoder, blob) => decoder.DecodeFieldSignature(ref blob));
                    else
                        VisitSignature(memberRef.Signature, member, channel, static (decoder, blob) => decoder.DecodeMethodSignature(ref blob));
                    break;
                case HandleKind.MethodSpecification:
                    var methodSpecHandle = (MethodSpecificationHandle)handle;
                    if (!methodSpecs.Add(methodSpecHandle))
                        break;
                    var methodSpec = md.GetMethodSpecification(methodSpecHandle);
                    Visit(methodSpec.Method, member, channel);
                    VisitSignature(methodSpec.Signature, member, channel, static (decoder, blob) => decoder.DecodeMethodSpecificationSignature(ref blob));
                    break;
                case HandleKind.StandaloneSignature:
                    var standalone = md.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                    if (standalone.GetKind() == StandaloneSignatureKind.Method)
                        VisitSignature(standalone.Signature, member, channel, static (decoder, blob) => decoder.DecodeMethodSignature(ref blob));
                    else
                        VisitSignature(standalone.Signature, member, channel, static (decoder, blob) => decoder.DecodeLocalSignature(ref blob));
                    break;
                // ModuleReference（P/Invoke 的 DLL）、組件參考本身不是型別：不在掃描範圍
            }
        }

        private void CheckTypeReference(TypeReferenceHandle handle, string member, string channel)
        {
            var scope = md.GetTypeReference(handle).ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
                scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            if (scope.Kind != HandleKind.AssemblyReference)
                return;
            var assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            if (IsForbidden(assembly))
                findings.Add($"{member} [{channel}] → {assembly}：{TypeRefFullName(md, handle)}");
        }

        /// <summary>名稱以 &lt; 開頭（或在這種型別裡面）的是編譯器產生的</summary>
        private bool IsCompilerGenerated(TypeDefinitionHandle handle)
        {
            for (var current = handle; !current.IsNil; current = md.GetTypeDefinition(current).GetDeclaringType())
            {
                if (md.GetString(md.GetTypeDefinition(current).Name).StartsWith('<'))
                    return true;
            }

            return false;
        }
    }
}
