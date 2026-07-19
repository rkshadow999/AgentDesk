using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud;

internal static class CloudFeatureEndpoints
{
    private static readonly HashSet<string> ExecutionProfiles =
        ["NativeProtected", "WslStrict"];

    public static RouteGroupBuilder MapCloudFeatureEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost(
                "/tokens",
                async Task<IResult> (
                    TokenCreateRequest request,
                    HttpContext context,
                    CloudStore cloudStore,
                    CloudNotificationConnectionRegistry connectionRegistry,
                    CancellationToken cancellationToken) =>
                {
                    if (!CloudRequestValidation.ValidIdentifier(request.SubjectId, 128))
                    {
                        return CloudRequestValidation.Invalid(
                            "subjectId",
                            "The token subject identifier is invalid.");
                    }
                    if (request.Role is not (CloudRoles.Device or CloudRoles.Service))
                    {
                        return CloudRequestValidation.Invalid(
                            "role",
                            "Only device and service tokens can be issued.");
                    }

                    var identity = context.User.CloudIdentity();
                    var token = await cloudStore.CreateTokenAsync(
                        identity.TeamId,
                        request.SubjectId,
                        request.Role,
                        cancellationToken);
                    connectionRegistry.Allow(identity.TeamId, request.SubjectId);
                    return Results.Json(
                        new TokenCreateResponse(token, request.SubjectId, request.Role),
                        statusCode: StatusCodes.Status201Created);
                })
            .RequireAuthorization(CloudPolicies.Admin);

        api.MapDelete(
                "/tokens/{subjectId}",
                async Task<IResult> (
                    string subjectId,
                    HttpContext context,
                    CloudStore cloudStore,
                    CloudNotificationConnectionRegistry connectionRegistry,
                    CancellationToken cancellationToken) =>
                {
                    if (!CloudRequestValidation.ValidIdentifier(subjectId, 128))
                    {
                        return CloudRequestValidation.Invalid(
                            "subjectId",
                            "The token subject identifier is invalid.");
                    }

                    var identity = context.User.CloudIdentity();
                    await cloudStore.RevokeTokensAsync(
                        identity.TeamId,
                        subjectId,
                        cancellationToken);
                    connectionRegistry.Revoke(identity.TeamId, subjectId);
                    return Results.NoContent();
                })
            .RequireAuthorization(CloudPolicies.Admin);

        api.MapPut(
                "/policy",
                async Task<IResult> (
                    TeamPolicyUpdateRequest request,
                    HttpContext context,
                    CloudStore cloudStore,
                    CloudNotifier notifier,
                    CancellationToken cancellationToken) =>
                {
                    if (request.AllowedExecutionProfiles is not { Count: > 0 and <= 2 } ||
                        request.AllowedExecutionProfiles.Any(profile => !ExecutionProfiles.Contains(profile)))
                    {
                        return CloudRequestValidation.Invalid(
                            "allowedExecutionProfiles",
                            "At least one supported execution profile is required.");
                    }
                    if (request.MaximumConcurrentJobs is < 1 or > 128)
                    {
                        return CloudRequestValidation.Invalid(
                            "maximumConcurrentJobs",
                            "The job limit must be between 1 and 128.");
                    }
                    if (request.AllowedPluginPublishers is null ||
                        request.AllowedPluginPublishers.Count > 128 ||
                        request.AllowedPluginPublishers.Any(
                            publisher => !CloudRequestValidation.ValidIdentifier(publisher, 128)))
                    {
                        return CloudRequestValidation.Invalid(
                            "allowedPluginPublishers",
                            "The publisher allowlist is invalid.");
                    }

                    var normalized = new TeamPolicyUpdateRequest(
                        request.AllowedExecutionProfiles
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .ToArray(),
                        request.RemoteRunnerEnabled,
                        request.UiAutomationEnabled,
                        request.MaximumConcurrentJobs,
                        request.AllowedPluginPublishers
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .ToArray());
                    var policy = await cloudStore.UpdatePolicyAsync(
                        context.User.CloudIdentity().TeamId,
                        normalized,
                        cancellationToken);
                    await notifier.PolicyChangedAsync(
                        context.User.CloudIdentity().TeamId,
                        policy.Version,
                        cancellationToken);
                    return Results.Ok(policy);
                })
            .RequireAuthorization(CloudPolicies.Admin);

