using System.IO;
using System.Reflection;
using Voca.Models;

namespace Voca.Services;

/// <summary>Ready-made plans bundled with the app (Seed/*.md), added to the course on first run.</summary>
public static class SeedData
{
    public static IReadOnlyList<Plan> LoadBundledPlans()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Seed.", StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return PlanFormat.Read(reader.ReadToEnd()).Plan;
            })
            .OfType<Plan>()
            .Where(p => p.Words.Count > 0)
            .ToList();
    }

    /// <summary>Adds the bundled plans once; after that the learner owns the list (deleting them is final).</summary>
    public static void EnsureSeeded(AppData data)
    {
        if (data.Seeded) return;
        foreach (var plan in LoadBundledPlans())
        {
            data.Plans.Add(plan);
            data.Course.Add(plan.Id);
        }
        data.Seeded = true;
        CourseEngine.EnsurePosition(data);
    }
}
