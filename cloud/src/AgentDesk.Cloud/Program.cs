using System.Threading.RateLimiting;
using AgentDesk.Cloud;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

if (CloudDatabaseMaintenanceCommand.IsRequested(args))
{
    Environment.ExitCode = await CloudDatabaseMaintenanceCommand.RunAsync(
        args,
        Console.Out,
        Console.Error);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<CloudOptions>()
    .Bind(builder.Configuration.GetSection(CloudOptions.SectionName))
    .Validate(
        options => CloudBootstrapTokens.IsValidConfiguredToken(options.BootstrapToken),
        "AgentDeskCloud:BootstrapToken must contain at least 32 characters.")
    .Validate(
        options => CloudBootstrapTokens.IsValidOptionalToken(options.PreviousBootstrapToken),
        "AgentDeskCloud:PreviousBootstrapToken must be empty or contain at least 32 characters.")
    .Validate(
        CloudBootstrapTokens.ConfiguredTokensDiffer,
        "AgentDeskCloud:BootstrapToken and PreviousBootstrapToken must differ.")
    .Validate(
        CloudDatabasePathPolicy.IsValid,
        "AgentDeskCloud:DatabasePath must be a fully qualified local path without reparse points or hard links.")
    .Validate(
        options => options.MaximumCiphertextBytes is >= 1024 and <= 64 * 1024 * 1024,
        "AgentDeskCloud:MaximumCiphertextBytes must be between 1 KiB and 64 MiB.")
    .Validate(
        options => options.AutomationPollingIntervalSeconds is >= 1 and <= 300,
        "AgentDeskCloud:AutomationPollingIntervalSeconds must be between 1 and 300 seconds.")
    .ValidateOnStart();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<CloudBootstrapTokens>();
builder.Services.AddSingleton<CloudDatabaseLease>();
builder.Services.AddSingleton<CloudStore>();
builder.Services.AddSingleton<CloudNotifier>();
builder.Services.AddSingleton<CloudNotificationConnectionRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<AutomationWorker>();
builder.Services.AddSignalR();
builder.Services
    .AddAuthentication(CloudAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, CloudAuthenticationHandler>(
        CloudAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization(
    options =>
    {
        options.AddPolicy(CloudPolicies.Admin, policy => policy.RequireRole(CloudRoles.Admin));
        options.AddPolicy(
            CloudPolicies.Device,
            policy => policy.RequireRole(CloudRoles.Admin, CloudRoles.Device));
        options.AddPolicy(
            CloudPolicies.Service,
            policy => policy.RequireRole(CloudRoles.Admin, CloudRoles.Service));
    });
builder.Services.AddRateLimiter(
    options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(
            "api",
            context => RateLimitPartition.GetFixedWindowLimiter(
                CloudRateLimitPartition.GetKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 240,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    });

var app = builder.Build();
var cloudOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CloudOptions>>()
    .Value;
if (!app.Environment.IsDevelopment() && cloudOptions.RequireHttps)
{
    app.UseHsts();
}
if (cloudOptions.RequireHttps)
{
    app.Use(
        async (context, next) =>
        {
            if (!context.Request.IsHttps)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                context.Response.Headers.Upgrade = "TLS/1.2";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(
                    new { error = "https_required" },
                    context.RequestAborted);
                return;
            }
            await next(context);
        });
}
app.UseExceptionHandler();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

var store = app.Services.GetRequiredService<CloudStore>();
_ = app.Services.GetRequiredService<CloudDatabaseLease>();
await store.InitializeAsync();

app.MapGet("/health/live", static () => TypedResults.Ok(new { status = "ok" }))
    .AllowAnonymous();
app.MapGet(
        "/health/ready",
        async Task<Results<Ok<object>, StatusCodeHttpResult>> (
            CloudStore cloudStore,
            CancellationToken cancellationToken) =>
            await cloudStore.IsReadyAsync(cancellationToken)
                ? TypedResults.Ok<object>(new { status = "ready" })
                : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

var api = app.MapGroup("/api/v1")
    .RequireAuthorization()
    .RequireRateLimiting("api");

api.MapGet(
    "/policy",
    async Task<Ok<TeamPolicy>> (
        HttpContext context,
        CloudStore cloudStore,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(
            await cloudStore.GetPolicyAsync(
                context.User.CloudIdentity().TeamId,
                cancellationToken)));

api.MapPut(
    "/sync/sessions/{sessionId}",
    async Task<IResult> (
        string sessionId,
        EncryptedEnvelopeRequest request,
        HttpContext context,
        CloudStore cloudStore,
        Microsoft.Extensions.Options.IOptions<CloudOptions> options,
        CancellationToken cancellationToken) =>
    {
        if (!CloudRequestValidation.ValidIdentifier(sessionId, 128))
        {
            return CloudRequestValidation.Invalid("sessionId", "The session identifier is invalid.");
        }
        if (request.Revision < 1)
        {
            return CloudRequestValidation.Invalid("revision", "The revision must be positive.");
        }
        if (CloudRequestValidation.ValidateEnvelope(
                request.Algorithm,
                request.Nonce,
                request.Ciphertext,
                options.Value) is { } error)
        {
            return CloudRequestValidation.Invalid("envelope", error);
        }

        var result = await cloudStore.PutSessionAsync(
            context.User.CloudIdentity().TeamId,
            sessionId,
            request,
            cancellationToken);
        return result switch
        {
            SessionWriteResult.Created => Results.Created(
                $"/api/v1/sync/sessions/{sessionId}",
                new { sessionId, request.Revision }),
            SessionWriteResult.Updated => Results.Ok(new { sessionId, request.Revision }),
            SessionWriteResult.RevisionConflict => Results.Conflict(
                new { error = "revision_conflict" }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    })
    .RequireAuthorization(CloudPolicies.Device);

api.MapGet(
    "/sync/sessions/{sessionId}",
    async Task<IResult> (
        string sessionId,
        HttpContext context,
        CloudStore cloudStore,
        CancellationToken cancellationToken) =>
    {
        if (!CloudRequestValidation.ValidIdentifier(sessionId, 128))
        {
            return CloudRequestValidation.Invalid("sessionId", "The session identifier is invalid.");
        }
        var result = await cloudStore.GetSessionAsync(
            context.User.CloudIdentity().TeamId,
            sessionId,
            cancellationToken);
        if (result is not null)
        {
            return Results.Ok(result);
        }
        var tombstoneRevision = await cloudStore.GetSessionTombstoneRevisionAsync(
            context.User.CloudIdentity().TeamId,
            sessionId,
            cancellationToken);
        return tombstoneRevision is int revision
            ? Results.Json(
                new SessionDeleteResponse(sessionId, revision),
                statusCode: StatusCodes.Status410Gone)
            : Results.NotFound();
    })
    .RequireAuthorization(CloudPolicies.Device);

api.MapDelete(
    "/sync/sessions/{sessionId}",
    async Task<IResult> (
        string sessionId,
        int revision,
        HttpContext context,
        CloudStore cloudStore,
        CancellationToken cancellationToken) =>
    {
        if (!CloudRequestValidation.ValidIdentifier(sessionId, 128))
        {
            return CloudRequestValidation.Invalid("sessionId", "The session identifier is invalid.");
        }
        if (revision < 1)
        {
            return CloudRequestValidation.Invalid("revision", "The revision must be positive.");
        }
        var result = await cloudStore.DeleteSessionAsync(
            context.User.CloudIdentity().TeamId,
            sessionId,
            revision,
            cancellationToken);
        return result.Status switch
        {
            SessionDeleteStatus.Deleted or SessionDeleteStatus.AlreadyDeleted => Results.Ok(
                new SessionDeleteResponse(sessionId, result.Revision!.Value)),
            SessionDeleteStatus.NotFound => Results.NotFound(),
            SessionDeleteStatus.RevisionConflict => Results.Conflict(
                new { error = "revision_conflict" }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    })
    .RequireAuthorization(CloudPolicies.Device);

api.MapPost(
    "/runners/{runnerId}/register",
    async Task<IResult> (
        string runnerId,
        RunnerRegistrationRequest request,
        HttpContext context,
        CloudStore cloudStore,
        CancellationToken cancellationToken) =>
    {
        var identity = context.User.CloudIdentity();
        if (identity.Role != CloudRoles.Admin &&
            !string.Equals(identity.SubjectId, runnerId, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }
        if (!CloudRequestValidation.ValidIdentifier(runnerId, 128) ||
            request.Capabilities is not { Count: > 0 and <= 32 } ||
            request.Capabilities.Any(
                capability => !CloudRequestValidation.ValidIdentifier(capability, 64)))
        {
            return CloudRequestValidation.Invalid("runner", "The runner registration is invalid.");
        }
        var capabilities = request.Capabilities
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await cloudStore.RegisterRunnerAsync(
            identity.TeamId,
            runnerId,
            capabilities,
            cancellationToken);
        return Results.Ok(new { runnerId, status = "online" });
    })
    .RequireAuthorization(CloudPolicies.Service);

api.MapPost(
    "/jobs",
    async Task<IResult> (
        JobQueueRequest request,
        HttpContext context,
        CloudStore cloudStore,
        CloudNotifier notifier,
        Microsoft.Extensions.Options.IOptions<CloudOptions> options,
        CancellationToken cancellationToken) =>
    {
        if (!CloudRequestValidation.ValidIdentifier(request.JobId, 128) ||
            !string.Equals(request.Kind, RunnerPayloadKinds.Task, StringComparison.Ordinal) ||
            !CloudRequestValidation.ValidIdentifier(request.RequiredCapability, 64) ||
            request.AutomationId is not null ||
            request.RunId is not null)
        {
            return CloudRequestValidation.Invalid(
                "job",
                "The job identity or required capability is invalid.");
        }
        if (CloudRequestValidation.ValidateEnvelope(
                request.Algorithm,
                request.Nonce,
                request.Ciphertext,
                options.Value) is { } error)
        {
            return CloudRequestValidation.Invalid("envelope", error);
        }
        var identity = context.User.CloudIdentity();
        var queued = await cloudStore.QueueJobAsync(
            identity.TeamId,
            request,
            cancellationToken);
        if (queued.Status is JobQueueStatus.Created)
        {
            var jobId = queued.JobId ?? throw new InvalidOperationException(
                "The created job did not include an identifier.");
            await notifier.JobChangedAsync(identity.TeamId, jobId, cancellationToken);
            return Results.Created(
                $"/api/v1/jobs/{jobId}",
                new JobCreatedResponse(jobId));
        }
        return queued.Status switch
        {
            JobQueueStatus.RemoteRunnerDisabled => Results.Conflict(
                new { error = "remote_runner_disabled" }),
            JobQueueStatus.MaximumConcurrentJobsReached => Results.Conflict(
                new { error = "maximum_concurrent_jobs_reached" }),
            JobQueueStatus.Duplicate => Results.Conflict(new { error = "duplicate_job" }),
            _ => throw new InvalidOperationException("The job queue status is invalid."),
        };
    })
    .RequireAuthorization(CloudPolicies.Device);

api.MapPost(
    "/runners/{runnerId}/claim",
    async Task<IResult> (
        string runnerId,
        JobClaimRequest request,
        HttpContext context,
        CloudStore cloudStore,
        CancellationToken cancellationToken) =>
    {
        var identity = context.User.CloudIdentity();
        if (identity.Role != CloudRoles.Admin &&
            !string.Equals(identity.SubjectId, runnerId, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }
        if (!CloudRequestValidation.ValidIdentifier(runnerId, 128) ||
            request.LeaseSeconds is < 10 or > 600)
        {
            return CloudRequestValidation.Invalid("claim", "The runner claim request is invalid.");
        }
        var claim = await cloudStore.ClaimJobAsync(
            identity.TeamId,
            runnerId,
            request.LeaseSeconds,
            cancellationToken);
        return claim.Status switch
        {
            JobClaimStatus.Claimed => Results.Ok(claim.Job),
            JobClaimStatus.Empty => Results.NoContent(),
            JobClaimStatus.RemoteRunnerDisabled => Results.Conflict(
                new { error = "remote_runner_disabled" }),
            JobClaimStatus.MaximumConcurrentJobsReached => Results.Conflict(
                new { error = "maximum_concurrent_jobs_reached" }),
            _ => throw new InvalidOperationException("The job claim status is invalid."),
        };
    })
    .RequireAuthorization(CloudPolicies.Service);

api.MapPost(
    "/jobs/{jobId}/complete",
    async Task<IResult> (
        string jobId,
        JobCompleteRequest request,
        HttpContext context,
        CloudStore cloudStore,
        Microsoft.Extensions.Options.IOptions<CloudOptions> options,
        CancellationToken cancellationToken) =>
    {
        if (!CloudRequestValidation.ValidIdentifier(jobId, 128) ||
            !CloudRequestValidation.ValidIdentifier(request.RunnerId, 128) ||
            !CloudRequestValidation.ValidIdentifier(request.LeaseToken, 128) ||
            request.LeaseGeneration < 1 ||
            !CloudRequestValidation.ValidIdentifier(request.Kind, 32) ||
            !CloudRequestValidation.ValidIdentifier(request.RequiredCapability, 64) ||
            (request.AutomationId is not null &&
                !CloudRequestValidation.ValidIdentifier(request.AutomationId, 128)) ||
            (request.RunId is not null &&
                !CloudRequestValidation.ValidIdentifier(request.RunId, 128)))
        {
            return CloudRequestValidation.Invalid("job", "The job completion identity is invalid.");
        }
        if (CloudRequestValidation.ValidateEnvelope(
                request.Algorithm,
                request.Nonce,
                request.Ciphertext,
                options.Value) is { } error)
        {
            return CloudRequestValidation.Invalid("envelope", error);
        }
        var identity = context.User.CloudIdentity();
        if (identity.Role != CloudRoles.Admin &&
            !string.Equals(identity.SubjectId, request.RunnerId, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }
        return await cloudStore.CompleteJobAsync(
            identity.TeamId,
            jobId,
            request,
            cancellationToken)
            ? Results.Ok(new { jobId, status = "completed" })
            : Results.Conflict(new { error = "job_not_leased" });
    })
    .RequireAuthorization(CloudPolicies.Service);

api.MapCloudFeatureEndpoints();
app.MapHub<CloudNotificationHub>(CloudNotificationHub.Route)
    .RequireAuthorization();

app.Run();

public partial class Program;
