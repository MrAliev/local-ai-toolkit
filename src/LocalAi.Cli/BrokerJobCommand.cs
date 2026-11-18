using System.Globalization;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Broker.Client;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.Cli;

/// <summary>Status and reattachment concern one durable broker job, not its former caller's workflow.</summary>
internal static class BrokerJobCommand
{
    public static Task<int> ExecuteProductionAsync(string[] args, CommandStreams streams, bool machineReadable,
        CancellationToken cancellationToken) => ExecuteAsync(args, streams, machineReadable,
            (deadline, observer) => BrokerClientFactory.CreateDefault(observer: observer, deadline: deadline),
            new BrokerJobHandleStore(ModelResidencyPolicyStore.DefaultRuntimeRoot), cancellationToken);

    public static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable,
        Func<TimeSpan, ILocalRunObserver, IBrokerJobClient> clientFactory, BrokerJobHandleStore handles,
        CancellationToken cancellationToken = default)
    {
        var operation = args.FirstOrDefault();
        var command = "job " + (operation ?? "");
        if (operation is not ("status" or "wait" or "cancel") || args.Length < 2 ||
            !Guid.TryParseExact(args[1], "D", out var id) || id == Guid.Empty)
            return Refuse("input_rejected", CliText.JobArguments, 2);
        var seconds = 1800;
        if (args.Length != 2)
        {
            if (operation != "wait" || args.Length != 4 || args[2] != "--timeout-seconds" ||
                !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
                seconds is < 1 or > 86400)
                return Refuse("input_rejected", CliText.JobArguments, 2);
        }

        try
        {
            var client = clientFactory(TimeSpan.FromSeconds(seconds),
                new LocalRunProgress(streams.Error, static () => DateTimeOffset.UtcNow));
            var status = await client.GetStatusAsync(id, cancellationToken);
            if (status is null) return Refuse("job_not_found", CliText.JobNotFound, 66);
            if (operation == "status")
            {
                Answer(new { jobId = id, scope = "broker-job", state = status.State.ToString(),
                    status.CancellationRequested, status.FailureCode,
                    progress = status.Progress is null ? null : LocalModelOutput.Answer(
                        "broker-job:" + id, JsonSerializer.Serialize(status.Progress, LocalAiJson.Strict), true) });
                return 0;
            }
            if (operation == "cancel")
            {
                BrokerJobHandle? handle;
                try { handle = handles.Read(id); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    return Refuse("job_handle_unavailable", CliText.JobHandleNotSaved(id), 73);
                }
                if (handle is null) return Refuse("job_not_owner", CliText.JobNotOwner, 77);
                var result = await client.RequestCancellationAsync(handle, cancellationToken);
                switch (result)
                {
                    case JobCancellationResult.NotOwner: return Refuse("job_not_owner", CliText.JobNotOwner, 77);
                    case JobCancellationResult.NotFound: return Refuse("job_not_found", CliText.JobNotFound, 66);
                    case JobCancellationResult.Unsupported: return Refuse("job_cancel_unsupported", CliText.JobCancelUnsupported, 69);
                }
                Answer(new { jobId = id, scope = "broker-job", cancellation = result.ToString(),
                    terminalCancellationConfirmed = false });
                return 0;
            }

            var completed = await client.WaitAsync<JsonElement>(id, cancellationToken);
            // This is cached output, not another inference. Do not register its old receipt as a new model run.
            Answer(new { jobId = id, scope = "broker-job", state = "Succeeded",
                result = LocalModelOutput.Answer("broker-job:" + id, completed.Value.GetRawText(), true) });
            return 0;
        }
        catch (BrokerJobWaitCancelledException detached) { return Refuse("job_wait_detached", CliText.JobWaitDetached(detached.JobId), 130); }
        catch (BrokerJobWaitTimeoutException timedOut) { return Refuse("job_wait_timeout", CliText.JobWaitDetached(timedOut.JobId), 75); }
        catch (BrokerJobCancelledException cancelled) { return Refuse("job_cancelled", CliText.JobCancelled(cancelled.JobId), 130); }
        catch (BrokerJobFailedException) { return Refuse("job_failed", CliText.JobFailed, 70); }

        void Answer(object value)
        {
            streams.Out.WriteLine(machineReadable ? MachineOutput.Answer(command, value)
                : JsonSerializer.Serialize(value, LocalAiJson.Strict));
        }

        int Refuse(string code, string message, int exit)
        {
            if (exit == 130) UsageCall.Current?.Cancelled();
            else UsageCall.Current?.Refused(code);
            if (machineReadable) streams.Out.WriteLine(MachineOutput.Refusal(command.TrimEnd(), code, message));
            else streams.Error.WriteLine(code + ": " + message);
            return exit;
        }
    }
}
