using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Pcl2Unlock;

/// <summary>补丁执行结果。</summary>
public enum PatchOutcome
{
    /// <summary>本次成功打上。</summary>
    Applied,

    /// <summary>原程序集里已经是解锁状态（重复运行时）。</summary>
    AlreadyApplied,

    /// <summary>找不到预期的代码结构，通常是 PCL2 版本不匹配。</summary>
    NotFound,
}

/// <summary>
/// PCL2「MC 下载功能已关闭」的三处硬编码拦截点。功能本身没有删除，只是被短路：
/// <list type="number">
/// <item>本体下载页 <c>PageDownloadInstall.LoaderInit</c> 塞了一个名叫「关闭下载」的假加载器；</item>
/// <item>整合包安装主流程 <c>ModModpack.ModpackInstall(File, InstanceName, Logo)</c> 识别完包类型后无条件抛异常；</item>
/// <item>整合包按钮入口 <c>ModModpack.ModpackInstall()</c> 只剩一句红色提示。</item>
/// </list>
/// </summary>
public sealed class Pcl2Patcher
{
    private const string BannedMessage = "为遵守相关法律法规";
    private const string FakeLoaderName = "关闭下载";
    private const string DispatchMethodName = "ModpackInstallDispatch";

    private readonly ModuleDef _module;

    public Pcl2Patcher(ModuleDef module) => _module = module;

    /// <summary>返回全部补丁：编号、名称、是否已解锁、应用委托。</summary>
    public IEnumerable<(int Id, string Name, Func<bool> IsApplied, Func<PatchOutcome> Apply)> AllPatches()
    {
        yield return (1, "本体下载：恢复真实版本列表加载器", IsVanillaDownloadUnlocked, PatchVanillaDownloadList);
        yield return (2, "整合包安装：补回按类型分派", IsModpackInstallUnlocked, PatchModpackInstall);
        yield return (3, "整合包按钮：补回文件选择框入口", IsModpackInstallEntryUnlocked, PatchModpackInstallEntry);
    }

    // 判定「是否已解锁」只读扫描，配合 --check 使用，避免重复打补丁。

    private bool IsVanillaDownloadUnlocked()
    {
        var loaderInit = FindType("PCL.PageDownloadInstall")?.Methods.FirstOrDefault(m => m.Name == "LoaderInit");
        if (loaderInit is not { HasBody: true })
        {
            return false;
        }

        var instructions = loaderInit.Body.Instructions;
        return instructions.IndexOfLdstr(FakeLoaderName) < 0 &&
               instructions.Any(i => i.OpCode.Code == Code.Ldsfld && i.Operand is IField f && f.Name == "DlClientListLoader");
    }

    private bool IsModpackInstallUnlocked() =>
        FindType("PCL.ModModpack")?.Methods.Any(m => m.Name == DispatchMethodName) == true;

    private bool IsModpackInstallEntryUnlocked()
    {
        var entry = FindType("PCL.ModModpack")?.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.Parameters.Count == 0);
        if (entry is not { HasBody: true })
        {
            return false;
        }

        var instructions = entry.Body.Instructions;
        bool hasSelectFile = instructions.Any(i => i.OpCode.Code == Code.Call && i.Operand is IMethod m && m.Name == "SelectFile");
        if (!hasSelectFile)
        {
            return false;
        }

