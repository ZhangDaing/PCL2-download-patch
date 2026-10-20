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
            var resolver = new EmbeddedResolver(src);
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

            // ---------- 写盘 ----------
            asm.Write(tmp);
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
        readonly Dictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>();
        readonly Dictionary<string, byte[]> embedded = new Dictionary<string, byte[]>();
        bool scanned;

        public EmbeddedResolver(string path)
        {
            exePath = path;
            baseDir = Path.GetDirectoryName(Path.GetFullPath(path));
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

        public AssemblyDefinition Resolve(AssemblyNameReference name) { return Resolve(name, new ReaderParameters()); }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (cache.ContainsKey(name.Name)) return cache[name.Name];

            // 1) 同目录
            // 2) 同目录 deps 子目录（预先从资源包解出的依赖）
            // 3) 程序目录
            var probe = new List<string>
            {
                baseDir,
                Path.Combine(baseDir, "deps"),
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
