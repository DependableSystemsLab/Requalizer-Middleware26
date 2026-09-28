using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;

namespace OneOS.Tests.Language;

internal static class TestUtil
{
    public static CompiledProgram Compile(params string[] sources) =>
        new AppCompiler().Compile(sources.Select((s, i) => new SourceFile($"test{i}.osh", s)).ToList());

    public static CompiledProgram CompileExample(string name) =>
        new AppCompiler().Compile(new[] { new SourceFile(name, File.ReadAllText(ExamplePath(name))) });

    public static string ExamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "examples", name);

    public static List<string> Codes(CompiledProgram p) => p.Diagnostics.Where(d => d.Code.Length > 0).Select(d => d.Code).ToList();
    public static List<string> Errors(CompiledProgram p) => p.Diagnostics.Where(d => d.IsError).Select(d => d.Code).ToList();

    public static string Dump(CompiledProgram p) => string.Join("\n", p.Diagnostics);

    public static void AssertNoErrors(CompiledProgram p) => Assert.True(!p.HasErrors, Dump(p));

    // Asserts that the expected code is reported, with the diagnostics in the failure message.
    public static void AssertHas(CompiledProgram p, string code) => Assert.True(Codes(p).Contains(code), $"expected {code}; got:\n{Dump(p)}");
    public static void AssertLacks(CompiledProgram p, string code) => Assert.False(Codes(p).Contains(code), $"did not expect {code}; got:\n{Dump(p)}");
}
