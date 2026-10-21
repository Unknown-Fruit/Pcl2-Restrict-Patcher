using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PclPatch
{
    /// <summary>
    /// 整合包安装（ModModpack.ModpackInstall）的限制修复。
    ///
    /// 被限制的版本里：
    ///   * 无参重载 —— 方法体被替换成「只弹提示」；
    ///   * 三参重载 —— 解析完整合包类型后直接 throw，后面的 Select Case 分发因不可达被编译器删除。
    ///
    /// 这里按上游原始实现补回：
    ///   无参：选文件 → 调三参重载
    ///   三参：PackType → CurseForge(0) / HMCL(1) / MMC(2) / MCBBS(3) / Modrinth(4) / 启动器压缩包(9) / 其他=压缩包
    ///
    /// 注意：三参重载的「返回」必须在 try 里用 leave 跳出（方法有 finally 释放 ZipArchive），
    /// 且这个 leave 必须落在原来 throw 的位置上——放在被其他 leave 跳转的分支目标处，CLR 会判为非法 IL。
    /// </summary>
    internal static class ModpackPatch
    {
        private const string TypeName = "PCL.ModModpack";

        public static int Apply(ModuleDefinition module, string msgKey, Action<string> log)
        {
            var type = module.GetType(TypeName);
            if (type == null)
            {
                return 0;
            }

            int done = 0;

            var m3 = type.Methods.FirstOrDefault(m =>
                m.Name == "ModpackInstall" && m.Parameters.Count == 3 && m.HasBody);
            if (m3 != null && ContainsMessage(m3.Body, msgKey) && RebuildDispatch(m3, log))
            {
                done++;
            }

            var m0 = type.Methods.FirstOrDefault(m =>
                m.Name == "ModpackInstall" && m.Parameters.Count == 0 && m.HasBody);
            if (m0 != null && ContainsMessage(m0.Body, msgKey) && RebuildNoArg(module, m0, log))
            {
                done++;
            }

            return done;
        }

        private static bool ContainsMessage(MethodBody body, string msgKey)
        {
            return body.Instructions.Any(i =>
                i.OpCode == OpCodes.Ldstr && (i.Operand as string) != null &&
                ((string)i.Operand).Contains(msgKey));
        }

        // ---------------- 三参重载：补回 Select Case 分发 ----------------

        private static bool RebuildDispatch(MethodDefinition method, Action<string> log)
        {
            var body = method.Body;
            var ins = body.Instructions;
            var il = body.GetILProcessor();

            int site = -1;
            for (int i = 0; i + 2 < ins.Count; i++)
            {
                if (ins[i].OpCode == OpCodes.Ldstr && ((ins[i].Operand as string) ?? "").Contains("不再提供") &&
                    ins[i + 1].OpCode == OpCodes.Newobj && ins[i + 2].OpCode == OpCodes.Throw)
                {
                    site = i;
                    break;
                }
            }
            if (site < 0)
            {
                return false;
            }

            var resultLocal = FindLocal(body, "PCL.ModLoader/LoaderCombo`1<System.String>");
            var archiveLocal = FindLocal(body, "System.IO.Compression.ZipArchive");
            var packTypeLocal = FindInt32Local(body);
            var baseFolderLocal = FindEmptyStringLocal(body);

            if (resultLocal == null || archiveLocal == null || packTypeLocal == null || baseFolderLocal == null)
            {
                log("[跳过] ModpackInstall(File, InstanceName, Logo)：找不到所需局部变量，结构不符");
                return false;
            }

            var t = method.DeclaringType;
            var mCurseForge = Find(t, "InstallPackCurseForge", 5);
            var mHmcl = Find(t, "InstallPackHMCL", 3);
            var mMmc = Find(t, "InstallPackMMC", 3);
            var mMcbbs = Find(t, "InstallPackMCBBS", 4);
            var mModrinth = Find(t, "InstallPackModrinth", 5);
            var mLauncherPack = Find(t, "InstallPackLauncherPack", 3);
            var mCompress = Find(t, "InstallPackCompress", 2);

            if (mCurseForge == null || mHmcl == null || mMmc == null || mMcbbs == null ||
                mModrinth == null || mLauncherPack == null || mCompress == null)
            {
                log("[跳过] ModpackInstall(File, InstanceName, Logo)：缺少 InstallPack* 方法，结构不符");
                return false;
            }

            // 退出块（方法末尾、所有异常处理区之外）
            var exitLoad = Instruction.Create(OpCodes.Ldloc, resultLocal);
            var openTryEnd = body.ExceptionHandlers.Where(h => h.TryEnd == null).ToList();
            var openHandlerEnd = body.ExceptionHandlers.Where(h => h.HandlerEnd == null).ToList();

            // 最后那条 leave：放在原 throw 的位置上
            var leaveInstr = ins[site + 2];
            var newObjInstr = ins[site + 1];

            // 各分支体：结尾统一 br 到 leaveInstr
            var lists = new SortedDictionary<int, List<Instruction>>
            {
                { 0, CaseBody(mCurseForge, true, true, resultLocal, archiveLocal, baseFolderLocal, leaveInstr) },
                { 1, CaseBody(mHmcl, false, false, resultLocal, archiveLocal, baseFolderLocal, leaveInstr) },
                { 2, CaseBody(mMmc, false, false, resultLocal, archiveLocal, baseFolderLocal, leaveInstr) },
                { 3, CaseBody(mMcbbs, true, false, resultLocal, archiveLocal, baseFolderLocal, leaveInstr) },
                { 4, CaseBody(mModrinth, true, true, resultLocal, archiveLocal, baseFolderLocal, leaveInstr) },
                { 9, CaseBody(mLauncherPack, false, false, resultLocal, archiveLocal, baseFolderLocal, leaveInstr) }
            };
            var defBody = CaseBody(mCompress, false, false, resultLocal, archiveLocal, baseFolderLocal, leaveInstr);
            var defLabel = defBody[0];

            // 现场改造：ldstr → ldloc PackType；其后插入 switch + 各分支
            var siteInstr = ins[site];
            siteInstr.OpCode = OpCodes.Ldloc;
            siteInstr.Operand = packTypeLocal;

            // 用 if 链代替 switch（更稳，避免某些实现下 switch 目标数组的处理差异）
            var prelude = new List<Instruction>();
            foreach (var kv in lists)
            {
                prelude.Add(Instruction.Create(OpCodes.Ldloc, packTypeLocal));
                prelude.Add(Instruction.Create(OpCodes.Ldc_I4, kv.Key));
                prelude.Add(Instruction.Create(OpCodes.Beq, kv.Value[0]));
            }

            var anchor = siteInstr;
            foreach (var i in prelude)
            {
                il.InsertAfter(anchor, i);
                anchor = i;
            }
            foreach (var i in defBody)
            {
                il.InsertAfter(anchor, i);
                anchor = i;
            }
            foreach (var kv in lists)
            {
                foreach (var i in kv.Value)
                {
                    il.InsertAfter(anchor, i);
                    anchor = i;
                }
            }

            // 原来的 newobj 没用，抹掉；原来的 throw 改成 leave（跳出 try，触发 finally）
            newObjInstr.OpCode = OpCodes.Nop;
            newObjInstr.Operand = null;
            leaveInstr.OpCode = OpCodes.Leave;
            leaveInstr.Operand = exitLoad;

            // 计算所需栈深（case0 最多压 5 个参数），确保 maxstack 足够
            if (body.MaxStackSize < 6) body.MaxStackSize = 6;

            // 追加退出块并修正开放边界
            il.Append(exitLoad);
            il.Append(Instruction.Create(OpCodes.Ret));
            foreach (var h in openTryEnd) h.TryEnd = exitLoad;
            foreach (var h in openHandlerEnd) h.HandlerEnd = exitLoad;

            Console.WriteLine("  [已改] ModModpack.ModpackInstall(File, InstanceName, Logo)：补回整合包类型分发" +
                              "（CurseForge/HMCL/MMC/MCBBS/Modrinth/启动器压缩包/压缩包）");
            return true;
        }

        private static List<Instruction> CaseBody(
            MethodReference target, bool needName, bool needLogo,
            VariableDefinition result, VariableDefinition archive, VariableDefinition baseFolder,
            Instruction leave)
        {
            // 按目标方法的实际参数个数压栈：File, Archive, ArchiveBaseFolder, InstanceName, Logo
            int want = target.Parameters.Count;
            var list = new List<Instruction> { Instruction.Create(OpCodes.Ldarg_0) };
            if (want >= 2) list.Add(Instruction.Create(OpCodes.Ldloc, archive));
            if (want >= 3) list.Add(Instruction.Create(OpCodes.Ldloc, baseFolder));
            if (want >= 4) list.Add(Instruction.Create(OpCodes.Ldarg_1));
            if (want >= 5) list.Add(Instruction.Create(OpCodes.Ldarg_2));
            list.Add(Instruction.Create(OpCodes.Call, target));
            list.Add(Instruction.Create(OpCodes.Stloc, result));
            list.Add(Instruction.Create(OpCodes.Br, leave));
            return list;
        }

        // ---------------- 无参重载：补回「选文件 → 安装」 ----------------

        private static bool RebuildNoArg(ModuleDefinition module, MethodDefinition method, Action<string> log)
        {
            var selectFile = FindMethodRef(module, "Dialogs", "SelectFile", 4);
            var firstOrDefault = FindMethodRef(module, "Enumerable", "FirstOrDefault", 1);
            var isNullOrEmpty = FindMethodRef(module, "String", "IsNullOrEmpty", 1);
            var m3 = method.DeclaringType.Methods.FirstOrDefault(m =>
                m.Name == "ModpackInstall" && m.Parameters.Count == 3);

            if (selectFile == null || firstOrDefault == null || isNullOrEmpty == null || m3 == null)
            {
                log("[跳过] ModpackInstall()：缺少 SelectFile / FirstOrDefault 引用");
                return false;
            }

            var body = method.Body;
            var il = body.GetILProcessor();
            var fileLocal = new VariableDefinition(module.TypeSystem.String);
            body.Variables.Add(fileLocal);

            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();

            var afterCheck = Instruction.Create(OpCodes.Ldloc, fileLocal);
            il.Append(Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Ldnull));
            il.Append(Instruction.Create(OpCodes.Ldnull));
            il.Append(Instruction.Create(OpCodes.Call, selectFile));
            il.Append(Instruction.Create(OpCodes.Call, firstOrDefault));
            il.Append(Instruction.Create(OpCodes.Stloc, fileLocal));
            il.Append(Instruction.Create(OpCodes.Ldloc, fileLocal));
            il.Append(Instruction.Create(OpCodes.Call, isNullOrEmpty));
            il.Append(Instruction.Create(OpCodes.Brfalse, afterCheck));
            il.Append(Instruction.Create(OpCodes.Ret));
            il.Append(afterCheck);
            il.Append(Instruction.Create(OpCodes.Ldnull));
            il.Append(Instruction.Create(OpCodes.Ldnull));
            il.Append(Instruction.Create(OpCodes.Call, m3));
            il.Append(Instruction.Create(OpCodes.Pop));
            il.Append(Instruction.Create(OpCodes.Ret));

            Console.WriteLine("  [已改] ModModpack.ModpackInstall()：补回「选择整合包文件 → 安装」");
            return true;
        }

        // ---------------- 工具方法 ----------------

        private static MethodDefinition Find(TypeDefinition type, string name, int paramCount)
        {
            return type.Methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == paramCount);
        }

        private static VariableDefinition FindLocal(MethodBody body, string typeFullName)
        {
            foreach (var v in body.Variables)
            {
                var t = v.VariableType;
                var gi = t as GenericInstanceType;
                if (gi != null && gi.FullName == typeFullName) return v;
                if (t.FullName == typeFullName) return v;
            }
            return null;
        }

        private static VariableDefinition FindInt32Local(MethodBody body)
        {
            return body.Variables.FirstOrDefault(v => v.VariableType.MetadataType == MetadataType.Int32);
        }

        private static VariableDefinition FindEmptyStringLocal(MethodBody body)
        {
            var ins = body.Instructions;
            for (int i = 0; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldstr || ((string)ins[i].Operand) != "") continue;
                var next = ins[i + 1];
                var v = next.Operand as VariableDefinition;
                if (v != null) return v;
                int idx = next.OpCode == OpCodes.Stloc_0 ? 0 : next.OpCode == OpCodes.Stloc_1 ? 1 :
                          next.OpCode == OpCodes.Stloc_2 ? 2 : next.OpCode == OpCodes.Stloc_3 ? 3 : -1;
                if (idx >= 0 && idx < body.Variables.Count) return body.Variables[idx];
            }
            return null;
        }

        /// <summary>在模块里借用一个已有调用的方法引用，避免手工拼泛型签名。</summary>
        private static MethodReference FindMethodRef(ModuleDefinition module, string typeNamePart, string methodName, int paramCount)
        {
            foreach (var type in module.Types.SelectMany(AllTypes))
            {
                foreach (var m in type.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var i in m.Body.Instructions)
                    {
                        if (i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt) continue;
                        var mr = i.Operand as MethodReference;
                        if (mr == null || mr.Name != methodName || mr.Parameters.Count != paramCount) continue;
                        var dt = mr.DeclaringType;
                        var gi = dt as GenericInstanceType;
                        if (gi != null) dt = gi.ElementType;
                        if (dt.Name == typeNamePart || dt.FullName.EndsWith("." + typeNamePart))
                        {
                            return module.ImportReference(mr);
                        }
                    }
                }
            }
            return null;
        }

        private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition t)
        {
            yield return t;
            foreach (var n in t.NestedTypes)
            foreach (var x in AllTypes(n))
                yield return x;
        }
    }
}