        api.MapPost(
                "/handoffs",
                async Task<IResult> (
                    HandoffCreateRequest request,
                    HttpContext context,
                    CloudStore cloudStore,
                    CloudNotifier notifier,
                    IOptions<CloudOptions> options,
                    CancellationToken cancellationToken) =>
                {
                    var identity = context.User.CloudIdentity();
                    if (!CloudRequestValidation.ValidIdentifier(request.HandoffId, 128) ||
                        !CloudRequestValidation.ValidIdentifier(request.TargetDeviceId, 128) ||
                        !CloudRequestValidation.ValidIdentifier(request.SessionId, 128) ||
                        string.Equals(
                            request.TargetDeviceId,
                            identity.SubjectId,
                            StringComparison.Ordinal))
                    {
                        return CloudRequestValidation.Invalid(
                            "handoff",
                            "The handoff target and session must identify a different valid device.");
                    }
                    if (CloudRequestValidation.ValidateEnvelope(
                            request.Algorithm,
                            request.Nonce,
                            request.Ciphertext,
                            options.Value) is { } error)
                    {
                        return CloudRequestValidation.Invalid("envelope", error);
                    }

                    var created = await cloudStore.CreateHandoffAsync(
                        identity.TeamId,
                        identity.SubjectId,
                        request,
                        cancellationToken);
                    if (created.Status is HandoffCreateStatus.Duplicate)
                    {
                        return Results.Conflict(new { error = "duplicate_handoff" });
                    }
                    var handoff = created.Handoff ?? throw new InvalidOperationException(
                        "The created handoff result did not include a handoff.");
                    await notifier.HandoffChangedAsync(
                        identity.TeamId,
                        request.TargetDeviceId,
                        handoff.HandoffId,
                        cancellationToken);
                    return Results.Created($"/api/v1/handoffs/{handoff.HandoffId}", handoff);
                })
            .RequireAuthorization(CloudPolicies.Device);

        api.MapGet(
                "/handoffs",
                async Task<IResult> (
                    int? limit,
                    HttpContext context,
                    CloudStore cloudStore,
                    CancellationToken cancellationToken) =>
                {
                    var boundedLimit = limit ?? 50;
                    if (boundedLimit is < 1 or > 100)
                    {
                        return CloudRequestValidation.Invalid(
                            "limit",
                            "The handoff limit must be between 1 and 100.");
                    }
                    var identity = context.User.CloudIdentity();
                    return Results.Ok(
                        await cloudStore.ListHandoffsAsync(
                            identity.TeamId,
                            identity.SubjectId,
                            boundedLimit,
                            cancellationToken));
                })
            .RequireAuthorization(CloudPolicies.Device);

        api.MapPost(
                "/handoffs/{handoffId}/acknowledge",
                async Task<IResult> (
                    string handoffId,
                    HttpContext context,
                    CloudStore cloudStore,
                    CancellationToken cancellationToken) =>
                {
                    if (!CloudRequestValidation.ValidIdentifier(handoffId, 128))
                    {
                        return CloudRequestValidation.Invalid(
                            "handoffId",
                            "The handoff identifier is invalid.");
                    }
                    var identity = context.User.CloudIdentity();
                    return await cloudStore.AcknowledgeHandoffAsync(
                        identity.TeamId,
                        identity.SubjectId,
                        handoffId,
                        cancellationToken)
                        ? Results.NoContent()
                        : Results.NotFound();
                })
            .RequireAuthorization(CloudPolicies.Device);

