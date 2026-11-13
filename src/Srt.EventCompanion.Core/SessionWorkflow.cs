namespace Srt.EventCompanion.Core;

public enum CompanionSessionState
{
    Idle,
    Prepared,
    LaunchRequested,
    Running,
    Completed,
    Aborted
}

public enum ContentManagerIntegrationStatus
{
    Unverified,
    Verified
}

public sealed record SessionDriver(string SrtUsername, string DisplayName);

public sealed record RigSessionConfiguration(
    string RigId,
    string EventId,
    ContentManagerIntegrationStatus ContentManagerIntegration,
    OnlineJoinTarget? OnlineJoinTarget);

public sealed record SessionRequest(SessionDriver Driver);

public sealed record SessionPreparation(
    Guid SessionId,
    SessionRequest Request,
    ProfileChangePreview ProfileChange,
    Uri ContentManagerJoinUri,
    CompanionSessionState State);

public sealed class SessionPreparationService
{
    private readonly RigProfileService _rigProfileService;

    public SessionPreparationService(RigProfileService rigProfileService) => _rigProfileService = rigProfileService;

    public SessionPreparation Prepare(RigSessionConfiguration configuration, SessionRequest request, string raceIniPath)
    {
        ValidateConfiguration(configuration);
        ValidateRequest(request);

        var profileChange = _rigProfileService.PreviewDriverNameChange(raceIniPath, request.Driver.DisplayName);
        return new SessionPreparation(Guid.NewGuid(), request, profileChange, configuration.OnlineJoinTarget!.ToUri(), CompanionSessionState.Prepared);
    }

    private static void ValidateConfiguration(RigSessionConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.RigId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.EventId);
        if (configuration.ContentManagerIntegration != ContentManagerIntegrationStatus.Verified)
        {
            throw new InvalidOperationException("Content Manager launch integration has not been verified for this rig.");
        }

        if (configuration.OnlineJoinTarget is null)
        {
            throw new ArgumentException("A verified rig must include an online join target.", nameof(configuration));
        }
    }

    private static void ValidateRequest(SessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Driver.SrtUsername) || !request.Driver.SrtUsername.StartsWith("@", StringComparison.Ordinal))
        {
            throw new ArgumentException("Driver SRT username must begin with @.", nameof(request));
        }

        if (!SrtUsername.IsValid(SrtUsername.Normalize(request.Driver.SrtUsername)))
        {
            throw new ArgumentException("Driver SRT username must be 2–32 characters: letters, numbers, \"_\" or \".\".", nameof(request));
        }
    }
}

public sealed class SessionLifecycle
{
    public CompanionSessionState State { get; private set; } = CompanionSessionState.Idle;

    public void MarkPrepared() => MoveTo(CompanionSessionState.Prepared, CompanionSessionState.Idle);

    public void RequestLaunch() => MoveTo(CompanionSessionState.LaunchRequested, CompanionSessionState.Prepared);

    public void MarkRunning() => MoveTo(CompanionSessionState.Running, CompanionSessionState.LaunchRequested);

    public void Complete() => MoveTo(CompanionSessionState.Completed, CompanionSessionState.Running);

    public void Abort()
    {
        if (State is CompanionSessionState.Completed or CompanionSessionState.Aborted)
        {
            throw new InvalidOperationException($"A {State} session cannot be aborted.");
        }

        State = CompanionSessionState.Aborted;
    }

    private void MoveTo(CompanionSessionState nextState, CompanionSessionState requiredState)
    {
        if (State != requiredState)
        {
            throw new InvalidOperationException($"Cannot move from {State} to {nextState}.");
        }

        State = nextState;
    }
}
