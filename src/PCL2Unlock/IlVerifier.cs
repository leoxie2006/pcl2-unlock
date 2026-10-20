using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Pcl2Unlock;

/// <summary>
/// 生成 IL 之后的轻量自检：沿控制流算栈深度，要求每条 <c>ret</c> 处栈为空。
/// dnlib 写文件时只算 maxstack，不会因为「ret 时栈上多压了一个值」报错，
/// 而这类错误在运行时会变成莫名其妙的异常，所以单独兜一层。
/// </summary>
internal static class IlVerifier
{
    public static void AssertBalanced(CilBody body, string methodName, int expectedReturnDepth)
    {
        var code = body.Instructions;
        var depth = new Dictionary<Instruction, int>();
        var queue = new Queue<Instruction>();

        Instruction Next(Instruction instruction)
        {
            int index = code.IndexOf(instruction);
            return index >= 0 && index + 1 < code.Count ? code[index + 1] : null;
        }

        void Visit(Instruction instruction, int value, string from)
        {
            if (instruction == null)
            {
                return;
            }

            if (value < 0)
            {
                throw new InvalidOperationException($"{methodName}: 在 {from} 之后栈深度变成 {value}");
            }

            if (depth.TryGetValue(instruction, out int known))
            {
                if (known != value)
                {
                    throw new InvalidOperationException(
                        $"{methodName}: {instruction.OpCode} 处栈深度不一致（{known} vs {value}）");
                }

                return;
            }

            depth[instruction] = value;
            queue.Enqueue(instruction);
        }

        Visit(body.Instructions[0], 0, "方法入口");

        while (queue.Count > 0)
        {
            var instruction = queue.Dequeue();
            int current = depth[instruction];
            int after = current + Delta(instruction, methodName);

            switch (instruction.OpCode.Code)
            {
                case Code.Ret:
                    if (after != expectedReturnDepth)
                    {
                        throw new InvalidOperationException(
                            $"{methodName}: 第 {code.IndexOf(instruction) + 1} 条指令（0x{instruction.Offset:X4}）ret 处栈深度为 {after}，应为 {expectedReturnDepth}");
                    }
                    continue;

                case Code.Throw:
                    continue;

                case Code.Leave:
                case Code.Leave_S:
                case Code.Br:
                case Code.Br_S:
                    Visit((Instruction)instruction.Operand, after, instruction.OpCode.Name);
                    continue;

                case Code.Brfalse:
                case Code.Brfalse_S:
                case Code.Brtrue:
                case Code.Brtrue_S:
                    Visit((Instruction)instruction.Operand, after, instruction.OpCode.Name);
                    Visit(Next(instruction), after, instruction.OpCode.Name);
                    continue;

                case Code.Switch:
                    foreach (var target in (Instruction[])instruction.Operand)
                    {
                        Visit(target, after, "switch");
                    }
                    Visit(Next(instruction), after, "switch");
                    continue;
            }

            Visit(Next(instruction), after, instruction.OpCode.Name);
        }
    }

    /// <summary>单条指令对栈深度的影响（弹出为负、压入为正）。</summary>
    private static int Delta(Instruction instruction, string methodName)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Nop:
            case Code.Break:
                return 0;

            case Code.Ldstr:
            case Code.Ldnull:
            case Code.Ldc_I4:
            case Code.Ldc_I4_S:
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_2:
            case Code.Ldc_I4_3:
            case Code.Ldc_I4_4:
            case Code.Ldc_I4_5:
            case Code.Ldc_I4_6:
            case Code.Ldc_I4_7:
            case Code.Ldc_I4_8:
            case Code.Ldc_I4_M1:
            case Code.Ldarg:
            case Code.Ldarg_0:
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
            case Code.Ldloc:
            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            case Code.Ldftn:
            case Code.Dup:
                return 1;

            case Code.Stloc:
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
            case Code.Starg:
            case Code.Pop:
                return -1;

            case Code.Newarr:
                return 0;

            case Code.Stelem_Ref:
            case Code.Stelem:
            case Code.Stelem_I1:
            case Code.Stelem_I2:
            case Code.Stelem_I4:
            case Code.Stelem_I8:
            case Code.Stelem_R4:
            case Code.Stelem_R8:
                return -3;

            case Code.Stfld:
                return -2;

            case Code.Switch:
                return -1;

            case Code.Brfalse:
            case Code.Brfalse_S:
            case Code.Brtrue:
            case Code.Brtrue_S:
                return -1;

            case Code.Br:
            case Code.Br_S:
            case Code.Leave:
            case Code.Leave_S:
            case Code.Ret:
                return 0;

            case Code.Newobj:
            case Code.Call:
            case Code.Callvirt:
            {
                if (instruction.Operand is not IMethod method || method.MethodSig == null)
                {
                    throw new InvalidOperationException($"{methodName}: 无法解析调用目标 {instruction.Operand}");
                }

                int pops = method.MethodSig.Params.Count;
                int pushes = method.MethodSig.RetType.ElementType == ElementType.Void ? 0 : 1;

                if (instruction.OpCode.Code == Code.Newobj)
                {
                    // newobj 不弹 this，只弹构造参数（值类型构造同理）
                    return 1 - pops;
                }

                if (method.MethodSig.HasThis && instruction.OpCode.Code != Code.Call || method.MethodSig.ExplicitThis)
                {
                    pops++;
                }

                return pushes - pops;
            }

            default:
                throw new InvalidOperationException($"{methodName}: 自检不支持指令 {instruction.OpCode.Name}");
        }
    }
}
