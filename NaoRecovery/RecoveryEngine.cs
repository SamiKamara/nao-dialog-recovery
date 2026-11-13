using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaoRecovery;

public enum RecoveryStatus { Healthy, Idle, Busy, ReloadRequired, NeedsAttention }
public sealed record RecoveryResult(RecoveryStatus Status, string Message, bool Restarted);

public sealed class RecoveryEngine(Action<string> log, int readinessAttempts = 15, int retryDelayMilliseconds = 1000,
    int recognitionSamples = 4, int recognitionDelayMilliseconds = 2000)
{
    private static readonly string[] StateKeys = ["ALSpeechRecognition/IsRunning", "ALSpeechRecognition/ActiveListening",
        "ALSpeechRecognition/Status", "ALTextToSpeech/TextStarted"];

    public async Task<RecoveryResult> RunAsync(string address, bool repair, CancellationToken cancellationToken, int port = 80)
    {
        log($"Connecting to NAO at {address}...");
        await using var robot = await RobotConnection.ConnectAsync(address, port, cancellationToken);
        log("Connected to NAO.");
        JsonElement dialog;
        var missing = false;
        try { dialog = await robot.ServiceAsync("ALDialog", cancellationToken); }
        catch (RobotRpcException error) when (IsMissingService(error, "ALDialog")) { dialog = default; missing = true; }
        var language = missing ? "" : ReadString(await robot.CallServiceAsync(dialog, "getLanguage", [], cancellationToken));
        var topics = missing ? [] : ReadStrings(await robot.CallServiceAsync(dialog, "getActivatedTopics", [], cancellationToken));
        var speech = await robot.ServiceAsync("ALSpeechRecognition", cancellationToken);
        var speechLanguage = ReadString(await robot.CallServiceAsync(speech, "getLanguage", [], cancellationToken));
        log($"Dialog language: {Display(language)}. Recognition language: {Display(speechLanguage)}.");
        log($"Active topics: {(topics.Length == 0 ? "none" : string.Join(", ", topics))}.");
        var contextFault = false;

        if (!string.IsNullOrWhiteSpace(language))
        {
            if (!language.Equals(speechLanguage, StringComparison.OrdinalIgnoreCase))
                return new(RecoveryStatus.NeedsAttention, "The dialog and recognition languages differ. Wait for the lesson to finish loading, then check again.", false);
            if (topics.Length == 0)
                return new(RecoveryStatus.Idle, $"{language} dialog is initialized. Start the lesson and run Check only if NAO does not respond. An idle robot does not need a recognition context.", false);

            var memory = await robot.ServiceAsync("ALMemory", cancellationToken);
            for (var sample = 0; sample < Math.Max(2, recognitionSamples); sample++)
            {
                // Re-read topics and language: do not restart a lesson that changed during the check.
                var currentLanguage = ReadString(await robot.CallServiceAsync(dialog, "getLanguage", [], cancellationToken));
                var currentTopics = ReadStrings(await robot.CallServiceAsync(dialog, "getActivatedTopics", [], cancellationToken));
                if (!currentLanguage.Equals(language, StringComparison.OrdinalIgnoreCase) || !topics.SequenceEqual(currentTopics))
                    return new(RecoveryStatus.Busy, "The lesson changed during the check. Let it finish loading, then check again.", false);
                var contexts = ReadStrings(await robot.CallServiceAsync(speech, "getContexts", [], cancellationToken));
                var state = await robot.CallServiceAsync(memory, "getListData", [StateKeys], cancellationToken);
                if (state.ValueKind != JsonValueKind.Array || state.GetArrayLength() != 4)
                    return new(RecoveryStatus.NeedsAttention, "NAO did not return a complete recognition state. No recovery was attempted; save the log for diagnosis.", false);
                var running = ReadBool(state[0]);
                var listening = ReadBool(state[1]);
                var status = ReadString(state[2]);
                var speaking = ReadBool(state[3]);
                log($"Recognition sample {sample + 1}: running={running}, active listening={listening}, status={status}, speaking={speaking}, contexts=[{string.Join(", ", contexts)}].");
                if (contexts.Length > 0 && listening == true && running == true)
                    return new(RecoveryStatus.Healthy, $"{language} recognition is actively listening with a loaded context. No recovery is needed.", false);
                if (running != true || listening != false || status != "ListenOn" || speaking != false || contexts.Length != 0)
                    return new(RecoveryStatus.Busy, "NAO is speaking, paused, processing, or still loading. No confirmed missing-context fault was found. Check again while the lesson says it is listening.", false);
                if (sample == 0) log("Checking whether the missing recognition context persists...");
                if (sample + 1 < Math.Max(2, recognitionSamples))
                    await Task.Delay(recognitionDelayMilliseconds, cancellationToken);
            }
            contextFault = true;
            log("Confirmed: the lesson claims to be listening, but no recognition context is loaded and active listening remains off.");
        }
        else log(missing ? "The dialog service is unavailable." : "The dialog service has no active language.");

        // A dialog restart interrupts every topic. Only the known ELIAS lesson topics may be active.
        if (topics.Any(topic => !IsEliasTopic(topic)))
            return new(RecoveryStatus.NeedsAttention, "Another robot conversation is active. Stop that conversation before recovery; the dialog service was left running.", false);
        var targetLanguage = !string.IsNullOrWhiteSpace(language) ? language : !string.IsNullOrWhiteSpace(speechLanguage) ? speechLanguage : "English";
        var installed = ReadStrings(await robot.CallServiceAsync(speech, "getAvailableLanguages", [], cancellationToken));
        if (!installed.Contains(targetLanguage, StringComparer.OrdinalIgnoreCase))
            return new(RecoveryStatus.NeedsAttention, $"NAO does not list {targetLanguage} as an installed recognition language. Check the language installation before recovery.", false);
        log($"Recovery will preserve {targetLanguage}.");
        if (!repair)
            return new(RecoveryStatus.NeedsAttention, contextFault
                ? $"The {targetLanguage} lesson has the missing recognition-context fault. Leave the stalled lesson open and use Check & recover."
                : $"The dialog startup fault is present. Use Check & recover to restore {targetLanguage}.", false);

        var manager = await robot.ServiceAsync("ALServiceManager", cancellationToken);
        var service = await robot.CallServiceAsync(manager, "service", ["dialog"], cancellationToken);
        var name = service.GetProperty("name").GetString();
        var command = service.GetProperty("execStart").GetString() ?? "";
        if (name != "dialog" || !Regex.IsMatch(command, @"(?:^|\s)--name\s+ALDialog(?:\s|$)") || !Regex.IsMatch(command, @"(?:^|\s)--legacy\s+dialog(?:\s|$)"))
            throw new InvalidOperationException("The robot's dialog service has an unexpected identity. Recovery stopped without restarting it.");
        if (!missing)
        {
            var finalLanguage = ReadString(await robot.CallServiceAsync(dialog, "getLanguage", [], cancellationToken));
            var finalTopics = ReadStrings(await robot.CallServiceAsync(dialog, "getActivatedTopics", [], cancellationToken));
            if (finalLanguage != language || !finalTopics.SequenceEqual(topics))
                return new(RecoveryStatus.Busy, "The lesson changed before recovery. No restart was attempted; check again.", false);
        }
        log("Restarting NAO's dialog service...");
        var restarted = await robot.CallServiceAsync(manager, "restartService", ["dialog"], cancellationToken, 25);
        if (restarted.ValueKind != JsonValueKind.True)
            return new(RecoveryStatus.NeedsAttention, "NAO did not accept the dialog-service restart. Check the robot or contact its supplier.", false);
        log($"Dialog service restarted. Waiting to restore {targetLanguage}...");
        var languageApplied = false;
        for (var attempt = 0; attempt < readinessAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Restart invalidates the old browser and tool proxies. Always resolve the new service.
                dialog = await robot.ServiceAsync("ALDialog", cancellationToken);
                var current = ReadString(await robot.CallServiceAsync(dialog, "getLanguage", [], cancellationToken));
                if (!string.IsNullOrWhiteSpace(current))
                {
                    if (!current.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase) && !languageApplied)
                    {
                        // ALDialog synchronizes ASR itself. Never issue simultaneous ASR/Dialog language changes.
                        await robot.CallServiceAsync(dialog, "setLanguage", [targetLanguage], cancellationToken, 25);
                        languageApplied = true;
                        current = ReadString(await robot.CallServiceAsync(dialog, "getLanguage", [], cancellationToken));
                    }
                    speech = await robot.ServiceAsync("ALSpeechRecognition", cancellationToken);
                    var recognized = ReadString(await robot.CallServiceAsync(speech, "getLanguage", [], cancellationToken));
                    if (current.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase) && recognized.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
                    {
                        log($"Verified: dialog and recognition languages are {targetLanguage}.");
                        return new(RecoveryStatus.ReloadRequired, $"{targetLanguage} dialog restored. Refresh the existing ELIAS lesson (Ctrl+R), reconnect if asked, and start it again. Then use Check only to verify listening.", true);
                    }
                }
            }
            catch (RobotRpcException error) when (IsMissingService(error, "ALDialog") || error.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)) { }
            await Task.Delay(retryDelayMilliseconds, cancellationToken);
        }
        return new(RecoveryStatus.NeedsAttention, $"The dialog service restarted, but {targetLanguage} did not become ready. Save this log for your robot supplier.", true);
    }

    private static string Display(string text) => string.IsNullOrWhiteSpace(text) ? "not initialized" : text;
    private static string ReadString(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string[] ReadStrings(JsonElement value) => value.EnumerateArray().Select(ReadString).ToArray();
    private static bool? ReadBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
        _ => null
    };
    private static bool IsEliasTopic(string topic) => Regex.IsMatch(topic, @"^DynamicVocabulary[A-Za-z]+$")
        || Regex.IsMatch(topic, @"^EliasChatEN[0-9]+$")
        || topic is "EliasChat" or "eliasChatFi" or "eliasChatEn";
    private static bool IsMissingService(RobotRpcException error, string name) =>
        error.Message.Contains($"Cannot find service '{name}'", StringComparison.Ordinal);
}