        api.MapPost(
                "/plugin-publishers",
                async Task<IResult> (
                    PublisherCreateRequest request,
                    HttpContext context,
                    CloudStore cloudStore,
                    CancellationToken cancellationToken) =>
                {
                    if (!CloudRequestValidation.ValidIdentifier(request.KeyId, 128))
                    {
                        return CloudRequestValidation.Invalid(
                            "keyId",
                            "The publisher key identifier is invalid.");
                    }
                    if (string.IsNullOrWhiteSpace(request.PublicKeyPem) ||
                        request.PublicKeyPem.Length > 16 * 1024 ||
                        !PluginSignatureVerifier.IsSupportedPublicKey(request.PublicKeyPem))
                    {
                        return CloudRequestValidation.Invalid(
                            "publicKeyPem",
                            "A valid ECDSA public key with at least 256 bits is required.");
                    }

                    await cloudStore.UpsertPublisherAsync(
                        context.User.CloudIdentity().TeamId,
                        request.KeyId,
                        request.PublicKeyPem,
                        cancellationToken);
                    return Results.Created(
                        $"/api/v1/plugin-publishers/{request.KeyId}",
                        new { request.KeyId });
                })
            .RequireAuthorization(CloudPolicies.Admin);

        api.MapPost(
                "/plugins/{pluginId}/versions/{version}",
                async Task<IResult> (
                    string pluginId,
                    string version,
                    PluginPublishRequest request,
                    HttpContext context,
                    CloudStore cloudStore,
                    CancellationToken cancellationToken) =>
                {
                    if (!CloudRequestValidation.ValidIdentifier(pluginId, 128) ||
                        !CloudRequestValidation.ValidIdentifier(version, 64))
                    {
                        return CloudRequestValidation.Invalid(
                            "plugin",
                            "The plugin identifier or version is invalid.");
                    }
                    if (!CloudRequestValidation.ValidIdentifier(request.PublisherKeyId, 128) ||
                        string.IsNullOrWhiteSpace(request.ManifestJson) ||
                        request.ManifestJson.Length > 1024 * 1024 ||
                        !IsJsonObject(request.ManifestJson) ||
                        string.IsNullOrWhiteSpace(request.Sha256) ||
                        request.Sha256.Length != 64 ||
                        request.Sha256.Any(character => !char.IsAsciiHexDigit(character)) ||
                        string.IsNullOrWhiteSpace(request.Signature))
                    {
                        return CloudRequestValidation.Invalid(
                            "publication",
                            "The plugin publication metadata is invalid.");
                    }

                    var identity = context.User.CloudIdentity();
                    var policy = await cloudStore.GetPolicyAsync(identity.TeamId, cancellationToken);
                    if (!policy.AllowedPluginPublishers.Contains(
                            request.PublisherKeyId,
                            StringComparer.Ordinal))
                    {
                        return CloudRequestValidation.Invalid(
                            "publisherKeyId",
                            "The publisher is not allowed by team policy.");
                    }
                    var publicKey = await cloudStore.GetPublisherKeyAsync(
                        identity.TeamId,
                        request.PublisherKeyId,
                        cancellationToken);
                    if (publicKey is null ||
                        !PluginSignatureVerifier.Verify(
                            publicKey,
                            pluginId,
                            version,
                            request))
                    {
                        return CloudRequestValidation.Invalid(
                            "signature",
                            "The plugin signature is invalid.");
                    }

                    var normalized = request with { Sha256 = request.Sha256.ToLowerInvariant() };
                    var plugin = await cloudStore.PublishPluginAsync(
                        identity.TeamId,
                        pluginId,
                        version,
                        normalized,
                        cancellationToken);
                    return Results.Created(
                        $"/api/v1/plugins/{pluginId}/versions/{version}",
                        plugin);
                })
            .RequireAuthorization(CloudPolicies.Service);

        api.MapGet(
            "/plugins",
            async Task<IResult> (
                int? limit,
                HttpContext context,
                CloudStore cloudStore,
                CancellationToken cancellationToken) =>
            {
                var boundedLimit = limit ?? 50;
                if (boundedLimit is < 1 or > 100)
                {
                    return CloudRequestValidation.Invalid(
                        "limit",
                        "The plugin limit must be between 1 and 100.");
                }
                return Results.Ok(
                    await cloudStore.ListPluginsAsync(
                        context.User.CloudIdentity().TeamId,
                        boundedLimit,
                        cancellationToken));
            });

