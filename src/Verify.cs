using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// 校验器：检查目标 exe 的“MC 下载相关能力”状态
// 退出码 0 = 全部已恢复（绿），1 = 仍有未恢复项（红），2 = 无法识别
class Verify
{
    const string Magic = "\u4e3a\u9075\u5b88\u76f8\u5173\u6cd5\u5f8b\u6cd5\u89c4";
    const string StubName = "\u5173\u95ed\u4e0b\u8f7d";

    static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string path = args[0];
        var asm = AssemblyDefinition.ReadAssembly(path);
        var mod = asm.MainModule;

        Console.WriteLine("目标: " + path);
        Console.WriteLine();

        int fail = 0;
        int unknown = 0;

        // ---------- 检查点 1：下载页勾选原版版本列表 ----------
        {
            var t = mod.GetType("PCL.PageDownloadInstall");
            var m = t == null ? null : t.Methods.FirstOrDefault(x => x.Name == "LoaderInit" && x.HasBody);
            if (m == null) { Console.WriteLine("  [??] LoaderInit 未找到"); unknown++; }
            else
            {
                bool real = m.Body.Instructions.Any(i => IsField(i, OpCodes.Ldsfld, "DlClientListLoader"));
                bool stub = m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
                             && i.Operand is string && (i.Operand as string) == StubName);
                if (real && !stub) Console.WriteLine("  [OK] 原版版本列表加载");
                else { Console.WriteLine("  [!!] 原版版本列表加载 —— 仍被禁用"); fail++; }
            }
        }

        // ---------- 检查点 2：整合包安装入口 ----------
        {
            var t = mod.GetType("PCL.ModModpack");
            var m = t == null ? null : t.Methods.FirstOrDefault(x => x.Name == "ModpackInstall"
                        && x.Parameters.Count == 0 && x.HasBody);
            if (m == null) { Console.WriteLine("  [??] ModpackInstall() 未找到"); unknown++; }
            else
            {
                // 恢复后的形态应包含对 3 参数重载的引用（通过 lambda）或文件选择逻辑
                bool hasSelector = m.Body.Instructions.Any(i => IsMethod(i, OpCodes.Call, "SelectFile"));
                bool hasThread = m.Body.Instructions.Any(i => IsMethod(i, OpCodes.Call, "RunInThread"));
                if (hasSelector && hasThread) Console.WriteLine("  [OK] 整合包安装入口");
                else { Console.WriteLine("  [!!] 整合包安装入口 —— 仍被拦截"); fail++; }
            }
        }

        // ---------- 检查点 3：整合包安装主体守卫 ----------
        {
            var t = mod.GetType("PCL.ModModpack");
            var m = t == null ? null : t.Methods.FirstOrDefault(x => x.Name == "ModpackInstall"
                        && x.Parameters.Count == 3 && x.HasBody);
            if (m == null) { Console.WriteLine("  [??] ModpackInstall(String,String,String) 未找到"); unknown++; }
            else
            {
                // 守卫文案形如「为遵守相关法律法规，PCL 不再提供 MC 下载功能……」
                // （2.99.0.0 为「暂时不再提供」，2.99.0.1 去掉了「暂时」）
                bool hasGuard = m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
                                && i.Operand is string && ((string)i.Operand).Contains(Magic));
                if (!hasGuard) Console.WriteLine("  [OK] 整合包安装主体");
                else { Console.WriteLine("  [!!] 整合包安装主体 —— 仍被拦截"); fail++; }
            }
        }

        // ---------- 检查点 4：版本设置「修改 MC 版本」卡片 ----------
        {
            var t = mod.GetType("PCL.PageDownloadModify");
            if (t == null) { Console.WriteLine("  [??] PageDownloadModify 未找到"); unknown++; }
            else
            {
                // 找到 get_CardMinecraft 之后的 set_Visibility，看其常量是否为 Collapsed(2)
                bool found = false, collapsed = false;
                foreach (var m in t.Methods.Where(x => x.HasBody))
                {
                    var ins = m.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        if (!IsMethod(ins[i], OpCodes.Callvirt, "get_CardMinecraft")) continue;
                        for (int j = i + 1; j < Math.Min(i + 4, ins.Count); j++)
                        {
                            if (ins[j].OpCode == OpCodes.Ldc_I4_2) { found = true; collapsed = true; }
                            if (ins[j].OpCode == OpCodes.Ldc_I4_0 || ins[j].OpCode == OpCodes.Ldc_I4_1)
                            { found = true; collapsed = false; }
                        }
                    }
                }
                if (!found) { Console.WriteLine("  [??] 卡片的 Visibility 赋值未找到"); unknown++; }
                else if (!collapsed) Console.WriteLine("  [OK] 修改 MC 版本卡片可见");
                else { Console.WriteLine("  [!!] 修改 MC 版本卡片 —— 仍被隐藏"); fail++; }
            }
        }

        // ---------- 检查点 5：资源页整合包「安装」按钮 ----------
        {
            var t = mod.GetType("PCL.PageDownloadResourceDetail");
            var m = t == null ? null : t.Methods.FirstOrDefault(x =>
                x.Name == "InitializeStackResourceVersions" && x.HasBody);
            if (m == null) { Console.WriteLine("  [??] InitializeStackResourceVersions 未找到"); unknown++; }
            else
            {
                bool hasInstall = m.Body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Ldftn && i.Operand is MethodReference &&
                    ((MethodReference)i.Operand).Name == "Install_Click");
                if (hasInstall) Console.WriteLine("  [OK] 资源页安装按钮");
                else { Console.WriteLine("  [!!] 资源页安装按钮 —— 仍只提供保存"); fail++; }
            }
        }

        // ---------- 检查点 6：下载页「原版游戏」入口（2.99.0.1 新增） ----------
        {
            var t = mod.GetType("PCL.PageDownloadLeft");
            var ctor = t == null ? null : t.Methods.FirstOrDefault(x => x.Name == ".ctor" && x.HasBody);
            if (ctor == null) { Console.WriteLine("  [??] PageDownloadLeft..ctor 未找到"); unknown++; }
            else
            {
                bool hasCollapsed = ctor.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_2);
                if (!hasCollapsed) Console.WriteLine("  [OK] 下载页「原版游戏」入口");
                else { Console.WriteLine("  [!!] 下载页「原版游戏」入口 —— 仍被隐藏"); fail++; }
            }
        }

        Console.WriteLine();
        if (fail == 0 && unknown == 0)
        {
            Console.WriteLine("结论: PASS —— 全部功能已恢复");
            return 0;
        }
        if (fail > 0)
        {
            Console.WriteLine("结论: FAIL —— " + fail + " 项仍被禁用");
            return 1;
        }
        Console.WriteLine("结论: 无法完整判定（" + unknown + " 项未识别）");
        return 2;
    }

    static bool IsField(Instruction i, OpCode op, string name)
    {
        if (i.OpCode != op) return false;
        var fr = i.Operand as FieldReference;
        return fr != null && fr.Name == name;
    }

    static bool IsMethod(Instruction i, OpCode op, string name)
    {
        if (i.OpCode != op) return false;
        var mr = i.Operand as MethodReference;
        return mr != null && mr.Name == name;
    }
}
