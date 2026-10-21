using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PclPatch
{
    /// <summary>
    /// PCL 下载限制补丁工具。
    /// 只对本地已有的 PCL 可执行文件做 IL 层面的最小改动，不附带、不分发任何 PCL 本体。
    /// </summary>
    internal static class Program
    {
        private const string MsgKey = "不再提供 MC 下载功能";     // 限制提示语特征片段
        private const string DummyLoaderName = "关闭下载";        // 被限制版本里的「假加载器」名字
        private const string LoaderTaskType = "PCL.ModLoader/LoaderTask`2";
        private const string RealLoaderType = "PCL.ModDownload";
        private const string RealLoaderField = "DlClientListLoader";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            int rc = Run(args);
            if (args.Length > 0 && !Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine("按任意键退出…");
                try { Console.ReadKey(true); } catch { }
            }
            return rc;
        }

        private static int Run(string[] args)
        {

            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            // 诊断用：--dump <文件> <方法名>  打印所有同名方法的 IL 与异常处理区
            if (args[0] == "--dump" && args.Length >= 3)
            {
                return DumpIl(args[1], args[2]);
            }

            string input = args[0];
            string output = null;
            for (int i = 1; i < args.Length; i++)
            {
                if ((args[i] == "-o" || args[i] == "--output") && i + 1 < args.Length)
                {
                    output = args[++i];
                }
            }
            if (output == null)
            {
                output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)),
                    Path.GetFileNameWithoutExtension(input) + ".patched" + Path.GetExtension(input));
            }

            PrintBanner();

            if (!File.Exists(input))
            {
                Console.WriteLine("[错误] 找不到文件：" + input);
                return 2;
            }
            if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[错误] 输出文件不能和输入文件相同（请换一个 -o 路径）。");
                return 2;
            }

            try
            {
                return Patch(input, output);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] " + ex.GetType().Name + "：" + ex.Message);
                return 3;
            }
        }

        private static int DumpIl(string file, string methodName)
        {
            using (var asm = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { InMemory = true, ReadingMode = ReadingMode.Immediate }))
            {
                foreach (var type in asm.MainModule.Types.SelectMany(AllTypes))
                {
                    foreach (var m in type.Methods)
                    {
                        if (m.Name != methodName || !m.HasBody) continue;
                        Console.WriteLine("=== " + type.FullName + "." + m.Name + " (" + m.Parameters.Count + " params) maxstack=" + m.Body.MaxStackSize);
                        for (int vi = 0; vi < m.Body.Variables.Count; vi++)
                        {
                            Console.WriteLine("  local V_" + vi + " : " + m.Body.Variables[vi].VariableType.FullName);
                        }
                        foreach (var h in m.Body.ExceptionHandlers)
                        {
                            Console.WriteLine("  EH " + h.HandlerType + " try[" + Idx(m.Body, h.TryStart) + "," + Idx(m.Body, h.TryEnd) +
                                "] handler[" + Idx(m.Body, h.HandlerStart) + "," + Idx(m.Body, h.HandlerEnd) + "]");
                        }
                        for (int i = 0; i < m.Body.Instructions.Count; i++)
                        {
                            var ins = m.Body.Instructions[i];
                            Console.WriteLine("  " + i.ToString().PadLeft(4) + " @" + ins.Offset.ToString("X3") + ": " + ins.OpCode + " " + FormatOperand(ins, m.Body));
                        }
                    }
                }
            }
            return 0;
        }

        private static string Idx(MethodBody body, Instruction i)
        {
            return i == null ? "end" : body.Instructions.IndexOf(i).ToString();
        }

        private static string FormatOperand(Instruction i, MethodBody body)
        {
            if (i.Operand == null) return "";
            var target = i.Operand as Instruction;
            if (target != null) return "-> " + body.Instructions.IndexOf(target);
            var arr = i.Operand as Instruction[];
            if (arr != null) return "[" + string.Join(",", arr.Select(x => body.Instructions.IndexOf(x).ToString())) + "]";
            return i.Operand.ToString();
        }

        private static int Patch(string input, string output)
        {
            Console.WriteLine("输入：" + Path.GetFullPath(input));

            var resolver = new EmbeddedResourceResolver();
            var reader = new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false,
                ReadingMode = ReadingMode.Immediate,
                AssemblyResolver = resolver
            };

            using (var asm = AssemblyDefinition.ReadAssembly(input, reader))
            {
                Console.WriteLine("程序集：" + asm.Name.Name + " " + asm.Name.Version);

                if (asm.Name.Name != "Plain Craft Launcher 2")
                {
                    Console.WriteLine("[错误] 这不是 PCL 主程序（程序集名应为 Plain Craft Launcher 2）。");
                    return 4;
                }

                var module = asm.MainModule;

                // PCL 把自己的依赖（MeloongCore / PCLCS / Newtonsoft.Json 等）内嵌在资源里，
                // Cecil 写回时需要解析这些类型，这里直接从内嵌资源里取出来喂给它。
                resolver.LoadFromModule(module);

                bool hasConst = module.Types.SelectMany(AllTypes)
                    .Where(t => t.Name == "ModSecret")
                    .SelectMany(t => t.Fields)
                    .Any(f => f.Name == "RestrictMcDownload");
                Console.WriteLine("限制常量 RestrictMcDownload：" + (hasConst ? "存在" : "未找到"));
                Console.WriteLine();

                int fixedLoader = ReplaceDummyLoader(module);
                int fixedModpack = ModpackPatch.Apply(module, MsgKey, m => Console.WriteLine("  " + m.Trim()));
                int fixedThrow = 0, fixedHint = 0, skippedThrow = 0;
                ScanAndPatch(module, ref fixedThrow, ref fixedHint, ref skippedThrow);

                Console.WriteLine();
                Console.WriteLine("结果：加载器替换 " + fixedLoader + " 处，整合包安装修复 " + fixedModpack +
                                  " 处，throw 抹除 " + fixedThrow + " 处，提示移除 " + fixedHint + " 处，跳过 " + skippedThrow + " 处");

                if (fixedLoader == 0 && fixedModpack == 0 && fixedThrow == 0 && fixedHint == 0)
                {
                    Console.WriteLine("[提示] 没有找到可修改的限制点：该文件可能已打过补丁，或该版本结构不同。");
                    resolver.Dispose();
                    return 5;
                }

                asm.Write(output);
                resolver.Dispose();
            }

            using (AssemblyDefinition.ReadAssembly(output, new ReaderParameters { InMemory = true, ReadingMode = ReadingMode.Immediate }))
            {
            }

            Console.WriteLine();
            Console.WriteLine("已输出：" + Path.GetFullPath(output));
            Console.WriteLine("注意：请先关闭正在运行的 PCL，再运行打过补丁的副本。");
            return 0;
        }

        /// <summary>把 LoaderInit 里构造「关闭下载」LoaderTask 的整段 IL 换成 ldsfld DlClientListLoader。</summary>
        private static int ReplaceDummyLoader(ModuleDefinition module)
        {
            var pageType = module.GetType("PCL.PageDownloadInstall");
            var realLoaderType = module.GetType(RealLoaderType);
            var realLoaderField = realLoaderType == null
                ? null
                : realLoaderType.Fields.FirstOrDefault(f => f.Name == RealLoaderField);

            if (pageType == null || realLoaderField == null)
            {
                Console.WriteLine("  [跳过] 未找到 PCL.PageDownloadInstall 或 " + RealLoaderType + "." + RealLoaderField);
                return 0;
            }

            int count = 0;
            foreach (var method in pageType.Methods.Where(m => m.HasBody))
            {
                var instrs = method.Body.Instructions;
                int start = -1, end = -1;
                for (int i = 0; i < instrs.Count && start < 0; i++)
                {
                    if (instrs[i].OpCode != OpCodes.Ldstr || (instrs[i].Operand as string) != DummyLoaderName) continue;
                    for (int j = i + 1; j < instrs.Count && j <= i + 40; j++)
                    {
                        if (instrs[j].OpCode != OpCodes.Newobj) continue;
                        var mr = instrs[j].Operand as MethodReference;
                        if (mr != null && TypeNameOf(mr.DeclaringType) == LoaderTaskType)
                        {
                            start = i;
                            end = j;
                            break;
                        }
                    }
                }
                if (start < 0) continue;

                instrs[start].OpCode = OpCodes.Ldsfld;
                instrs[start].Operand = realLoaderField;
                for (int k = start + 1; k <= end; k++)
                {
                    instrs[k].OpCode = OpCodes.Nop;
                    instrs[k].Operand = null;
                }
                count++;
                Console.WriteLine("  [已改] " + pageType.FullName + "." + method.Name +
                                  "：假加载器 → " + RealLoaderType + "." + RealLoaderField);
            }
            return count;
        }

        private static void ScanAndPatch(ModuleDefinition module, ref int fixedThrow, ref int fixedHint, ref int skippedThrow)
        {
            foreach (var type in module.Types.SelectMany(AllTypes).ToList())
            {
                foreach (var method in type.Methods.ToList())
                {
                    if (!method.HasBody) continue;
                    var body = method.Body;
                    var instrs = body.Instructions;

                    for (int i = 0; i < instrs.Count; i++)
                    {
                        if (instrs[i].OpCode != OpCodes.Ldstr) continue;
                        var s = instrs[i].Operand as string;
                        if (s == null || !s.Contains(MsgKey)) continue;

                        int kind = Classify(instrs, i);
                        if (kind == 1)
                        {
                            if (body.ExceptionHandlers.Count == 0 &&
                                method.ReturnType.MetadataType == MetadataType.Void)
                            {
                                body.Instructions.Clear();
                                body.Variables.Clear();
                                body.ExceptionHandlers.Clear();
                                body.InitLocals = false;
                                body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
                                fixedThrow++;
                                Console.WriteLine("  [已改] " + type.FullName + "." + method.Name +
                                                  "：throw 方法整体改为 ret");
                                break;
                            }

                            skippedThrow++;
                            Console.WriteLine("  [跳过] " + type.FullName + "." + method.Name +
                                              "：throw 位于 try/finally 内，强行改会产生非法 IL");
                        }
                        else if (kind == 2)
                        {
                            int end = FindHintCallEnd(instrs, i);
                            if (end <= i) continue;
                            for (int k = i; k <= end; k++)
                            {
                                instrs[k].OpCode = OpCodes.Nop;
                                instrs[k].Operand = null;
                            }
                            fixedHint++;
                            Console.WriteLine("  [已改] " + type.FullName + "." + method.Name +
                                              "：去掉下载限制提示");
                            i = end;
                        }
                    }
                }
            }
        }

        private static int Classify(IList<Instruction> instrs, int i)
        {
            for (int j = i + 1; j < instrs.Count && j <= i + 3; j++)
            {
                if (instrs[j].OpCode != OpCodes.Newobj) continue;
                var mr = instrs[j].Operand as MethodReference;
                if (mr != null && TypeNameOf(mr.DeclaringType) == "System.Exception" &&
                    mr.Parameters.Count == 1 && j + 1 < instrs.Count && instrs[j + 1].OpCode == OpCodes.Throw)
                {
                    return 1;
                }
            }
            return FindHintCallEnd(instrs, i) > i ? 2 : 0;
        }

        private static int FindHintCallEnd(IList<Instruction> instrs, int i)
        {
            for (int j = i + 1; j < instrs.Count && j <= i + 4; j++)
            {
                var op = instrs[j].OpCode;
                if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    var mr = instrs[j].Operand as MethodReference;
                    return mr != null && mr.Name == "Hint" ? j : -1;
                }
                bool isLdc = op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S || op == OpCodes.Ldc_I4_0 ||
                             op == OpCodes.Ldc_I4_1 || op == OpCodes.Ldc_I4_2 || op == OpCodes.Ldc_I4_3 ||
                             op == OpCodes.Ldc_I4_4 || op == OpCodes.Ldc_I4_5;
                if (!isLdc) return -1;
            }
            return -1;
        }

        private static string TypeNameOf(TypeReference tr)
        {
            var gi = tr as GenericInstanceType;
            if (gi != null) tr = gi.ElementType;
            return tr.FullName;
        }

        private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition t)
        {
            yield return t;
            foreach (var n in t.NestedTypes)
            foreach (var x in AllTypes(n))
                yield return x;
        }

        private static void PrintBanner()
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine(" PCL 下载限制补丁工具 (PclRestrictPatcher) v1.0");
            Console.WriteLine("==========================================================");
            Console.WriteLine(" 用途：修改本机已存在的 PCL 主程序，恢复「下载 → 安装」页面。");
            Console.WriteLine(" 声明：本工具不含、不分发任何 PCL 文件，也不提供任何下载地址；");
            Console.WriteLine("       请自行合法取得程序副本后使用，改动与后果由使用者自负。");
            Console.WriteLine("==========================================================");
            Console.WriteLine();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  PclRestrictPatcher.exe <PCL主程序.exe> [-o 输出文件]");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  PclRestrictPatcher.exe \"Plain Craft Launcher 2.exe\"");
            Console.WriteLine("  PclRestrictPatcher.exe rls.exe -o \"PCL_patched.exe\"");
            Console.WriteLine();
            Console.WriteLine("说明：默认输出为 <原名>.patched.exe，不会覆盖原文件。");
            Console.WriteLine("      需要 .NET Framework 4.8，并与 Mono.Cecil.dll 放在同一目录。");
        }
    }

    /// <summary>
    /// 从 PCL 内嵌的 *.resources 里取出它自己的依赖程序集，供 Cecil 解析使用。
    /// 这样工具无需向用户索取任何 DLL，也不需要这些 DLL 存在于磁盘上。
    /// </summary>
    internal sealed class EmbeddedResourceResolver : IAssemblyResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> _cache =
            new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly List<AssemblyDefinition> _owned = new List<AssemblyDefinition>();

        public void LoadFromModule(ModuleDefinition module)
        {
            _cache[module.Assembly.Name.Name] = module.Assembly;

            foreach (var res in module.Resources)
            {
                var er = res as EmbeddedResource;
                if (er == null) continue;
                if (!er.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    using (var s = er.GetResourceStream())
                    using (var rr = new ResourceReader(s))
                    {
                        foreach (System.Collections.DictionaryEntry e in rr)
                        {
                            var bytes = e.Value as byte[];
                            if (bytes == null || bytes.Length < 2) continue;
                            if (bytes[0] != 0x4D || bytes[1] != 0x5A) continue; // "MZ"
                            try
                            {
                                var sub = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
                                _owned.Add(sub);
                                _cache[sub.Name.Name] = sub;
                            }
                            catch
                            {
                                // 不是托管程序集，忽略
                            }
                        }
                    }
                }
                catch
                {
                    // 资源读不出来就跳过，后续解析失败会有明确报错
                }
            }
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return Resolve(name, new ReaderParameters());
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            AssemblyDefinition hit;
            if (_cache.TryGetValue(name.Name, out hit)) return hit;

            // 1) 同目录（用户把依赖 DLL 放在工具旁边时）
            var dirPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name.Name + ".dll");
            if (File.Exists(dirPath))
            {
                return Cache(name, dirPath);
            }

            // 2) .NET Framework 运行时目录（含 WPF 子目录）
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var runtimePath = Path.Combine(runtimeDir, name.Name + ".dll");
            if (File.Exists(runtimePath))
            {
                return Cache(name, runtimePath);
            }
            var wpfPath = Path.Combine(Path.Combine(runtimeDir, "WPF"), name.Name + ".dll");
            if (File.Exists(wpfPath))
            {
                return Cache(name, wpfPath);
            }

            // 3) 交给当前进程去定位（GAC 里的 System / WPF 等）
            foreach (var id in new[] { name.FullName, name.Name })
            {
                try
                {
                    var loaded = System.Reflection.Assembly.Load(id);
                    if (!string.IsNullOrEmpty(loaded.Location) && File.Exists(loaded.Location))
                    {
                        return Cache(name, loaded.Location);
                    }
                }
                catch
                {
                }
            }

            throw new AssemblyResolutionException(name);
        }

        private AssemblyDefinition Cache(AssemblyNameReference name, string path)
        {
            var a = AssemblyDefinition.ReadAssembly(path);
            _owned.Add(a);
            _cache[name.Name] = a;
            return a;
        }

        public void Dispose()
        {
            foreach (var a in _owned) a.Dispose();
            _owned.Clear();
            _cache.Clear();
        }
    }
}
