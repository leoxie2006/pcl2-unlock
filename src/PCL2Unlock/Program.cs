using System;
using System.Collections.Generic;
using System.IO;

namespace Pcl2Unlock;

/// <summary>命令行入口：解析参数、加载程序集、依次跑三个补丁、写出结果。</summary>
internal static class Program
{
    private const string Banner = "PCL2 下载功能解锁补丁（针对 PCL2 2.13.1 / 2.99.0.0(413)）";

    private static int Main(string[] args)
    {
        if (args.Length == 0 || Array.IndexOf(args, "-h") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var options = new PatchOptions();
        if (!options.Parse(args, out var error))
        {
            Console.Error.WriteLine("参数错误：" + error);
            PrintUsage();
            return 1;
        }

        if (!File.Exists(options.Input))
        {
            Console.Error.WriteLine("找不到输入文件：" + options.Input);
            return 1;
        }

        Console.WriteLine(Banner);

        using var module = dnlib.DotNet.ModuleDefMD.Load(options.Input);
        var patcher = new Pcl2Patcher(module);

        var outcomes = new List<PatchOutcome>();

        foreach (var (id, name, isApplied, apply) in patcher.AllPatches())
        {
            if (!options.ShouldRun(id))
            {
                continue;
            }

            if (options.CheckOnly)
            {
                bool unlocked = isApplied();
                Console.WriteLine($"  [{id}] {name} → {(unlocked ? "已解锁" : "未解锁")}");
                outcomes.Add(unlocked ? PatchOutcome.AlreadyApplied : PatchOutcome.NotFound);
                continue;
            }

            var outcome = isApplied() ? PatchOutcome.AlreadyApplied : apply();
            outcomes.Add(outcome);
            Console.WriteLine($"  [{id}] {name} → {Describe(outcome)}");
        }

        bool allUnlocked = outcomes.TrueForAll(o => o == PatchOutcome.Applied || o == PatchOutcome.AlreadyApplied);

        if (options.CheckOnly)
        {
            Console.WriteLine(allUnlocked ? "检查完成：目标程序集已全部处于解锁状态。" : "检查完成：仍有补丁未应用（返回 2）。");
            return allUnlocked ? 0 : 2;
        }

        if (!allUnlocked)
        {
            Console.Error.WriteLine("有补丁未能定位目标代码，已中止，未写出文件。请确认 PCL2 版本是否匹配。");
            return 3;
        }

        module.Write(options.Output);
        Console.WriteLine("已写出：" + Path.GetFullPath(options.Output));
        return 0;
    }

    private static string Describe(PatchOutcome outcome) => outcome switch
    {
        PatchOutcome.Applied => "已应用",
        PatchOutcome.AlreadyApplied => "已是解锁状态，跳过",
        PatchOutcome.NotFound => "未找到目标代码（版本不匹配？）",
        _ => outcome.ToString(),
    };

    private static void PrintUsage()
    {
        Console.WriteLine(Banner);
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  pcl2unlock <原始 exe> [-o <输出 exe>] [--only 1,2,3] [--check]");
        Console.WriteLine();
        Console.WriteLine("参数：");
        Console.WriteLine("  -o, --output <路径>   输出文件，默认 <输入名>.unlocked.exe");
        Console.WriteLine("  --only <列表>         只跑指定补丁（1=本体下载 2=整合包分派 3=整合包按钮）");
        Console.WriteLine("  -c, --check           只检查是否已解锁，不写文件（已解锁返回 0，未解锁返回 2）");
        Console.WriteLine("  -h, --help            显示本帮助");
        Console.WriteLine();
        Console.WriteLine("示例：");
        Console.WriteLine("  dotnet run --project src/PCL2Unlock -- \"Plain Craft Launcher 2.exe\"");
        Console.WriteLine("  dotnet run --project src/PCL2Unlock -- PCL2.exe -o PCL2.unlocked.exe --only 1");
    }
}

/// <summary>命令行参数。</summary>
internal sealed class PatchOptions
{
    public string Input { get; private set; } = "";
    public string Output { get; private set; } = "";
    public bool CheckOnly { get; private set; }

    private readonly HashSet<int> _only = new();

    public bool ShouldRun(int id) => _only.Count == 0 || _only.Contains(id);

    public bool Parse(string[] args, out string error)
    {
        error = "";

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o":
                case "--output":
                    if (++i >= args.Length)
                    {
                        error = "缺少输出路径";
                        return false;
                    }
                    Output = args[i];
                    break;

                case "--only":
                    if (++i >= args.Length)
                    {
                        error = "缺少补丁编号列表";
                        return false;
                    }
                    foreach (var part in args[i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!int.TryParse(part.Trim(), out int id) || id < 1 || id > 3)
                        {
                            error = "补丁编号只能是 1、2、3：" + part;
                            return false;
                        }
                        _only.Add(id);
                    }
                    break;

                case "-c":
                case "--check":
                    CheckOnly = true;
                    break;

                default:
                    if (a.StartsWith('-'))
                    {
                        error = "未知选项：" + a;
                        return false;
                    }
                    if (Input.Length != 0)
                    {
                        error = "只能指定一个输入文件";
                        return false;
                    }
                    Input = a;
                    break;
            }
        }

        if (Input.Length == 0)
        {
            error = "缺少输入文件";
            return false;
        }

        if (Output.Length == 0)
        {
            Output = Path.ChangeExtension(Input, null) + ".unlocked.exe";
        }

        return true;
    }
}
