using AiBilling.Tests;

// 入口点：
//   dotnet AiBilling.Tests.dll               → 自运行全部测试（不依赖 VSTest testhost）
//   dotnet test                              → 常规 VSTest 流程（环境允许时）
//
// 受限环境下 VSTest 的 testhost 无法调用 OpenProcess，会直接崩溃，
// 因此提供这条自运行路径来验证同样的断言。

if (args.Contains("--self-run") || args.Length == 0)
{
    return SelfRunner.RunAll(args);
}

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        用法：dotnet AiBilling.Tests.dll [--self-run]

          --self-run    直接运行全部单元测试（默认行为），不依赖 VSTest。

        常规环境下也可以直接使用：
          dotnet test tests/AiBilling.Tests/AiBilling.Tests.csproj
        """);
    return 0;
}

return SelfRunner.RunAll(args);
