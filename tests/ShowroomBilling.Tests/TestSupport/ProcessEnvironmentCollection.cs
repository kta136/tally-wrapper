namespace ShowroomBilling.Tests.TestSupport;

/// <summary>
/// For tests that change an environment variable the whole process reads,
/// such as the data folder. They run by themselves, after every other test
/// has finished: an API booted by a contract test at the same moment would
/// otherwise pick the changed folder up and keep its log file open in it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
