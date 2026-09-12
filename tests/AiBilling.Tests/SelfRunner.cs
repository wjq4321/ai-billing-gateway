using System.Reflection;

namespace AiBilling.Tests;

/// <summary>
/// 独立测试运行器。
///
/// 为什么需要它：标准 `dotnet test` 依赖 VSTest 的 testhost，而 testhost 会调用
/// OpenProcess 去挂父进程退出的回调。在受限/沙箱环境里该调用被拒绝（Win32 错误 5），
/// 导致测试无法运行。本运行器直接用反射调用同一个测试程序集里的 [TestMethod]，
/// 在没有 testhost 的情况下跑完全部断言。
///
/// 用法：dotnet run --project tests/AiBilling.Tests -- --self-run
/// 常规环境仍可直接用 `dotnet test`。
/// </summary>
public static class SelfRunner
{
    public static int RunAll(string[] args)
    {
        var asm = Assembly.GetExecutingAssembly();
        int passed = 0, failed = 0;
        var failures = new List<string>();

        foreach (var type in asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
        {
            var testMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<TestMethodAttribute>() is not null ||
                            m.GetCustomAttribute<DataTestMethodAttribute>() is not null)
                .ToList();
            if (testMethods.Count == 0) continue;

            Console.WriteLine();
            Console.WriteLine($"  {type.Name}");
            Console.WriteLine("  " + new string('─', 60));

            foreach (var m in testMethods)
            {
                var dataRows = m.GetCustomAttributes<DataRowAttribute>().ToList();

                if (dataRows.Count == 0)
                {
                    RunOne(type, m, Array.Empty<object>(), ref passed, ref failed, failures);
                }
                else
                {
                    foreach (var row in dataRows)
                    {
                        var rowArgs = row.Data ?? Array.Empty<object>();
                        var label = $"{m.Name}({string.Join(", ", rowArgs.Select(a => Fmt(a)))})";
                        RunOne(type, m, rowArgs!, ref passed, ref failed, failures, label);
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("  " + new string('═', 60));
        Console.WriteLine($"  通过 {passed}    失败 {failed}");
        Console.WriteLine("  " + new string('═', 60));

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  失败详情：");
            foreach (var f in failures) Console.WriteLine("    · " + f);
            Console.WriteLine();
            return 1;
        }

        Console.WriteLine();
        return 0;
    }

    private static void RunOne(
        Type type, MethodInfo m, object[] args,
        ref int passed, ref int failed, List<string> failures, string? label = null)
    {
        var name = label ?? m.Name;
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            m.Invoke(instance, args);
            passed++;
            Console.WriteLine($"    ✓ {name}");
        }
        catch (TargetInvocationException tie)
        {
            failed++;
            var msg = tie.InnerException?.Message ?? tie.Message;
            failures.Add($"{type.Name}.{name} → {msg}");
            Console.WriteLine($"    ✗ {name}");
            Console.WriteLine($"        {msg}");
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add($"{type.Name}.{name} → {ex.Message}");
            Console.WriteLine($"    ✗ {name}");
            Console.WriteLine($"        {ex.Message}");
        }
        finally
        {
            if (instance is IDisposable d) d.Dispose();
        }
    }

    private static string Fmt(object? o) => o switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => o.ToString() ?? "?"
    };
}
