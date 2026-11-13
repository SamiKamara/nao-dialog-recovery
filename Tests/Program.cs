using NaoRecovery;
using NaoRecovery.Tests;

var passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Run(string name, Func<Task> body)
{
    await body();
    Console.WriteLine("PASS " + name);
    passed++;
}

foreach (var (scenario, expected, restarted) in new[]
{
    (Scenario.Healthy, RecoveryStatus.Healthy, false),
    (Scenario.HealthyFinnish, RecoveryStatus.Healthy, false),
    (Scenario.IdleFinnish, RecoveryStatus.Idle, false),
    (Scenario.StalledEnglish, RecoveryStatus.ReloadRequired, true),
    (Scenario.StalledEnglishChat, RecoveryStatus.ReloadRequired, true),
    (Scenario.StalledFinnish, RecoveryStatus.ReloadRequired, true),
    (Scenario.StalledFinnishChat, RecoveryStatus.ReloadRequired, true),
    (Scenario.Transient, RecoveryStatus.Healthy, false),
    (Scenario.Speaking, RecoveryStatus.Busy, false),
    (Scenario.Paused, RecoveryStatus.Busy, false),
    (Scenario.LoadedButPaused, RecoveryStatus.Busy, false),
    (Scenario.UnknownState, RecoveryStatus.Busy, false),
    (Scenario.ChangedTopics, RecoveryStatus.Busy, false),
    (Scenario.MixedTopics, RecoveryStatus.NeedsAttention, false),
    (Scenario.MismatchedLanguages, RecoveryStatus.NeedsAttention, false),
    (Scenario.NoFinnish, RecoveryStatus.NeedsAttention, false),
    (Scenario.EmptyFinnish, RecoveryStatus.ReloadRequired, true),
    (Scenario.EmptyBoth, RecoveryStatus.ReloadRequired, true),
    (Scenario.Recoverable, RecoveryStatus.ReloadRequired, true),
    (Scenario.MissingEnglish, RecoveryStatus.NeedsAttention, false),
    (Scenario.Busy, RecoveryStatus.NeedsAttention, false),
    (Scenario.RestartRefused, RecoveryStatus.NeedsAttention, false),
    (Scenario.Stuck, RecoveryStatus.NeedsAttention, true),
    (Scenario.MissingService, RecoveryStatus.ReloadRequired, true)
})
{
    await Run(scenario.ToString(), async () =>
    {
        await using var fake = new FakeNao(scenario, arrayArgs: scenario == Scenario.Recoverable, fragmented: true);
        var lines = new List<string>();
        var result = await new RecoveryEngine(lines.Add, 3, 10, 4, 10).RunAsync("127.0.0.1", true, CancellationToken.None, fake.Port);
        Assert(result.Status == expected, $"Unexpected result: {result}");
        Assert(result.Restarted == restarted, "Restart report is wrong");
        var expectedMutations = new List<string>();
        if (restarted || scenario == Scenario.RestartRefused) expectedMutations.Add("restart:dialog");
        if (scenario is Scenario.StalledFinnish or Scenario.StalledFinnishChat or Scenario.EmptyFinnish) expectedMutations.Add("language:Finnish");
        Assert(fake.Mutations.SequenceEqual(expectedMutations), $"Unexpected mutations: {string.Join(",", fake.Mutations)}");
        if (scenario == Scenario.Transient) Assert(lines.Count(line => line.StartsWith("Recognition sample")) == 2, "A temporary context gap was not rechecked");
        if (scenario == Scenario.StalledFinnish)
        {
            Assert(lines.Count(line => line.StartsWith("Recognition sample")) == 4, "The fault was not confirmed across all samples");
            Assert(result.Message.Contains("Finnish") && result.Message.Contains("Refresh"), "Finnish/reload guidance is missing");
        }
        Assert(fake.Heartbeats > 0, "Heartbeat was not handled");
    });
}

