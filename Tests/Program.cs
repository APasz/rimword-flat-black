using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace FlatBlack.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        string gameDirectory = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RimWorldDir").Value!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(gameDirectory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };

        return Run(args.Length == 1 ? Path.GetFullPath(args[0]) : FindRepository());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string repository)
    {
        FieldInfo preferences = typeof(Verse.Prefs).GetField("data", BindingFlags.Static | BindingFlags.NonPublic)!;
        preferences.SetValue(null, Activator.CreateInstance(preferences.FieldType));
        Verse.LanguageDatabase.activeLanguage = new Verse.LoadedLanguage("English") { info = new Verse.LanguageInfo { canBeTiny = true } };
        return ThemeTests.Run(repository);
    }

    private static string FindRepository()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Source", "FlatBlack.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Pass the mod repository path as the first argument.");
    }
}
