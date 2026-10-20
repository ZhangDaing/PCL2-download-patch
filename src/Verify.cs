using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// 校验器：检查目标 exe 的“原版 MC 下载”能力状态
// 退出码 0 = 已恢复（绿），1 = 仍被限制（红），2 = 无法识别
class Verify
{
    const string Magic = "\u4e3a\u9075\u5b88\u76f8\u5173\u6cd5\u5f8b\u6cd5\u89c4";

    static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string path = args[0];
        var asm = AssemblyDefinition.ReadAssembly(path);
        var mod = asm.MainModule;

        var pdi = mod.GetType("PCL.PageDownloadInstall");
        if (pdi == null) { Console.WriteLine("ERROR: 找不到 PCL.PageDownloadInstall"); return 2; }

        var loaderInit = pdi.Methods.FirstOrDefault(m => m.Name == "LoaderInit" && m.HasBody);
        if (loaderInit == null) { Console.WriteLine("ERROR: 找不到 LoaderInit"); return 2; }

        // 判定依据：LoaderInit 是否把 ModDownload.DlClientListLoader 交给 PageLoaderInit
        // 且不再构造那个直接 throw 的假 loader
        bool referencesRealLoader = false;
        bool referencesThrowingStub = false;
        bool constructsFakeLoader = false;

        foreach (var ins in loaderInit.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldsfld)
            {
                var fr = ins.Operand as FieldReference;
                if (fr != null && fr.Name == "DlClientListLoader") referencesRealLoader = true;
            }
            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string)
            {
                string s = (string)ins.Operand;
                if (s.Contains(Magic) || s == "\u5173\u95ed\u4e0b\u8f7d") referencesThrowingStub = true;
            }
            if (ins.OpCode == OpCodes.Newobj)
            {
                var mr = ins.Operand as MethodReference;
                if (mr != null && mr.DeclaringType.Name.StartsWith("LoaderTask")) constructsFakeLoader = true;
            }
        }

        Console.WriteLine("目标: " + path);
        Console.WriteLine("  引用真实 DlClientListLoader : " + referencesRealLoader);
        Console.WriteLine("  含限制文案/假任务名        : " + referencesThrowingStub);
        Console.WriteLine("  构造 LoaderTask 实例        : " + constructsFakeLoader);
        Console.WriteLine();

        bool restored = referencesRealLoader && !referencesThrowingStub && !constructsFakeLoader;

        // 额外确认那个 throw 桩已不可达（方法体只剩 throw）
        var closure = mod.GetType("PCL.PageDownloadInstall/_Closure$__");
        if (closure != null)
        {
            var thunk = closure.Methods.FirstOrDefault(m => m.Name == "_Lambda$__3-0");
            if (thunk != null)
                Console.WriteLine("  抛异常桩 _Lambda$__3-0   : 仍存在（已无引用则为死代码）");
        }

        Console.WriteLine();
        if (restored)
        {
            Console.WriteLine("结论: PASS —— 原版 MC 下载已恢复");
            return 0;
        }
        Console.WriteLine("结论: FAIL —— 原版 MC 下载仍被禁用");
        return 1;
    }
}
