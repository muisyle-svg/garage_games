using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarageGames.Controller.Infrastructure;
using GarageGames.Core.Domain;

namespace GarageGames.Controller.Services;

public sealed class GoogleSyncHostedService(
    ControllerStore store,
    RunService runs,
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<GoogleSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(2, configuration.GetValue("Controller:GooglePollSeconds", 5));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PushOutboxAsync(stoppingToken);
                await PullCorrectionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Google synchronization pass failed.");
            }
        }
    }

    private async Task PushOutboxAsync(CancellationToken cancellationToken)
    {
        var endpoint = await store.GetSettingAsync("googleEndpoint", cancellationToken);
        var secret = await store.GetSettingAsync("googleSecret", cancellationToken);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        var records = await store.GetOutboxAsync(50, cancellationToken);
        if (records.Count == 0)
        {
            return;
        }

        var payload = new
        {
            action = "syncBatch",
            idempotencyKey = $"batch:{records[0].Id}:{records[^1].Id}",
            records = records.Select(record => new
            {
                record.IdempotencyKey,
                record.Action,
                payload = JsonSerializer.Deserialize<JsonElement>(record.PayloadJson)
            })
        };

        try
        {
            using var response = await SendSignedAsync(endpoint, secret, payload, cancellationToken);
            await EnsureGoogleSuccessAsync(response, cancellationToken);
            foreach (var record in records)
            {
                await store.CompleteOutboxAsync(record.Id, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            foreach (var record in records)
            {
                await store.FailOutboxAsync(record.Id, record.Attempts, exception.Message, cancellationToken);
            }
            throw;
        }
    }

    private async Task PullCorrectionsAsync(CancellationToken cancellationToken)
    {
        var endpoint = await store.GetSettingAsync("googleEndpoint", cancellationToken);
        var secret = await store.GetSettingAsync("googleSecret", cancellationToken);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        using var response = await SendSignedAsync(
            endpoint,
            secret,
            new { action = "pullCorrections", limit = 25 },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var result = await response.Content.ReadFromJsonAsync<CorrectionResponse>(
            JsonDefaults.Options,
            cancellationToken);
        if (result?.Ok != true || result.Corrections is null)
        {
            return;
        }

        var state = await runs.GetStateAsync(cancellationToken);
        foreach (var correction in result.Corrections)
        {
            var accepted = false;
            var message = "";
            try
            {
                var eventId = $"correction:{correction.CorrectionId}";
                if (await store.EventExistsAsync(eventId, cancellationToken))
                {
                    accepted = true;
                    message = "Already applied";
                }
                else
                {
                if (state.Run is null || correction.RunId != state.Run.RunId)
                {
                    throw new InvalidOperationException("Correction does not target the active run.");
                }
                if (correction.ExpectedRevision != state.Run.Revision)
                {
                    throw new InvalidOperationException(
                        $"Stale correction: expected revision {correction.ExpectedRevision}, current {state.Run.Revision}.");
                }

                await runs.CorrectAsync(
                    new(
                        correction.GameId,
                        correction.Field,
                        correction.NumericValue,
                        correction.BooleanValue,
                        correction.Reason,
                        correction.ExpectedRevision),
                    "google-sheet",
                    correction.CorrectionId,
                    cancellationToken);
                accepted = true;
                message = "Applied";
                state = await runs.GetStateAsync(cancellationToken);
                }
            }
            catch (Exception exception)
            {
                message = exception.Message;
            }

            using var acknowledgement = await SendSignedAsync(
                endpoint,
                secret,
                new
                {
                    action = "ackCorrection",
                    correctionId = correction.CorrectionId,
                    accepted,
                    message
                },
                cancellationToken);
            await EnsureGoogleSuccessAsync(acknowledgement, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendSignedAsync(
        string endpoint,
        string secret,
        object payload,
        CancellationToken cancellationToken)
    {
        var payloadJson = JsonDefaults.Serialize(payload);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var material = $"{timestamp}.{nonce}.{payloadJson}";
        var signature = Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(material)));
        var body = JsonDefaults.Serialize(new { timestamp, nonce, signature, payloadJson });

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-GG-Timestamp", timestamp);
        request.Headers.Add("X-GG-Nonce", nonce);
        request.Headers.Add("X-GG-Signature", signature);
        return await clients.CreateClient("google-sheet").SendAsync(request, cancellationToken);
    }

    private static async Task EnsureGoogleSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GoogleResponse>(
            JsonDefaults.Options,
            cancellationToken);
        if (result?.Ok != true)
        {
            throw new InvalidOperationException(result?.Error ?? "Google synchronization was rejected.");
        }
    }

    private sealed record GoogleResponse(bool Ok, string? Error);
    private sealed record CorrectionResponse(bool Ok, IReadOnlyList<SheetCorrection>? Corrections);
    private sealed record SheetCorrection(
        string CorrectionId,
        string RunId,
        string GameId,
        string Field,
        decimal? NumericValue,
        bool? BooleanValue,
        string Reason,
        long ExpectedRevision);
}
