using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._RuMC14.RoleTests;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;

namespace Content.IntegrationTests._CMU14.RoleTests;

public sealed class RoleTestCoverageTest
{
    [Test]
    public async Task PersonalizationJobsHaveQuestionPools()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ProtoMan;

        await server.WaitAssertion(() =>
        {
            var missing = new SortedSet<string>();
            var insufficient = new SortedSet<string>();
            var whitelisted = new SortedSet<string>();
            var questions = prototypes.EnumeratePrototypes<RoleTestQuestionPrototype>().ToList();
            var testPools = prototypes.EnumeratePrototypes<RoleTestQuestionPoolPrototype>().ToList();

            foreach (var department in prototypes.EnumeratePrototypes<DepartmentPrototype>())
            {
                if (department.EditorHidden)
                    continue;

                foreach (var jobId in department.Roles)
                {
                    if (!prototypes.TryIndex<JobPrototype>(jobId, out var job) ||
                        !job.ID.StartsWith("AU14Job") ||
                        !job.SetPreference ||
                        job.Hidden ||
                        RoleTestShared.IsRoleTestExempt(job))
                    {
                        continue;
                    }

                    if (!prototypes.TryIndex<RoleTestQuestionPoolPrototype>(job.ID, out var pool))
                    {
                        missing.Add(job.ID);
                        continue;
                    }

                    if (job.Whitelisted)
                        whitelisted.Add(job.ID);
                }
            }

            foreach (var pool in testPools)
            {
                var job = prototypes.Index(pool.Job);
                var required = RoleTestShared.GetRequiredRoleQuestionCount(
                    pool.Responsibility,
                    RoleTestShared.RequiresLaw(job));
                var available = questions
                    .Where(question => question.Pools.Contains(pool.Pool))
                    .DistinctBy(question => NormalizeQuestionText(question.Text), StringComparer.OrdinalIgnoreCase)
                    .Count();

                if (available < required)
                    insufficient.Add($"{job.ID}: pool {pool.Pool} has {available}, requires {required}");

                var testPoolsForJob = new HashSet<string> { RoleTestShared.CommonPool, pool.Pool };
                if (RoleTestShared.RequiresLaw(job))
                    testPoolsForJob.Add(RoleTestShared.LawPool);

                var totalUnique = questions
                    .Where(question => question.Pools.Overlaps(testPoolsForJob))
                    .DistinctBy(question => NormalizeQuestionText(question.Text), StringComparer.OrdinalIgnoreCase)
                    .Count();
                var questionCount = RoleTestShared.GetQuestionCount(pool.Responsibility);
                if (totalUnique < questionCount)
                    insufficient.Add($"{job.ID}: has {totalUnique} unique questions, requires {questionCount}");
            }

            Assert.That(missing, Is.Empty,
                $"CMU personalization jobs without role test pools:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
            Assert.That(whitelisted, Is.Empty,
                $"CMU personalization jobs must use role tests instead of whitelists:{Environment.NewLine}{string.Join(Environment.NewLine, whitelisted)}");
            Assert.That(insufficient, Is.Empty,
                $"CMU personalization jobs without enough role questions:{Environment.NewLine}{string.Join(Environment.NewLine, insufficient)}");
            Assert.That(testPools.Select(pool => pool.Responsibility).Distinct(), Is.EquivalentTo(
                new[]
                {
                    RoleTestResponsibility.Low,
                    RoleTestResponsibility.Medium,
                    RoleTestResponsibility.High,
                }),
                "Role test pools must include low, medium, and high responsibility roles.");
        });

        await pair.CleanReturnAsync();
    }

    private static string NormalizeQuestionText(string text)
    {
        return string.Join(' ', text.Split((char[]) null!, StringSplitOptions.RemoveEmptyEntries));
    }
}
