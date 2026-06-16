using System.Linq;
using Content.Server.EUI;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared._RuMC14.RoleTests;
using Content.Shared.Eui;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._RuMC14.RoleTests;

public sealed class RoleTestEui : BaseEui
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private PlayTimeTrackingManager _playTime = default!;

    private ActiveRoleTest? _active;
    private Dictionary<string, int> _activeCorrectAnswers = new();
    private readonly string? _initialTestId;
    private string? _message;

    public RoleTestEui(string? initialTestId = null)
    {
        _initialTestId = initialTestId;
        IoCManager.InjectDependencies(this);
    }

    public override void Opened()
    {
        if (_initialTestId != null)
        {
            StartTest(_initialTestId);
            return;
        }

        StateDirty();
    }

    public override EuiStateBase GetNewState()
    {
        var playTimes = _playTime.GetTrackerTimes(Player);
        var tests = EnumerateRoleTests()
            .OrderBy(test => test.Responsibility)
            .ThenBy(test => test.RequiresLaw)
            .ThenBy(test => test.Name)
            .Select(test =>
            {
                var questions = GetQuestions(test);
                var available = GetAvailableQuestions(test, questions).Count;
                return new RoleTestEntry(
                    test.Id,
                    test.Name,
                    test.Description,
                    test.Responsibility,
                    test.QuestionCount,
                    test.RequiresLaw,
                    playTimes.GetValueOrDefault(RoleTestShared.GetTracker(test.Id)) > TimeSpan.Zero,
                    CanStart(test, questions),
                    available);
            })
            .ToList();

        return new RoleTestEuiState(tests, _active, _message);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        switch (msg)
        {
            case RoleTestEuiMsg.StartTest start:
                StartTest(start.TestId);
                break;
            case RoleTestEuiMsg.SubmitTest submit:
                SubmitTest(submit.TestId, submit.Answers);
                break;
            case RoleTestEuiMsg.CancelTest:
                _active = null;
                _message = null;
                StateDirty();
                break;
        }
    }

    private void StartTest(string testId)
    {
        var test = GetRoleTest(testId);
        if (test == null)
            return;

        var allQuestions = GetQuestions(test);
        if (!CanStart(test, allQuestions))
        {
            _message = Loc.GetString(
                "role-test-not-enough-questions",
                ("available", GetAvailableQuestions(test, allQuestions).Count),
                ("required", test.QuestionCount));
            StateDirty();
            return;
        }

        var selected = PickQuestions(test, allQuestions);
        var activeQuestions = BuildActiveQuestions(selected);
        _active = new ActiveRoleTest(
            test.Id,
            test.Name,
            activeQuestions);
        _message = null;
        StateDirty();
    }

    private void SubmitTest(string testId, Dictionary<string, int> answers)
    {
        if (_active == null || _active.TestId != testId)
        {
            return;
        }

        var correct = 0;
        foreach (var question in _active.Questions)
        {
            if (!_activeCorrectAnswers.TryGetValue(question.Id, out var correctAnswer))
                continue;

            if (answers.TryGetValue(question.Id, out var answer) &&
                answer == correctAnswer)
            {
                correct++;
            }
        }

        if (correct != _active.Questions.Count)
        {
            _message = Loc.GetString(
                "role-test-failed",
                ("correct", correct),
                ("total", _active.Questions.Count));
            _active = null;
            _activeCorrectAnswers.Clear();
            StateDirty();
            return;
        }

        _playTime.AddTimeToTracker(Player, RoleTestShared.GetTracker(_active.TestId), TimeSpan.FromSeconds(1));
        _playTime.QueueSendTimers(Player);
        _message = Loc.GetString("role-test-passed", ("test", _active.Name));
        _active = null;
        _activeCorrectAnswers.Clear();
        StateDirty();
    }

    private bool CanStart(RoleTestDefinition test, List<TestQuestion> questions)
    {
        foreach (var (pool, count) in test.RequiredPools)
        {
            if (questions.Count(question => question.Pools.Contains(pool)) < count)
                return false;
        }

        return GetAvailableQuestions(test, questions).Count >= test.QuestionCount;
    }

    private List<TestQuestion> GetAvailableQuestions(
        RoleTestDefinition test,
        List<TestQuestion> questions)
    {
        return questions
            .Where(question => question.Pools.Overlaps(test.QuestionPools))
            .ToList();
    }

    private List<TestQuestion> PickQuestions(
        RoleTestDefinition test,
        List<TestQuestion> questions)
    {
        var selected = new List<TestQuestion>();
        var used = new HashSet<string>();

        foreach (var (pool, count) in test.RequiredPools)
        {
            var poolQuestions = questions
                .Where(question => question.Pools.Contains(pool))
                .Where(question => used.Add(question.ID))
                .ToList();
            _random.Shuffle(poolQuestions);
            selected.AddRange(poolQuestions.Take(count));
        }

        var remaining = GetAvailableQuestions(test, questions)
            .Where(question => !used.Contains(question.ID))
            .ToList();
        _random.Shuffle(remaining);
        selected.AddRange(remaining.Take(test.QuestionCount - selected.Count));
        _random.Shuffle(selected);
        return selected;
    }

    private List<RoleTestQuestionData> BuildActiveQuestions(List<TestQuestion> selected)
    {
        _activeCorrectAnswers.Clear();
        var activeQuestions = new List<RoleTestQuestionData>();

        foreach (var question in selected)
        {
            var answers = question.Answers
                .Select((answer, index) => (Answer: answer, OriginalIndex: index))
                .ToList();
            _random.Shuffle(answers);

            var correctAnswer = answers.FindIndex(answer => answer.OriginalIndex == question.CorrectAnswer);
            _activeCorrectAnswers[question.ID] = correctAnswer;
            activeQuestions.Add(new RoleTestQuestionData(
                question.ID,
                question.Text,
                answers.Select(answer => answer.Answer).ToList()));
        }

        return activeQuestions;
    }

    private IEnumerable<RoleTestDefinition> EnumerateRoleTests()
    {
        foreach (var job in _prototypes.EnumeratePrototypes<JobPrototype>())
        {
            if (!job.SetPreference || job.Hidden || RoleTestShared.IsRoleTestExempt(job))
                continue;

            yield return CreateJobRoleTest(job);
        }
    }

    private RoleTestDefinition? GetRoleTest(string testId)
    {
        if (RoleTestShared.TryGetJobId(testId, out var jobId) &&
            _prototypes.TryIndex<JobPrototype>(jobId, out var job) &&
            !RoleTestShared.IsRoleTestExempt(job))
        {
            return CreateJobRoleTest(job);
        }

        return null;
    }

    private RoleTestDefinition CreateJobRoleTest(JobPrototype job)
    {
        var responsibility = RoleTestShared.GetResponsibility(job);
        var requiresLaw = RoleTestShared.RequiresLaw(job);
        var questionPools = new HashSet<string> { RoleTestShared.GetJobQuestionPool(job.ID) };
        var requiredPools = new Dictionary<string, int>();

        if (requiresLaw)
        {
            questionPools.Add(RoleTestShared.LawPool);
            requiredPools[RoleTestShared.LawPool] = RoleTestShared.GetRequiredLawQuestionCount(responsibility);
        }

        return new RoleTestDefinition(
            RoleTestShared.GetJobTestId(job.ID),
            Loc.GetString("role-test-job-name", ("job", job.LocalizedName)),
            job.LocalizedDescription,
            responsibility,
            RoleTestShared.GetQuestionCount(responsibility),
            questionPools,
            requiredPools,
            requiresLaw,
            job);
    }

    private List<TestQuestion> GetQuestions(RoleTestDefinition test)
    {
        var questions = _prototypes.EnumeratePrototypes<RoleTestQuestionPrototype>()
            .Select(question => new TestQuestion(
                question.ID,
                question.Text,
                question.Answers,
                question.CorrectAnswer,
                question.Pools))
            .Where(question => question.Pools.Overlaps(test.QuestionPools))
            .ToList();

        if (test.Job != null)
            questions.AddRange(CreateGeneratedJobQuestions(test.Job, test));

        return questions;
    }

    private List<TestQuestion> CreateGeneratedJobQuestions(JobPrototype job, RoleTestDefinition test)
    {
        var role = job.LocalizedName;
        var supervisors = Loc.GetString(job.Supervisors);
        var description = string.IsNullOrWhiteSpace(job.LocalizedDescription)
            ? Loc.GetString("role-test-generated-duty-generic", ("role", role))
            : job.LocalizedDescription;
        var pool = RoleTestShared.GetJobQuestionPool(job.ID);
        var questions = new List<TestQuestion>();

        AddGeneratedQuestion(
            questions,
            job,
            pool,
            "role-test-generated-role-name-question",
            role,
            role,
            Loc.GetString("role-test-generated-wrong-random-role"),
            Loc.GetString("role-test-generated-wrong-command"),
            Loc.GetString("role-test-generated-wrong-antag"));

        AddGeneratedQuestion(
            questions,
            job,
            pool,
            "role-test-generated-supervisor-question",
            supervisors,
            supervisors,
            Loc.GetString("role-test-generated-wrong-no-supervisor"),
            Loc.GetString("role-test-generated-wrong-self-command"),
            Loc.GetString("role-test-generated-wrong-anyone"));

        AddGeneratedQuestion(
            questions,
            job,
            pool,
            "role-test-generated-duty-question",
            description,
            description,
            Loc.GetString("role-test-generated-wrong-ignore-duty"),
            Loc.GetString("role-test-generated-wrong-leave-role"),
            Loc.GetString("role-test-generated-wrong-disrupt"));

        var templates = new[]
        {
            "role-test-generated-ask-question",
            "role-test-generated-roundstart-question",
            "role-test-generated-unknown-mechanic-question",
            "role-test-generated-teamwork-question",
            "role-test-generated-escalation-question",
            "role-test-generated-equipment-question",
            "role-test-generated-communication-question",
            "role-test-generated-priority-question",
            "role-test-generated-orders-question",
            "role-test-generated-emergency-question",
        };

        var correct = new[]
        {
            "role-test-generated-correct-ask",
            "role-test-generated-correct-roundstart",
            "role-test-generated-correct-unknown-mechanic",
            "role-test-generated-correct-teamwork",
            "role-test-generated-correct-escalation",
            "role-test-generated-correct-equipment",
            "role-test-generated-correct-communication",
            "role-test-generated-correct-priority",
            "role-test-generated-correct-orders",
            "role-test-generated-correct-emergency",
        };

        for (var i = 0; questions.Count < test.QuestionCount; i++)
        {
            var template = templates[i % templates.Length];
            var correctKey = correct[i % correct.Length];
            AddGeneratedQuestion(
                questions,
                job,
                pool,
                template,
                Loc.GetString(correctKey, ("role", role)),
                Loc.GetString(correctKey, ("role", role)),
                Loc.GetString("role-test-generated-wrong-ignore-duty"),
                Loc.GetString("role-test-generated-wrong-leave-role"),
                Loc.GetString("role-test-generated-wrong-disrupt"));
        }

        return questions;
    }

    private void AddGeneratedQuestion(
        List<TestQuestion> questions,
        JobPrototype job,
        string pool,
        string questionKey,
        string correct,
        params string[] answers)
    {
        var id = $"{RoleTestShared.GetJobTestId(job.ID)}:{questions.Count}";
        questions.Add(new TestQuestion(
            id,
            Loc.GetString(questionKey, ("role", job.LocalizedName)),
            answers.ToList(),
            0,
            new HashSet<string> { pool }));
    }

    private sealed record RoleTestDefinition(
        string Id,
        string Name,
        string? Description,
        RoleTestResponsibility Responsibility,
        int QuestionCount,
        HashSet<string> QuestionPools,
        Dictionary<string, int> RequiredPools,
        bool RequiresLaw,
        JobPrototype? Job);

    private sealed record TestQuestion(
        string ID,
        string Text,
        List<string> Answers,
        int CorrectAnswer,
        HashSet<string> Pools);
}
