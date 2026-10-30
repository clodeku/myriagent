using Xunit.Abstractions;
using Xunit.Sdk;

namespace Myriagent.Tests.TestSupport;

/// <summary>測試方法的執行順序（<see cref="PriorityOrderer"/>）：小的先跑，沒標的是 0</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}

/// <summary>
/// 依 <see cref="TestPriorityAttribute"/> 排序（同一優先順序依方法名稱）：給「前面的測試跑完後再檢查」的類別用，
/// 例如掃描前面幾個測試寫下的記錄。用法：<c>[TestCaseOrderer(PriorityOrderer.TypeName, PriorityOrderer.AssemblyName)]</c>
/// </summary>
public sealed class PriorityOrderer : ITestCaseOrderer
{
    public const string TypeName = "Myriagent.Tests.TestSupport.PriorityOrderer";

    public const string AssemblyName = "Myriagent.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases
            .OrderBy(t => Priority(t))
            .ThenBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal)
            .ToList();

    private static int Priority(ITestCase testCase) =>
        testCase.TestMethod.Method.GetCustomAttributes(typeof(TestPriorityAttribute).AssemblyQualifiedName)
            .Select(a => a.GetConstructorArguments().OfType<int>().FirstOrDefault())
            .FirstOrDefault();
}