await Run("Read-only check does not restart a broken service", async () =>
{
    await using var fake = new FakeNao(Scenario.Recoverable);
    var result = await new RecoveryEngine(_ => { }).RunAsync("127.0.0.1", false, CancellationToken.None, fake.Port);
    Assert(result.Status == RecoveryStatus.NeedsAttention && !result.Restarted, "Read-only check changed the robot");
    Assert(fake.Mutations.IsEmpty, "Read-only check sent a mutation");
});

await Run("Read-only check detects the Finnish context fault without changing anything", async () =>
{
    await using var fake = new FakeNao(Scenario.StalledFinnish);
    var result = await new RecoveryEngine(_ => { }, 3, 10, 4, 10).RunAsync("127.0.0.1", false, CancellationToken.None, fake.Port);
    Assert(result.Status == RecoveryStatus.NeedsAttention && !result.Restarted && result.Message.Contains("recognition-context"), "The Finnish fault was not diagnosed");
    Assert(fake.Mutations.IsEmpty, "Read-only context check sent a mutation");
});

await Run("Cancelling the confirmation interval prevents recovery", async () =>
{
    await using var fake = new FakeNao(Scenario.StalledFinnish);
    using var cancellation = new CancellationTokenSource();
    try
    {
        await new RecoveryEngine(line => { if (line.StartsWith("Recognition sample")) cancellation.Cancel(); })
            .RunAsync("127.0.0.1", true, cancellation.Token, fake.Port);
        throw new Exception("Confirmation was not cancelled");
    }
    catch (OperationCanceledException) { }
    Assert(fake.Mutations.IsEmpty, "Cancellation during confirmation caused a mutation");
});

await Run("Wrong service identity is never restarted", async () =>
{
    await using var fake = new FakeNao(Scenario.WrongService);
    try
    {
        await new RecoveryEngine(_ => { }).RunAsync("127.0.0.1", true, CancellationToken.None, fake.Port);
        throw new Exception("Identity guard did not reject the service");
    }
    catch (InvalidOperationException error) when (error.Message.Contains("unexpected identity")) { }
    Assert(fake.Mutations.IsEmpty, "Identity mismatch caused a mutation");
});

await Run("Cancellation interrupts an unresponsive robot", async () =>
{
    await using var fake = new FakeNao(Scenario.Silent);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
    try
    {
        await new RecoveryEngine(_ => { }).RunAsync("127.0.0.1", true, cancellation.Token, fake.Port);
        throw new Exception("Unresponsive check was not cancelled");
    }
    catch (OperationCanceledException) { }
    Assert(fake.Mutations.IsEmpty, "Unresponsive check sent a mutation");
});

await Run("An unrelated HTTP device is rejected", async () =>
{
    await using var fake = new FakeNao(Scenario.BadHandshake);
    try
    {
        await new RecoveryEngine(_ => { }).RunAsync("127.0.0.1", true, CancellationToken.None, fake.Port);
        throw new Exception("Non-NAO handshake was accepted");
    }
    catch (RobotRpcException error) when (error.Message.Contains("handshake")) { }
    Assert(fake.Mutations.IsEmpty, "A non-NAO device received a mutation");
});

await Run("Invalid address is rejected before connection", async () =>
{
    try
    {
        await new RecoveryEngine(_ => { }).RunAsync("not-an-ip", true, CancellationToken.None);
        throw new Exception("Invalid IP was accepted");
    }
    catch (ArgumentException) { }
});

if (args.Length == 2 && args[0] == "--live")
{
    await Run("Live NAO check leaves healthy listening or idle dialog unchanged", async () =>
    {
        var result = await new RecoveryEngine(Console.WriteLine).RunAsync(args[1], false, CancellationToken.None);
        Assert((result.Status is RecoveryStatus.Healthy or RecoveryStatus.Idle) && !result.Restarted, $"Live robot did not have a ready dialog: {result.Message}");
    });
}
Console.WriteLine($"{passed} checks passed.");