        // 过滤器必须用 op_Implicit(!0) 这个「扩展名数组」重载；用成 !1（string）会在
        // SelectFile 里抛 InvalidCastException，所以把用错重载的文件判定为未解锁。
        return instructions
            .Where(i => i.OpCode.Code == Code.Call && i.Operand is IMethod m &&
                        m.Name == "op_Implicit" && m.DeclaringType?.Name == "OneOf`2")
            .All(i =>
            {
                var method = (IMethod)i.Operand;
                return method.MethodSig.Params.Count == 1 &&
                       method.MethodSig.Params[0] is GenericVar typeVar &&
                       typeVar.Number == 0;
            });
    }

    // ───────────────────────────── 补丁 1 ─────────────────────────────

    /// <summary>
    /// 原版应把 <c>ModDownload.DlClientListLoader</c> 交给 <c>PageLoaderInit</c>，
    /// 被改成了 <c>new LoaderTask("关闭下载", 抛异常的 lambda)</c>。
    /// 把这段假加载器的构造整体换成一条 <c>ldsfld</c>，页面就能重新拉版本列表。
    /// </summary>
    private PatchOutcome PatchVanillaDownloadList()
    {
        var page = FindType("PCL.PageDownloadInstall");
        var loaderInit = page?.Methods.FirstOrDefault(m => m.Name == "LoaderInit");
        if (loaderInit is not { HasBody: true })
        {
            return PatchOutcome.NotFound;
        }

        var instructions = loaderInit.Body.Instructions;
        int start = instructions.IndexOfLdstr(FakeLoaderName);

        if (start < 0)
        {
            return PatchOutcome.NotFound;
        }

        int end = -1;
        for (int i = start; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode.Code == Code.Newobj &&
                instructions[i].Operand is IMethod ctor &&
                ctor.DeclaringType is { } dt && dt.Name.StartsWith("LoaderTask`2"))
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            return PatchOutcome.NotFound;
        }

        var loaderField = FindField("ModDownload", "DlClientListLoader");
        if (loaderField == null)
        {
            return PatchOutcome.NotFound;
        }

        for (int i = end; i >= start; i--)
        {
            instructions.RemoveAt(i);
        }

        instructions.Insert(start, Instruction.Create(OpCodes.Ldsfld, loaderField));

        loaderInit.Body.SimplifyBranches();
        loaderInit.Body.UpdateInstructionOffsets();
        return PatchOutcome.Applied;
    }

    // ───────────────────────────── 补丁 2 ─────────────────────────────

    /// <summary>
    /// 三参 <c>ModpackInstall</c> 会把压缩包类型识别到局部变量 <c>num</c>（0 CurseForge / 1 HMCL /
    /// 2 MMC / 3 MCBBS / 4 Modrinth / 9 启动器包 / 其它纯压缩包），随后无条件 <c>throw</c> 掉，
    /// 原本的「按 num 分派到 InstallPackXxx」被整段删除。
    /// 这里新增一个分派方法，并把末尾的 throw 换成分派 + 返回。
    /// </summary>
    private PatchOutcome PatchModpackInstall()
    {
        var type = FindType("PCL.ModModpack");
        var install = type?.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.Parameters.Count == 3);
        if (install is not { HasBody: true })
        {
            return PatchOutcome.NotFound;
        }

        if (type.Methods.Any(m => m.Name == DispatchMethodName))
        {
            return PatchOutcome.AlreadyApplied;
        }

        var body = install.Body;
        var instructions = body.Instructions;

        // 找出「ldstr 禁用提示 → newobj ArgumentException → throw」这个三连。
        int throwStart = -1;
        for (int i = 0; i + 2 < instructions.Count; i++)
        {
            if (instructions[i].OpCode.Code == Code.Ldstr &&
                (instructions[i].Operand as string)?.Contains(BannedMessage) == true &&
                instructions[i + 1].OpCode.Code == Code.Newobj &&
                instructions[i + 2].OpCode.Code == Code.Throw)
            {
                throwStart = i;
            }
        }

        if (throwStart < 0)
        {
            return PatchOutcome.NotFound;
        }

        MethodDef FindInstaller(string name, int parameterCount) =>
            type.Methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == parameterCount)
            ?? throw new InvalidOperationException("缺少方法 " + name);

        var mCurseForge = FindInstaller("InstallPackCurseForge", 5);
        var mModrinth = FindInstaller("InstallPackModrinth", 5);
        var mHmcl = FindInstaller("InstallPackHMCL", 3);
        var mMmc = FindInstaller("InstallPackMMC", 3);
        var mMcbbs = FindInstaller("InstallPackMCBBS", 4);
        var mLauncherPack = FindInstaller("InstallPackLauncherPack", 3);
        var mCompress = FindInstaller("InstallPackCompress", 2);

        var dispatch = BuildDispatchMethod(install, mCompress, new[]
        {
            (Kind: 0, Target: mCurseForge, Args: new[] { 0, 1, 2, 4, 5 }),
            (Kind: 1, Target: mHmcl, Args: new[] { 0, 1, 2 }),
            (Kind: 2, Target: mMmc, Args: new[] { 0, 1, 2 }),
            (Kind: 3, Target: mMcbbs, Args: new[] { 0, 1, 2, 4 }),
            (Kind: 4, Target: mModrinth, Args: new[] { 0, 1, 2, 4, 5 }),
            (Kind: 9, Target: mLauncherPack, Args: new[] { 0, 1, 2 }),
        });
        type.Methods.Add(dispatch);

        // 分派 + 返回；原 throw 三条指令留作不可达填充，避免破坏 try/catch/finally 的边界。
        var retLabel = Instruction.Create(OpCodes.Ldloc, body.Variables[0]);

        var injected = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg, install.Parameters[0]),   // File
            Instruction.Create(OpCodes.Ldloc, body.Variables[1]),       // zipArchive
            Instruction.Create(OpCodes.Ldloc, body.Variables[2]),       // text：包内基准目录
            Instruction.Create(OpCodes.Ldloc, body.Variables[4]),       // num：包类型
            Instruction.Create(OpCodes.Ldarg, install.Parameters[1]),   // InstanceName
            Instruction.Create(OpCodes.Ldarg, install.Parameters[2]),   // Logo
            Instruction.Create(OpCodes.Call, dispatch),
            Instruction.Create(OpCodes.Stloc, body.Variables[0]),       // 结果 LoaderCombo
            Instruction.Create(OpCodes.Leave, retLabel),
        };

        int at = throwStart;
        foreach (var instruction in injected)
        {
            instructions.Insert(at++, instruction);
        }

        instructions.Add(retLabel);
        instructions.Add(Instruction.Create(OpCodes.Ret));

        // 原来 finally 的 HandlerEnd 是「到方法末尾」，追加 ret 之后必须钉在 ret 之前，
        // 否则 leave 会跳进异常处理区间，CLR 判为无效 IL。
        foreach (var handler in body.ExceptionHandlers)
        {
            if (handler.HandlerEnd == null)
            {
                handler.HandlerEnd = retLabel;
            }
        }

        if (body.MaxStack < 6)
        {
            body.MaxStack = 6;
        }

        body.SimplifyBranches();
        body.UpdateInstructionOffsets();
        return PatchOutcome.Applied;
    }

    /// <summary>
    /// 生成 <c>ModpackInstallDispatch(File, Archive, BaseFolder, Kind, InstanceName, Logo)</c>，
    /// 方法体等价于 <c>return kind switch { 0 =&gt; CurseForge(…), … _ =&gt; Compress(File, Archive) }</c>。
    /// 参数下标刻意与分派参数顺序一致，便于阅读。
    /// </summary>
    private static MethodDef BuildDispatchMethod(
        MethodDef install,
        MethodDef compress,
        IEnumerable<(int Kind, MethodDef Target, int[] Args)> cases)
    {
        var module = install.Module;
        var signature = MethodSig.CreateStatic(
            install.ReturnType,
            module.CorLibTypes.String,        // 0 File
            compress.Parameters[1].Type,      // 1 ZipArchive
            module.CorLibTypes.String,        // 2 BaseFolder
            module.CorLibTypes.Int32,         // 3 Kind
            module.CorLibTypes.String,        // 4 InstanceName
            module.CorLibTypes.String);       // 5 Logo

        var dispatch = new MethodDefUser(DispatchMethodName, signature,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static);

        for (int i = 0; i < 6; i++)
        {
            dispatch.ParamDefs.Add(new ParamDefUser("p" + i, (ushort)(i + 1)));
        }

        var body = new CilBody { MaxStack = 5 };
        dispatch.Body = body;

        var switchTargets = new Instruction[10];
        var defaultStart = Instruction.Create(OpCodes.Ldarg, dispatch.Parameters[0]);

        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, dispatch.Parameters[3]));
        var switchInstruction = Instruction.Create(OpCodes.Switch, switchTargets);
        body.Instructions.Add(switchInstruction);
        body.Instructions.Add(defaultStart);

        var blocks = new List<Instruction>();
        foreach (var (kind, target, args) in cases)
        {
            var first = Instruction.Create(OpCodes.Ldarg, dispatch.Parameters[args[0]]);
            switchTargets[kind] = first;
            blocks.Add(first);

            foreach (int arg in args.Skip(1))
            {
                blocks.Add(Instruction.Create(OpCodes.Ldarg, dispatch.Parameters[arg]));
            }

            blocks.Add(Instruction.Create(OpCodes.Call, target));
            blocks.Add(Instruction.Create(OpCodes.Ret));
        }

        // 默认分支：没有清单文件的纯压缩包
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, dispatch.Parameters[1]));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, compress));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        foreach (var block in blocks)
        {
            body.Instructions.Add(block);
        }

        for (int i = 0; i < switchTargets.Length; i++)
        {
            switchTargets[i] ??= defaultStart;
        }

        body.UpdateInstructionOffsets();
        return dispatch;
    }

    // ───────────────────────────── 补丁 3 ─────────────────────────────

    /// <summary>
    /// 无参 <c>ModpackInstall()</c> 只弹出「暂时不再提供 MC 下载功能」的红色提示，
    /// 原本应该是「弹文件选择框 → 调三参重载」。这里用程序集里已有的
    /// <c>MeloongCore.Wpf.Dialogs.SelectFile</c> 调用序列重建它。
    /// </summary>
    private PatchOutcome PatchModpackInstallEntry()
    {
        var type = FindType("PCL.ModModpack");
        var entry = type?.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.Parameters.Count == 0);
        var install = type?.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.Parameters.Count == 3);
        if (entry == null || install == null)
        {
            return PatchOutcome.NotFound;
        }

        // 泛型实例化不手搓签名，直接从程序集里已有的调用点复用成员引用。
        var members = new ExistingMembers(_module);

        var tupleCtor = members.TupleCtor
            ?? throw new InvalidOperationException("找不到 ValueTuple<string[], string> 的构造引用");
        var tupleStelem = members.TupleStelem
            ?? throw new InvalidOperationException("找不到 ValueTuple<string[], string> 的 stelem 引用");
        var oneOfImplicit = members.OneOfImplicit
            ?? throw new InvalidOperationException("找不到 OneOf 的 op_Implicit 引用");
        var nullableCtor = members.NullableCtor
            ?? throw new InvalidOperationException("找不到 Nullable<OneOf> 的构造引用");
        var selectFile = members.SelectFile
            ?? throw new InvalidOperationException("找不到 Dialogs.SelectFile 引用");
        var firstOrDefault = members.FirstOrDefaultString
            ?? throw new InvalidOperationException("找不到 Enumerable.FirstOrDefault<string> 引用");
        var isNullOrEmpty = members.IsNullOrEmpty
            ?? throw new InvalidOperationException("找不到 string.IsNullOrEmpty 引用");

        entry.FreeMethodBody();

        var body = new CilBody { InitLocals = true, MaxStack = 8 };
        var fileVariable = new Local(_module.CorLibTypes.String);
        body.Variables.Add(fileVariable);
        entry.Body = body;

        var il = body.Instructions;
        var hasFile = Instruction.Create(OpCodes.Ldloc, fileVariable);

        void Add(Instruction instruction) => il.Add(instruction);

        // Dialogs.SelectFile("选择整合包文件", false, null, 过滤器).FirstOrDefault()
        Add(Instruction.Create(OpCodes.Ldstr, "选择整合包文件"));
        Add(Instruction.Create(OpCodes.Ldc_I4_0));                                     // 单选
        Add(Instruction.Create(OpCodes.Ldnull));                                       // 起始目录
        Add(Instruction.Create(OpCodes.Ldc_I4_1));                                     // 一组过滤器
        Add(Instruction.Create(OpCodes.Newarr, (ITypeDefOrRef)tupleCtor.DeclaringType));
        Add(Instruction.Create(OpCodes.Dup));
        Add(Instruction.Create(OpCodes.Ldc_I4_0));
        Add(Instruction.Create(OpCodes.Ldc_I4_3));                                     // 三个扩展名
        Add(Instruction.Create(OpCodes.Newarr, _module.CorLibTypes.String.ToTypeDefOrRef()));
        Add(Instruction.Create(OpCodes.Dup));
        Add(Instruction.Create(OpCodes.Ldc_I4_0));
        Add(Instruction.Create(OpCodes.Ldstr, "zip"));
        Add(Instruction.Create(OpCodes.Stelem_Ref));
        Add(Instruction.Create(OpCodes.Dup));
        Add(Instruction.Create(OpCodes.Ldc_I4_1));
        Add(Instruction.Create(OpCodes.Ldstr, "mrpack"));
        Add(Instruction.Create(OpCodes.Stelem_Ref));
        Add(Instruction.Create(OpCodes.Dup));
        Add(Instruction.Create(OpCodes.Ldc_I4_2));
        Add(Instruction.Create(OpCodes.Ldstr, "rar"));
        Add(Instruction.Create(OpCodes.Stelem_Ref));
        Add(Instruction.Create(OpCodes.Ldstr, "整合包"));                               // 过滤器描述
        Add(Instruction.Create(OpCodes.Newobj, (IMethod)tupleCtor));
        Add(Instruction.Create(OpCodes.Stelem, (ITypeDefOrRef)tupleStelem));
        Add(Instruction.Create(OpCodes.Call, (IMethod)oneOfImplicit));
        Add(Instruction.Create(OpCodes.Newobj, (IMethod)nullableCtor));
        Add(Instruction.Create(OpCodes.Call, (IMethod)selectFile));
        Add(Instruction.Create(OpCodes.Call, (IMethod)firstOrDefault));
        Add(Instruction.Create(OpCodes.Stloc, fileVariable));
        Add(Instruction.Create(OpCodes.Ldloc, fileVariable));
        Add(Instruction.Create(OpCodes.Call, (IMethod)isNullOrEmpty));
        Add(Instruction.Create(OpCodes.Brfalse, hasFile));
        Add(Instruction.Create(OpCodes.Ret));                                          // 用户取消
        il.Add(hasFile);
        Add(Instruction.Create(OpCodes.Ldnull));                                       // InstanceName
        Add(Instruction.Create(OpCodes.Ldnull));                                       // Logo
        Add(Instruction.Create(OpCodes.Call, (IMethod)install));
        Add(Instruction.Create(OpCodes.Pop));                                          // 安装器内部已 Start，丢弃返回值
        Add(Instruction.Create(OpCodes.Ret));

        body.UpdateInstructionOffsets();
        return PatchOutcome.Applied;
    }

    // ───────────────────────────── 公共工具 ─────────────────────────────

    private TypeDef FindType(string fullName) => _module.Find(fullName, false);

    private IField FindField(string declaringTypeName, string fieldName) =>
        _module.GetTypes()
            .Where(t => t.HasMethods)
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode.Code == Code.Ldsfld && i.Operand is IField f &&
                        f.DeclaringType != null && f.DeclaringType.Name == declaringTypeName &&
                        f.Name == fieldName)
            .Select(i => ((IField)i.Operand).ResolveFieldDef())
            .FirstOrDefault(f => f != null);

    /// <summary>从程序集现有 IL 里回收补丁 3 需要的成员引用。</summary>
    private sealed class ExistingMembers
    {
        public IMethod TupleCtor { get; }
        public ITypeDefOrRef TupleStelem { get; }
        public IMethod OneOfImplicit { get; }
        public IMethod NullableCtor { get; }
        public IMethod SelectFile { get; }
        public IMethod FirstOrDefaultString { get; }
        public IMethod IsNullOrEmpty { get; }

        public ExistingMembers(ModuleDef module)
        {
            var instructions = module.GetTypes()
                .Where(t => t.HasMethods)
                .SelectMany(t => t.Methods)
                .Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions)
                .ToList();

            TupleCtor = instructions
                .Where(i => i.OpCode.Code == Code.Newobj && i.Operand is IMethod m &&
                            m.DeclaringType?.Name == "ValueTuple`2")
                .Select(i => (IMethod)i.Operand)
                .FirstOrDefault(m => (m.DeclaringType as TypeSpec)?.TypeSig is GenericInstSig sig &&
                                     sig.GenericArguments.Count == 2 &&
                                     sig.GenericArguments[0].FullName == "System.String[]" &&
                                     sig.GenericArguments[1].FullName == "System.String");

            if (TupleCtor?.DeclaringType is { } tupleType)
            {
                TupleStelem = instructions
                    .FirstOrDefault(i => i.OpCode.Code == Code.Stelem && i.Operand is ITypeDefOrRef t &&
                                         t.FullName == tupleType.FullName)?.Operand as ITypeDefOrRef;
            }

            // 过滤器有两个重载：op_Implicit(!0) 收 IEnumerable<(string[] Extensions, string Display)>，
            // op_Implicit(!1) 收 string。必须挑 !0 的那个——传错重载会把数组塞进 string 槽位，
            // SelectFile 里 Is<string>() 误判为真，随后 As<string>() 抛 InvalidCastException。
            OneOfImplicit = instructions
                .Where(i => i.OpCode.Code == Code.Call && i.Operand is IMethod m &&
                            m.Name == "op_Implicit" && m.DeclaringType?.Name == "OneOf`2" &&
                            m.DeclaringType.FullName.Contains("System.ValueTuple`2<System.String[],System.String>"))
                .Select(i => (IMethod)i.Operand)
                .FirstOrDefault(m =>
                {
                    if (m.MethodSig.Params.Count != 1)
                    {
                        return false;
                    }

                    // !0 是类型泛型参数（GenericVar），!1 对应 string 重载
                    return m.MethodSig.Params[0] is GenericVar typeVar && typeVar.Number == 0;
                });

            NullableCtor = instructions
                .Where(i => i.OpCode.Code == Code.Newobj && i.Operand is IMethod m &&
                            m.DeclaringType?.Name == "Nullable`1" &&
                            m.DeclaringType.FullName.Contains("OneOf`2"))
                .Select(i => (IMethod)i.Operand)
                .FirstOrDefault();

            SelectFile = instructions
                .Where(i => i.OpCode.Code == Code.Call && i.Operand is IMethod m &&
                            m.Name == "SelectFile" && m.DeclaringType?.Name == "Dialogs")
                .Select(i => (IMethod)i.Operand)
                .FirstOrDefault();

            FirstOrDefaultString = instructions
                .Where(i => i.OpCode.Code == Code.Call && i.Operand is MethodSpec spec &&
                            spec.Method.Name == "FirstOrDefault" &&
                            spec.GenericInstMethodSig.GenericArguments.Count == 1 &&
                            spec.GenericInstMethodSig.GenericArguments[0].FullName == "System.String")
                .Select(i => (IMethod)i.Operand)
                .FirstOrDefault();

            IsNullOrEmpty = instructions
                .Where(i => i.OpCode.Code == Code.Call && i.Operand is IMethod m &&
                            m.Name == "IsNullOrEmpty" &&
                            m.DeclaringType?.FullName == "System.String")
                .Select(i => (IMethod)i.Operand)
                .FirstOrDefault();
        }
    }
}

internal static class InstructionListExtensions
{
    /// <summary>返回第一条 <c>ldstr</c> 且操作数等于给定字符串的指令下标，找不到返回 -1。</summary>
    public static int IndexOfLdstr(this IList<Instruction> instructions, string value)
    {
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode.Code == Code.Ldstr && (instructions[i].Operand as string) == value)
            {
                return i;
            }
        }

        return -1;
    }
}
