using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// PCL2 原版 MC 下载恢复补丁
// 原理：2.99.0.0 因 RestrictMcDownload(Const) 在编译期被 DCE，
//       PageDownloadInstall.LoaderInit 里的 Else 分支（真实 DlClientListLoader 绑定）被整段删除。
//       本补丁重建该方法体，恢复为等价于 2.14 官方实现的形态。
class Patcher
{
    // 特征串：限制文案（用于安全识别）
    const string Magic = "\u4e3a\u9075\u5b88\u76f8\u5173\u6cd5\u5f8b\u6cd5\u89c4";
    const string StubName = "\u5173\u95ed\u4e0b\u8f7d"; // "关闭下载"

    static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);

        if (args.Length < 2)
        {
            Console.WriteLine("用法: Patcher.exe <输入exe> <输出exe> [--inplace]");
            return 2;
        }

        string src = args[0];
        string dst = args[1];
        bool inplace = args.Contains("--inplace");

        if (!File.Exists(src)) { Console.WriteLine("错误: 找不到 " + src); return 2; }

        // 输出到临时文件，全部成功后才原子替换：保证绝不写坏目标
        string tmp = dst + ".patching.tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        try
        {
            // 依赖在运行时从目标 exe 自身解出，仓库不附带任何 PCL 二进制
            var resolver = new EmbeddedResolver(src);
            resolver.ExtractDependencies();
            var asm = AssemblyDefinition.ReadAssembly(src, new ReaderParameters
            {
                ReadWrite = false,
                AssemblyResolver = resolver,
                ReadingMode = ReadingMode.Deferred,
            });
            var mod = asm.MainModule;

            var pdi = mod.GetType("PCL.PageDownloadInstall");
            if (pdi == null) { Console.WriteLine("错误: 不是预期的 PCL2 程序集（缺 PageDownloadInstall）"); return 3; }

            var loaderInit = pdi.Methods.FirstOrDefault(m => m.Name == "LoaderInit" && m.HasBody);
            if (loaderInit == null) { Console.WriteLine("错误: 找不到 PageDownloadInstall.LoaderInit"); return 3; }

            // ---------- 安全识别：必须确认当前是“被限制”形态 ----------
            bool hasStub = false, hasReal = false;
            foreach (var ins in loaderInit.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldsfld)
                {
                    var fr = ins.Operand as FieldReference;
                    if (fr != null && fr.Name == "DlClientListLoader") hasReal = true;
                }
                if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string)
                {
                    string s = (string)ins.Operand;
                    if (s.Contains(Magic) || s == StubName) hasStub = true;
                }
            }

            if (hasReal && !hasStub)
            {
                Console.WriteLine("该 exe 已是恢复状态，无需重复打补丁。");
                return 0;
            }
            if (!hasStub)
            {
                Console.WriteLine("错误: LoaderInit 形态无法识别（既无限制桩也无真实加载器）。");
                Console.WriteLine("      可能 PCL 版本结构已变化，已中止，目标文件未改动。");
                return 4;
            }

            // ---------- 解析所需成员 ----------
            var modDownload = mod.GetType("PCL.ModDownload");
            if (modDownload == null) { Console.WriteLine("错误: 找不到 PCL.ModDownload"); return 3; }

            var realLoaderField = modDownload.Fields.FirstOrDefault(f => f.Name == "DlClientListLoader");
            if (realLoaderField == null)
            {
                Console.WriteLine("错误: 找不到 ModDownload.DlClientListLoader —— 该版本可能已彻底删除加载器，无法恢复。");
                return 3;
            }

            // 复用现存的 PageLoaderInit 调用，读取其真实签名（保证泛型实参一致）
            MethodReference pageLoaderInit = null;
            foreach (var ins in loaderInit.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Call) continue;
                var mr = ins.Operand as MethodReference;
                if (mr != null && mr.Name == "PageLoaderInit") { pageLoaderInit = mr; break; }
            }
            if (pageLoaderInit == null) { Console.WriteLine("错误: LoaderInit 中找不到 PageLoaderInit 调用"); return 3; }

            // 属性 getter
            var loadMinecraft = Prop(pdi, "LoadMinecraft");
            var panLoad = Prop(pdi, "PanLoad");
            var panAllBack = Prop(pdi, "PanAllBack");

            // 保留原有的控制权初始化调用（ControlInit），它是 LoaderInit 的第一条语句
            MethodReference controlInit = null;
            foreach (var ins in loaderInit.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Call || !(ins.Operand is MethodReference)) continue;
                var mr = (MethodReference)ins.Operand;
                if (mr.Name == "ControlInit" && mr.DeclaringType.FullName == pdi.FullName) { controlInit = mr; break; }
            }

            // 沿用原方法的 onFinish 回调 lambda（2.99 与 2.14 完全一致，都调用 LoadMinecraft_OnFinish）
            MethodReference onFinishLambda = null;
            foreach (var ins in loaderInit.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Ldftn || !(ins.Operand is MethodReference)) continue;
                var mr = (MethodReference)ins.Operand;
                if (mr.Name.IndexOf("Lambda", StringComparison.Ordinal) >= 0) { onFinishLambda = mr; break; }
            }
            if (onFinishLambda == null) { Console.WriteLine("错误: 找不到 onFinish 回调 lambda"); return 3; }

            // ---------- 外科手术式替换 ----------
            // 只替换“构造假 LoaderTask”的那一段表达式，其余指令一律原样保留。
            // 计算栈在此期间净变化为 0（原段压入 1 个 LoaderTask，新段压入 1 个 LoaderTask），
            // 因此后续 PageLoaderInit 调用的栈状态与泛型实参完全不受影响。
            var body = loaderInit.Body;
            var list = body.Instructions.ToList();

            // 1) 定位假任务表达式起点：最后一个 ldstr "关闭下载"（或含限制文案）
            int start = -1;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].OpCode != OpCodes.Ldstr) continue;
                string s = list[i].Operand as string;
                if (s != null && (s == StubName || s.Contains(Magic))) { start = i; break; }
            }
            if (start < 0) { Console.WriteLine("错误: 未定位到假任务表达式起点"); return 4; }

            // 2) 定位表达式终点：其后第一条构造 LoaderTask`2 的 newobj
            int end = -1;
            for (int i = start; i < list.Count; i++)
            {
                if (list[i].OpCode != OpCodes.Newobj) continue;
                var mr = list[i].Operand as MethodReference;
                if (mr != null && mr.DeclaringType.Name.StartsWith("LoaderTask")) { end = i; break; }
            }
            if (end < 0) { Console.WriteLine("错误: 未定位到假任务表达式终点"); return 4; }

            // 3) 该段内不允许有跳转目标从外部进入（保证可安全删除）
            var deadZone = new HashSet<Instruction>();
            for (int i = start; i <= end; i++) deadZone.Add(list[i]);
            for (int i = 0; i < list.Count; i++)
            {
                if (i >= start && i <= end) continue;
                var op = list[i].Operand;
                if (op is Instruction && deadZone.Contains((Instruction)op))
                {
                    Console.WriteLine("错误: 待删除区间存在外部跳转引用，形态不安全，已中止。");
                    return 4;
                }
            }

            // 4) 执行替换
            var il = body.GetILProcessor();
            foreach (var ins in deadZone) il.Remove(ins);
            il.InsertBefore(list[end + 1], il.Create(OpCodes.Ldsfld, mod.ImportReference(realLoaderField)));
            Console.WriteLine("  [1/4] 下载页原版版本列表 ............ 已恢复");

            // ============================================================
            // 补丁点 2：PageDownloadModify —— 「修改 MC 版本」卡片
            // 源码：CardMinecraft.Visibility = If(RestrictMcDownload, Collapsed, Visible)
            // DCE 后变成常量 ldc.i4.2 (Collapsed)，改回 ldc.i4.0 (Visible)
            // ============================================================
            {
                var t = mod.GetType("PCL.PageDownloadModify");
                bool done = false, already = false;
                if (t != null)
                {
                    foreach (var m in t.Methods.Where(x => x.HasBody).ToList())
                    {
                        var ins = m.Body.Instructions;
                        for (int i = 0; i < ins.Count; i++)
                        {
                            if (!(ins[i].OpCode == OpCodes.Callvirt)) continue;
                            var mr = ins[i].Operand as MethodReference;
                            if (mr == null || mr.Name != "get_CardMinecraft") continue;
                            for (int j = i + 1; j < Math.Min(i + 4, ins.Count); j++)
                            {
                                if (ins[j].OpCode == OpCodes.Ldc_I4_2 &&
                                    j + 1 < ins.Count && ins[j + 1].OpCode == OpCodes.Callvirt &&
                                    ((MethodReference)ins[j + 1].Operand).Name == "set_Visibility")
                                {
                                    ins[j].OpCode = OpCodes.Ldc_I4_0;
                                    ins[j].Operand = null;
                                    done = true;
                                }
                                else if (ins[j].OpCode == OpCodes.Ldc_I4_0)
                                {
                                    already = true;
                                }
                            }
                        }
                    }
                }
                if (done) Console.WriteLine("  [2/4] 修改 MC 版本卡片 .............. 已恢复");
                else if (already) Console.WriteLine("  [2/4] 修改 MC 版本卡片 .............. 已是恢复状态");
                else Console.WriteLine("  [2/4] 修改 MC 版本卡片 .............. 未定位，跳过");
            }

            // ============================================================
            // 补丁点 3：ModpackInstall(String,String,String) —— 主体守卫
            // 源码在“执行对应的安装方法”前插入 Throw。编译后该 throw 落在
            // catch 块出口处：所有正常路径 leave 到这里后立即抛异常，
            // 使整个安装流程必然失败。
            // 修法：删除这 3 条（ldstr/newobj/throw），让正常路径直接落入 finally。
            // ============================================================
            {
                var t = mod.GetType("PCL.ModModpack");
                var m = t == null ? null : t.Methods.FirstOrDefault(x =>
                    x.Name == "ModpackInstall" && x.Parameters.Count == 3 && x.HasBody);
                bool done = false;
                if (m != null)
                {
                    var list3 = m.Body.Instructions.ToList();
                    for (int i = 0; i < list3.Count - 2; i++)
                    {
                        if (list3[i].OpCode != OpCodes.Ldstr) continue;
                        string s = list3[i].Operand as string;
                        if (s == null || (s != StubName && !s.Contains(Magic))) continue;
                        if (list3[i + 1].OpCode != OpCodes.Newobj) continue;
                        if (list3[i + 2].OpCode != OpCodes.Throw) continue;
                        var mr = list3[i + 1].Operand as MethodReference;
                        if (mr == null || mr.DeclaringType.Name != "Exception") continue;

                        // 该 3 条（ldstr/newobj Exception/throw）位于 catch 之后、
                        // 外层 finally 的 try 范围末尾。所有正常路径 leave 到这里。
                        // 不能删除（会让 leave 落入 finally handler，非法），
                        // 也不能改 nop（会让异常块边界失效）。
                        // 正确做法：原地改成「读出方法返回值并返回」——
                        // 等价于正常完成。这里改为 leave 到方法最后一条 ret。
                        Instruction finalRet = null;
                        foreach (var x in list3)
                            if (x.OpCode == OpCodes.Ret) finalRet = x;
                        if (finalRet == null) { Console.WriteLine("  [3/4] 整合包安装主体 ................ 找不到 ret，跳过"); }
                        else
                        {
                            var il3 = m.Body.GetILProcessor();
                            il3.Replace(list3[i], il3.Create(OpCodes.Ldloc_0));   // 装载结果
                            il3.Replace(list3[i + 1], il3.Create(OpCodes.Leave, finalRet));
                            il3.Remove(list3[i + 2]);                              // 去掉 throw
                        }
                        done = true;
                        break;
                    }
                }
                if (done) Console.WriteLine("  [3/4] 整合包安装主体 ................ 已恢复");
                else
                {
                    bool stillBlocked = m != null && m.Body.Instructions.Any(x => x.OpCode == OpCodes.Ldstr
                        && x.Operand is string && (x.Operand as string) == StubName);
                    if (m != null && !stillBlocked) Console.WriteLine("  [3/4] 整合包安装主体 ................ 已是恢复状态");
                    else Console.WriteLine("  [3/4] 整合包安装主体 ................ 未定位，跳过");
                }
            }

            // ============================================================
            // 补丁点 4：ModpackInstall() —— 整合包安装入口
            // 入口被替换成仅弹提示，但其依赖的表达式（SelectFile 的元组数组重载）
            // 在 2.99 里已不再被任何方法引用，无法从本 exe 内重建。
            // 因此本补丁不尝试重建：整合包安装可通过 [下载页 → 资源 → 整合包]
            // 或把压缩包拖入窗口触发（那些入口同样调用本方法，实测需另行处理）。
            // ============================================================
            {
                var t = mod.GetType("PCL.ModModpack");
                var entry = t == null ? null : t.Methods.FirstOrDefault(x =>
                    x.Name == "ModpackInstall" && x.Parameters.Count == 0 && x.HasBody);

                bool already = entry != null && entry.Body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Call && i.Operand is MethodReference &&
                    ((MethodReference)i.Operand).Name == "RunInThread");

                if (entry == null) Console.WriteLine("  [4/4] 整合包安装入口 ................ 未找到，跳过");
                else if (already) Console.WriteLine("  [4/4] 整合包安装入口 ................ 已是恢复状态");
                else
                {
                    // 从本 exe 其它方法里借用一个现存的 SelectFile 引用（签名完全一致）
                    MethodReference selectFile = null, runInThread = null, actionCtor = null;
                    foreach (var tt in mod.GetTypes())
                    {
                        foreach (var mm in tt.Methods)
                        {
                            if (!mm.HasBody) continue;
                            foreach (var ins in mm.Body.Instructions)
                            {
                                var mr = ins.Operand as MethodReference;
                                if (mr == null) continue;
                                if (mr.Name == "SelectFile" && mr.DeclaringType.Name == "Dialogs") selectFile = mr;
                                else if (mr.Name == "RunInThread" && mr.DeclaringType.Name == "ModBase") runInThread = mr;
                            }
                        }
                    }

                    var closureType = t.NestedTypes.FirstOrDefault(x => x.Name.StartsWith("_Closure$__0-0"));
                    var lambda = closureType == null ? null : closureType.Methods.FirstOrDefault(x => x.Name == "_Lambda$__0");
                    var fileField = closureType == null ? null : closureType.Fields.FirstOrDefault(x => x.Name.Contains("VB$Local_File"));
                    var closureCtor = closureType == null ? null : closureType.Methods.FirstOrDefault(x => x.IsConstructor && !x.IsStatic);

                    if (selectFile == null || runInThread == null || lambda == null ||
                        fileField == null || closureCtor == null)
                    {
                        Console.WriteLine("  [4/4] 整合包安装入口 ................ 依赖不全，跳过");
                    }
                    else
                    {
                        // 复用「现成的 SelectFile 调用」：本 exe 里有 7 处同签名调用，
                        // 其中 ManuallyImportJava 用的是「字符串过滤器」的简单形态：
                        //   ldstr "java.exe|java.exe"; call OneOf.op_Implicit(string);
                        //   newobj Nullable::.ctor; call SelectFile
                        // 这里采用同一模式，但传空字符串表示不限制过滤器。
                        var nb = new MethodBody(entry);
                        nb.MaxStackSize = 8;
                        var n2 = nb.GetILProcessor();
                        var cv = new VariableDefinition(mod.ImportReference(closureType));
                        nb.Variables.Add(cv);
                        var fileVar = new VariableDefinition(mod.TypeSystem.String);
                        nb.Variables.Add(fileVar);

                        n2.Emit(OpCodes.Newobj, mod.ImportReference(closureCtor));
                        n2.Emit(OpCodes.Stloc, cv);

                        var nullableT = selectFile.Parameters[3].ParameterType as GenericInstanceType;
                        var oneOfT = nullableT == null ? null : nullableT.GenericArguments[0] as GenericInstanceType;
                        if (oneOfT == null) { Console.WriteLine("  [4/4] 整合包安装入口 ................ 类型异常，跳过"); goto SkipFour; }

                        n2.Emit(OpCodes.Ldstr, "选择整合包压缩文件");
                        n2.Emit(OpCodes.Ldc_I4_0);
                        n2.Emit(OpCodes.Ldnull);

                        // op_Implicit(T2) -> OneOf<...>
                        // 关键：参数类型必须是「开放泛型占位 !1」，不能写死成 string，
                        // 否则元数据里找不到匹配的重载（MissingMethodException）。
                        GenericParameter gp = null;
                        try
                        {
                            var oneOfDef = oneOfT.ElementType.Resolve();
                            gp = oneOfDef.GenericParameters[1]; // T2
                        }
                        catch { }
                        if (gp == null) { Console.WriteLine("  [4/4] 整合包安装入口 ................ 泛型占位解析失败，跳过"); goto SkipFour; }

                        // 直接借用 ModJava::ManuallyImportJava 里那条已验证正确的
                        // op_Implicit / Nullable::.ctor / SelectFile 引用，
                        // 它们是同一个模块内的真实对象，元数据天然正确。
                        MethodReference opImp = null, nlCtor = null;
                        {
                            var mj = mod.GetType("PCL.ModJava");
                            var mm = mj == null ? null : mj.Methods.FirstOrDefault(x => x.Name == "ManuallyImportJava" && x.HasBody);
                            if (mm != null)
                            {
                                foreach (var ins in mm.Body.Instructions)
                                {
                                    var mr = ins.Operand as MethodReference;
                                    if (mr == null) continue;
                                    if (mr.Name == "op_Implicit" && opImp == null) opImp = mr;
                                    if (mr.Name == ".ctor" && mr.DeclaringType.Name == "Nullable`1" && nlCtor == null) nlCtor = mr;
                                }
                            }
                        }
                        // SelectFile 的第 4 参数 filter 本身是 Optional，默认值就是 default(Nullable<OneOf<...>>)。
                        // 直接构造 default 值即可，彻底绕开 op_Implicit 的泛型元数据问题。
                        var nlLocal = new VariableDefinition(mod.ImportReference(nullableT));
                        nb.Variables.Add(nlLocal);
                        n2.Emit(OpCodes.Ldloca, nlLocal);
                        n2.Emit(OpCodes.Initobj, mod.ImportReference(nullableT));
                        n2.Emit(OpCodes.Ldloc, nlLocal);

                        // Nullable<OneOf<...>>::.ctor 已经在上面用借来的 nlCtor 完成

                        n2.Emit(OpCodes.Call, mod.ImportReference(selectFile));

                        MethodReference firstOrDefault = null;
                        foreach (var tt in mod.GetTypes())
                        {
                            foreach (var mm in tt.Methods)
                            {
                                if (firstOrDefault != null || !mm.HasBody) continue;
                                foreach (var ins in mm.Body.Instructions)
                                {
                                    var mr = ins.Operand as MethodReference;
                                    if (mr != null && mr.Name == "FirstOrDefault" && mr.Parameters.Count == 1)
                                    { firstOrDefault = mr; break; }
                                }
                            }
                            if (firstOrDefault != null) break;
                        }
                        if (firstOrDefault == null) { Console.WriteLine("  [4/4] 整合包安装入口 ................ 缺 FirstOrDefault，跳过"); goto SkipFour; }

                        n2.Emit(OpCodes.Call, mod.ImportReference(firstOrDefault));
                        n2.Emit(OpCodes.Stloc, fileVar);

                        n2.Emit(OpCodes.Ldloc, cv);
                        n2.Emit(OpCodes.Ldloc, fileVar);
                        n2.Emit(OpCodes.Stfld, mod.ImportReference(fileField));

                        var skip = n2.Create(OpCodes.Ret);

                        MethodReference isEmpty = null;
                        foreach (var tt in mod.GetTypes())
                        {
                            foreach (var mm in tt.Methods)
                            {
                                if (isEmpty != null || !mm.HasBody) continue;
                                foreach (var ins in mm.Body.Instructions)
                                {
                                    var mr = ins.Operand as MethodReference;
                                    if (mr != null && mr.Name == "IsNullOrEmpty" && mr.DeclaringType.Name == "String")
                                    { isEmpty = mr; break; }
                                }
                            }
                            if (isEmpty != null) break;
                        }
                        if (isEmpty == null) { Console.WriteLine("  [4/4] 整合包安装入口 ................ 缺 IsNullOrEmpty，跳过"); goto SkipFour; }

                        n2.Emit(OpCodes.Ldloc, cv);
                        n2.Emit(OpCodes.Ldfld, mod.ImportReference(fileField));
                        n2.Emit(OpCodes.Call, mod.ImportReference(isEmpty));
                        n2.Emit(OpCodes.Brtrue, skip);

                        n2.Emit(OpCodes.Ldloc, cv);
                        n2.Emit(OpCodes.Ldftn, mod.ImportReference(lambda));
                        TypeReference actionType = null;
                        foreach (var tr in mod.GetTypeReferences()) if (tr.Name == "Action") { actionType = tr; break; }
                        if (actionType == null) { Console.WriteLine("  [4/4] 整合包安装入口 ................ 缺 Action，跳过"); goto SkipFour; }
                        var ac = new MethodReference(".ctor", mod.TypeSystem.Void, actionType) { HasThis = true };
                        ac.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
                        ac.Parameters.Add(new ParameterDefinition(mod.TypeSystem.IntPtr));
                        n2.Emit(OpCodes.Newobj, mod.ImportReference(ac));
                        n2.Emit(OpCodes.Call, mod.ImportReference(runInThread));

                        n2.Append(skip);
                        entry.Body = nb;
                        Console.WriteLine("  [4/4] 整合包安装入口 ................ 已恢复");
                    }
                SkipFour: ;
                }
            }

            // ---------- 写盘 ----------
            asm.Write(tmp);

            // 写盘后再回读校验一次，确保产物可加载
            using (var chk = AssemblyDefinition.ReadAssembly(tmp))
            {
                var li = chk.MainModule.GetType("PCL.PageDownloadInstall")
                                   .Methods.First(m => m.Name == "LoaderInit");
                bool ok = li.Body.Instructions.Any(i =>
                {
                    if (i.OpCode != OpCodes.Ldsfld) return false;
                    var fr = i.Operand as FieldReference;
                    return fr != null && fr.Name == "DlClientListLoader";
                });
                if (!ok) { Console.WriteLine("错误: 产物回读校验失败，已放弃写入。"); File.Delete(tmp); return 5; }
            }

            if (File.Exists(dst) && !inplace) File.Delete(dst);
            if (inplace)
            {
                // 原子替换：先备份
                string bak = dst + ".bak";
                if (File.Exists(dst) && !File.Exists(bak)) File.Copy(dst, bak, true);
                File.Delete(dst);
            }
            File.Move(tmp, dst);

            Console.WriteLine("补丁应用成功: " + dst);
            if (inplace) Console.WriteLine("原文件备份于: " + dst + ".bak");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("补丁失败（目标未改动）: " + ex.Message);
            if (Environment.GetEnvironmentVariable("PCLPATCH_TRACE") == "1")
                Console.WriteLine(ex.ToString());
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch {}
            return 9;
        }
    }

    // PCL2 把 PCLCS / MeloongCore 等依赖以“内嵌资源”形式打包进 exe，
    // 磁盘上并不存在对应 DLL，因此需要自定义解析器从资源流里取。
    class EmbeddedResolver : IAssemblyResolver
    {
        readonly string exePath;
        readonly string baseDir;
        readonly string depsDir;
        readonly Dictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>();
        readonly Dictionary<string, byte[]> embedded = new Dictionary<string, byte[]>();
        bool scanned;

        public EmbeddedResolver(string path)
        {
            exePath = path;
            baseDir = Path.GetDirectoryName(Path.GetFullPath(path));
            depsDir = Path.Combine(baseDir, ".pclpatch-deps");
        }

        void Scan()
        {
            if (scanned) return;
            scanned = true;

            // PCL2 把依赖 DLL 作为资源项打进 WPF 资源包里。
            // 形如 PCL.Resources.resources 里的若干条目，值是 DLL 的字节流。
            // 用 .NET 自带的 ResourceReader 解析。
            try
            {
                using (var raw = AssemblyDefinition.ReadAssembly(exePath,
                    new ReaderParameters { ReadingMode = ReadingMode.Immediate }))
                {
                    foreach (var res in raw.MainModule.Resources)
                    {
                        var er = res as EmbeddedResource;
                        if (er == null) continue;
                        if (!er.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) continue;

                        using (var s = er.GetResourceStream())
                        {
                            try
                            {
                                using (var rr = new System.Resources.ResourceReader(s))
                                {
                                    var e = rr.GetEnumerator();
                                    while (e.MoveNext())
                                    {
                                        string key = e.Key as string;
                                        if (key == null) continue;
                                        byte[] bytes = TryGetBytes(e.Value);
                                        if (bytes == null) continue;
                                        if (!LooksLikeAssembly(bytes)) continue;
                                        string nm = Path.GetFileNameWithoutExtension(key);
                                        if (!embedded.ContainsKey(nm)) embedded[nm] = bytes;
                                    }
                                }
                            }
                            catch { /* 非 ResourceReader 格式，跳过 */ }
                        }
                    }
                }
            }
            catch { /* 扫描失败不致命，后续走磁盘/框架回退 */ }

            // 兜底：若仍有缺失，按 PE 头直接在资源二进制里抠出程序集
            if (embedded.Count < 5)
            {
                try { ScanBytes(); } catch { }
            }
        }

        static byte[] TryGetBytes(object value)
        {
            if (value == null) return null;
            var b = value as byte[];
            if (b != null) return b;
            var ms = value as MemoryStream;
            if (ms != null) return ms.ToArray();
            var s = value as Stream;
            if (s != null) { using (var o = new MemoryStream()) { s.CopyTo(o); return o.ToArray(); } }
            return null;
        }

        // 兜底：直接在字节流里按 PE 头定位内嵌程序集
        void ScanBytes()
        {
            byte[] all;
            using (var raw = AssemblyDefinition.ReadAssembly(exePath,
                new ReaderParameters { ReadingMode = ReadingMode.Immediate }))
            {
                foreach (var res in raw.MainModule.Resources)
                {
                    var er = res as EmbeddedResource;
                    if (er == null) continue;
                    if (!er.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) continue;
                    using (var s = er.GetResourceStream())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        all = ms.ToArray();
                    }
                    Harvest(all);
                }
            }
        }

        // 在 .resources 二进制里搜索 "MZ...PE" 段，按内嵌元数据中的 Assembly 名登记
        void Harvest(byte[] buf)
        {
            for (int i = 0; i + 0x40 < buf.Length; i++)
            {
                if (buf[i] != (byte)'M' || buf[i + 1] != (byte)'Z') continue;
                int pe = BitConverter.ToInt32(buf, i + 0x3C);
                if (pe <= 0 || i + pe + 0x18 >= buf.Length) continue;
                int p = i + pe;
                if (buf[p] != (byte)'P' || buf[p + 1] != (byte)'E') continue;

                // 从整个剩余缓冲里找该程序集名：先尝试用 Cecil 直接解析
                try
                {
                    var ms = new MemoryStream(buf, i, buf.Length - i);
                    var rp = new ReaderParameters { ReadingMode = ReadingMode.Immediate };
                    rp.AssemblyResolver = this;
                    var a = AssemblyDefinition.ReadAssembly(ms, rp);
                    string nm = a.Name.Name;
                    if (!embedded.ContainsKey(nm)) embedded[nm] = Slice(buf, i, a);
                    a.Dispose();
                }
                catch { }
            }
        }

        static byte[] Slice(byte[] buf, int start, AssemblyDefinition a)
        {
            // 简单起见：截到缓冲区末尾；Cecil 只读取需要的部分
            var outb = new byte[buf.Length - start];
            Buffer.BlockCopy(buf, start, outb, 0, outb.Length);
            return outb;
        }

        static bool LooksLikeAssembly(byte[] b)
        {
            if (b == null || b.Length < 0x40) return false;
            if (b[0] != (byte)'M' || b[1] != (byte)'Z') return false;
            try
            {
                int pe = BitConverter.ToInt32(b, 0x3C);
                if (pe <= 0 || pe + 4 >= b.Length) return false;
                return b[pe] == (byte)'P' && b[pe + 1] == (byte)'E';
            }
            catch { return false; }
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return Resolve(name, new ReaderParameters());
        }

        // 把目标 exe 内嵌资源里的依赖 DLL 解到临时目录，供 Cecil 解析。
        // 这样补丁器自身不携带、也不分发任何 PCL 的二进制文件。
        public void ExtractDependencies()
        {
            if (Directory.Exists(depsDir)) return;
            try
            {
                Directory.CreateDirectory(depsDir);
                using (var raw = AssemblyDefinition.ReadAssembly(exePath,
                    new ReaderParameters { ReadingMode = ReadingMode.Immediate }))
                {
                    foreach (var res in raw.MainModule.Resources)
                    {
                        var er = res as EmbeddedResource;
                        if (er == null) continue;
                        if (!er.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) continue;

                        using (var s = er.GetResourceStream())
                        using (var rr = new System.Resources.ResourceReader(s))
                        {
                            var e = rr.GetEnumerator();
                            while (e.MoveNext())
                            {
                                string key = e.Key as string;
                                if (key == null) continue;
                                byte[] bytes = TryGetBytes(e.Value);
                                if (!LooksLikeAssembly(bytes)) continue;
                                string outPath = Path.Combine(depsDir, key + ".dll");
                                if (!File.Exists(outPath)) File.WriteAllBytes(outPath, bytes);
                            }
                        }
                    }
                }
            }
            catch { /* 解出失败不致命，解析时仍会尝试框架目录 */ }
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (cache.ContainsKey(name.Name)) return cache[name.Name];

            // 1) 同目录
            // 2) .pclpatch-deps（刚从目标 exe 解出的依赖）
            // 3) 上级目录
            var probe = new List<string>
            {
                baseDir,
                depsDir,
                Path.Combine(baseDir, ".."),
            };

            foreach (var dir in probe)
            {
                if (!Directory.Exists(dir)) continue;
                string f = Path.Combine(dir, name.Name + ".dll");
                if (!File.Exists(f)) continue;
                var rpl = new ReaderParameters { ReadingMode = ReadingMode.Immediate };
                rpl.AssemblyResolver = this;
                var a = AssemblyDefinition.ReadAssembly(f, rpl);
                cache[name.Name] = a;
                return a;
            }

            // 4) 框架目录
            string fx = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), name.Name + ".dll");
            if (File.Exists(fx))
            {
                var rpf = new ReaderParameters { ReadingMode = ReadingMode.Immediate };
                rpf.AssemblyResolver = this;
                var a = AssemblyDefinition.ReadAssembly(fx, rpf);
                cache[name.Name] = a;
                return a;
            }

            throw new AssemblyResolutionException(name);
        }

        public void Dispose()
        {
            foreach (var a in cache.Values) { try { a.Dispose(); } catch {} }
            cache.Clear();
        }
    }

    // 取属性定义，缺失则抛错
    static PropertyDefinition Prop(TypeDefinition t, string name)
    {
        var p = t.Properties.FirstOrDefault(x => x.Name == name);
        if (p == null) throw new Exception("找不到属性 " + name);
        return p;
    }

    // 找到 Action`1<TLoader> 的 .ctor(对象, IntPtr)
    static MethodReference FindActionCtor(ModuleDefinition mod, MethodReference target)    {
        TypeReference actionType = null;
        foreach (var r in mod.GetTypeReferences())
        {
            if (r.Name == "Action`1") { actionType = r; break; }
        }
        if (actionType == null) throw new Exception("找不到 Action`1 引用");

        return new MethodReference(".ctor", mod.TypeSystem.Void, actionType)
        {
            HasThis = true,
            Parameters =
            {
                new ParameterDefinition(mod.TypeSystem.Object),
                new ParameterDefinition(mod.TypeSystem.IntPtr),
            }
        };
    }
}