        api.MapPost(
                "/automations",
                async Task<IResult> (
                    AutomationCreateRequest request,
                    HttpContext context,
                    CloudStore cloudStore,
                    IOptions<CloudOptions> options,
                    CancellationToken cancellationToken) =>
                {
                    if (!CloudRequestValidation.ValidIdentifier(request.AutomationId, 128) ||
                        !string.Equals(
                            request.Kind,
                            RunnerPayloadKinds.Automation,
                            StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(request.Name) ||
                        request.Name.Length > 128 ||
                        request.IntervalSeconds is < 60 or > 2_678_400 ||
                        !CloudRequestValidation.ValidIdentifier(request.RequiredCapability, 64))
                    {
                        return CloudRequestValidation.Invalid(
                            "automation",
                            "The automation name, interval, or capability is invalid.");
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
                    var created = await cloudStore.CreateAutomationAsync(
                        identity.TeamId,
                        request,
                        cancellationToken);
                    if (created.Status is AutomationCreateStatus.RemoteRunnerDisabled)
                    {
                        return Results.Conflict(new { error = "remote_runner_disabled" });
                    }
                    if (created.Status is AutomationCreateStatus.Duplicate)
                    {
                        return Results.Conflict(new { error = "duplicate_automation" });
                    }
                    var automation = created.Automation ?? throw new InvalidOperationException(
                        "The created automation result did not include an automation.");
                    return Results.Created(
                        $"/api/v1/automations/{automation.AutomationId}",
                        automation);
                })
            .RequireAuthorization(CloudPolicies.Device);

        api.MapGet(
                "/automations",
                async Task<IResult> (
                    HttpContext context,
                    CloudStore cloudStore,
                    CancellationToken cancellationToken) =>
                    Results.Ok(
                        await cloudStore.ListAutomationsAsync(
                            context.User.CloudIdentity().TeamId,
                            cancellationToken)))
            .RequireAuthorization(CloudPolicies.Device);

        api.MapDelete(
                "/automations/{automationId}",
                async Task<IResult> (
                    string automationId,
                    HttpContext context,
                    CloudStore cloudStore,
                    CancellationToken cancellationToken) =>
                {
                    if (!CloudRequestValidation.ValidIdentifier(automationId, 128))
                    {
                        return CloudRequestValidation.Invalid(
                            "automationId",
                            "The automation identifier is invalid.");
                    }
                    return await cloudStore.DisableAutomationAsync(
                        context.User.CloudIdentity().TeamId,
                        automationId,
                        cancellationToken)
                        ? Results.NoContent()
                        : Results.NotFound();
                })
            .RequireAuthorization(CloudPolicies.Device);

        return api;
    }

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal static class CloudRequestValidation
{
    public static bool ValidIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(
            character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static string? ValidateEnvelope(
        string algorithm,
        string nonce,
        string ciphertext,
        CloudOptions options)
    {
        if (algorithm is not ("AES-256-GCM" or "XCHACHA20-POLY1305"))
        {
            return "The encryption algorithm is not supported.";
        }
        try
        {
            var nonceBytes = Convert.FromBase64String(nonce);
            var ciphertextBytes = Convert.FromBase64String(ciphertext);
            var expectedNonceLength = algorithm == "AES-256-GCM" ? 12 : 24;
            if (nonceBytes.Length != expectedNonceLength)
            {
                return "The envelope nonce length is invalid.";
            }
            if (ciphertextBytes.Length is < 16 ||
                ciphertextBytes.Length > options.MaximumCiphertextBytes)
            {
                return "The ciphertext size is outside the permitted range.";
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return "The envelope is not valid Base64.";
        }
        return null;
    }

    public static IResult Invalid(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });
}
