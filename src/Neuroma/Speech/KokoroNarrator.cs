using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Neuroma.Speech;

public sealed partial class KokoroNarrator : IAsyncDisposable
{
    private readonly SpeechSettings _settings;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private bool? _captionApiAvailable;
    private bool _isRunning;
    private string _status = "KOKORO · READY";
    private SpeechCue? _currentCue;

    public KokoroNarrator(SpeechSettings settings) => _settings = settings;
    public event Action? Changed;

    public bool IsRunning { get { lock (_gate) return _isRunning; } }
    public string Status { get { lock (_gate) return _status; } }
    public SpeechCue? CurrentCue { get { lock (_gate) return _currentCue; } }

    public void Start(IReadOnlyList<SpeechChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            SetStatus("KOKORO FAILED · no readable text after this line");
            return;
        }

        CancellationTokenSource cancellation;
        Task task;
        lock (_gate)
        {
            if (_isRunning) return;
            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _isRunning = true;
            _status = $"KOKORO · STARTING · {_settings.Voice}";
            task = RunAsync(chunks, cancellation.Token);
            _task = task;
        }
        Changed?.Invoke();
        _ = ObserveAsync(task, cancellation);
    }

    public void Stop(string message = "KOKORO · STOPPED")
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _status = message;
            _currentCue = null;
        }
        cancellation?.Cancel();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        Task? task;
        lock (_gate) task = _task;
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _httpClient.Dispose();
        _cancellation?.Dispose();
    }

    private async Task ObserveAsync(Task task, CancellationTokenSource owner)
    {
        string? failure = null;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, owner))
                {
                    _isRunning = false;
                    _task = null;
                    _cancellation = null;
                    _currentCue = null;
                    if (failure is not null) _status = $"KOKORO FAILED · {failure}";
                    else if (!owner.IsCancellationRequested) _status = "KOKORO · COMPLETE";
                }
            }
            owner.Dispose();
            Changed?.Invoke();
        }
    }

    private async Task RunAsync(IReadOnlyList<SpeechChunk> chunks, CancellationToken cancellationToken)
    {
        for (int index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus($"KOKORO · GENERATING {index + 1}/{chunks.Count} · {_settings.Voice}");
            SpeechResult speech = await RequestSpeechAsync(chunks[index], cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SpeechCue> cues = BuildCues(chunks[index], speech.Timestamps);
            SetStatus($"KOKORO · {(speech.Exact ? "EXACT" : "ESTIMATED")} WORD TIMING · s TO STOP");
            await PlaySpeechAsync(speech.Audio, cues, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SpeechResult> RequestSpeechAsync(SpeechChunk chunk, CancellationToken cancellationToken)
    {
        var body = new
        {
            model = "kokoro",
            input = chunk.Text,
            voice = _settings.Voice,
            response_format = "wav",
            speed = _settings.Speed,
            stream = false
        };

        if (_captionApiAvailable != false)
        {
            using HttpResponseMessage captionResponse = await PostJsonAsync(
                $"{_settings.KokoroUrl}/dev/captioned_speech",
                new
                {
                    body.model, body.input, body.voice, body.response_format, body.speed, body.stream,
                    return_timestamps = true
                }, cancellationToken).ConfigureAwait(false);
            if (captionResponse.IsSuccessStatusCode)
            {
                byte[] json = await captionResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                _captionApiAvailable = true;
                return ParseCaptionedSpeech(json);
            }
            if (captionResponse.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException($"captioned speech returned HTTP {(int)captionResponse.StatusCode}");
            _captionApiAvailable = false;
        }

        using HttpResponseMessage response = await PostJsonAsync(
            $"{_settings.KokoroUrl}/v1/audio/speech", body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kokoro returned HTTP {(int)response.StatusCode}");
        byte[] audio = RepairWavHeader(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return new SpeechResult(audio, EstimateTimestamps(chunk.Text, WavDuration(audio)), Exact: false);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object body, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(json)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static SpeechResult ParseCaptionedSpeech(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string? encodedAudio = root.TryGetProperty("audio", out JsonElement audioElement) ? audioElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(encodedAudio)) throw new InvalidDataException("captioned speech returned no audio");
        var timestamps = new List<SpeechTimestamp>();
        if (root.TryGetProperty("timestamps", out JsonElement values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in values.EnumerateArray())
            {
                string word = value.TryGetProperty("word", out JsonElement wordElement) ? wordElement.GetString() ?? "" : "";
                double start = value.TryGetProperty("start_time", out JsonElement startElement) ? startElement.GetDouble() : 0;
                double end = value.TryGetProperty("end_time", out JsonElement endElement) ? endElement.GetDouble() : start;
                timestamps.Add(new SpeechTimestamp(word, start, end));
            }
        }
        return new SpeechResult(RepairWavHeader(Convert.FromBase64String(encodedAudio)), timestamps, Exact: true);
    }

    private async Task PlaySpeechAsync(byte[] audio, IReadOnlyList<SpeechCue> cues, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Kokoro playback currently requires Windows.");
        string audioPath = Path.Combine(Path.GetTempPath(), $"neuroma-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(audioPath, audio, cancellationToken).ConfigureAwait(false);
            string escapedPath = audioPath.Replace("'", "''", StringComparison.Ordinal);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add($"$p=New-Object System.Media.SoundPlayer('{escapedPath}');$p.Load();[Console]::Out.WriteLine('READY');$p.PlaySync()");

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows audio player did not start");
            using CancellationTokenRegistration registration = cancellationToken.Register(() => TryKill(process));
            string? ready = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ready, "READY", StringComparison.Ordinal))
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                string error = await process.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows audio player did not start" : error.Trim());
            }

            var stopwatch = Stopwatch.StartNew();
            int activeIndex = -1;
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                double elapsed = stopwatch.Elapsed.TotalSeconds;
                int cueIndex = FindActiveCue(cues, elapsed);
                if (cueIndex >= 0 && cueIndex != activeIndex)
                {
                    activeIndex = cueIndex;
                    SetCue(cues[cueIndex]);
                }
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            if (cancellationToken.IsCancellationRequested) TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            string playerError = await process.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(playerError) ? "Windows audio playback failed" : playerError.Trim());
        }
        finally
        {
            SetCue(null);
            try { File.Delete(audioPath); } catch (IOException) { }
        }
    }

    private static int FindActiveCue(IReadOnlyList<SpeechCue> cues, double elapsed)
    {
        int last = -1;
        for (int index = 0; index < cues.Count; index++)
        {
            if (elapsed >= cues[index].Start) last = index;
            if (elapsed >= cues[index].Start && elapsed < cues[index].End) return index;
        }
        return last;
    }

    private static IReadOnlyList<SpeechCue> BuildCues(SpeechChunk chunk, IReadOnlyList<SpeechTimestamp> timestamps)
    {
        var cues = new List<SpeechCue>(); int searchFrom = 0;
        foreach (SpeechTimestamp timestamp in timestamps)
        {
            string word = TrimNonWord().Replace(timestamp.Word, ""); if (word.Length == 0) continue;
            int matchStart = chunk.Text.IndexOf(word, searchFrom, StringComparison.CurrentCultureIgnoreCase);
            int matchLength = word.Length;
            if (matchStart < 0)
            {
                Match fallback = SpokenWord().Match(chunk.Text, searchFrom);
                if (!fallback.Success) continue;
                matchStart = fallback.Index; matchLength = fallback.Length;
            }
            int matchEnd = matchStart + matchLength; searchFrom = matchEnd;
            SpeechSpan? span = chunk.Spans.FirstOrDefault(candidate => matchStart >= candidate.TextStart && matchStart < candidate.TextEnd);
            if (span is null || matchEnd > span.TextEnd) continue;
            cues.Add(new SpeechCue(word, timestamp.StartTime, timestamp.EndTime, span.ChapterIndex, span.LineIndex,
                span.VisibleStart + matchStart - span.TextStart, span.VisibleStart + matchEnd - span.TextStart));
        }
        return cues;
    }

    private static IReadOnlyList<SpeechTimestamp> EstimateTimestamps(string text, double duration)
    {
        MatchCollection words = SpokenWord().Matches(text); if (words.Count == 0) return [];
        double[] weights = words.Select(match => Math.Max(1, Math.Sqrt(match.Length))).ToArray();
        double total = weights.Sum(); double elapsed = 0; var result = new List<SpeechTimestamp>();
        for (int index = 0; index < words.Count; index++)
        {
            double start = elapsed; elapsed += duration * weights[index] / total;
            result.Add(new SpeechTimestamp(words[index].Value, start, elapsed));
        }
        return result;
    }

    private static byte[] RepairWavHeader(byte[] audio)
    {
        if (audio.Length < 44 || Encoding.ASCII.GetString(audio, 0, 4) != "RIFF" || Encoding.ASCII.GetString(audio, 8, 4) != "WAVE")
            return audio;
        byte[] repaired = audio.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(repaired.AsSpan(4, 4), (uint)(repaired.Length - 8));
        int offset = 12;
        while (offset + 8 <= repaired.Length)
        {
            string name = Encoding.ASCII.GetString(repaired, offset, 4);
            uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(offset + 4, 4));
            if (name == "data")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(repaired.AsSpan(offset + 4, 4), (uint)(repaired.Length - offset - 8));
                break;
            }
            if (declaredSize == uint.MaxValue || declaredSize > int.MaxValue) break;
            long next = offset + 8L + declaredSize + declaredSize % 2;
            if (next > repaired.Length) break;
            offset = (int)next;
        }
        return repaired;
    }

    private static double WavDuration(byte[] audio)
    {
        if (audio.Length < 44) return 0;
        uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(28, 4));
        return byteRate == 0 ? 0 : Math.Max(0, audio.Length - 44d) / byteRate;
    }

    private void SetStatus(string status)
    {
        lock (_gate) _status = status;
        Changed?.Invoke();
    }

    private void SetCue(SpeechCue? cue)
    {
        lock (_gate) _currentCue = cue;
        Changed?.Invoke();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    [GeneratedRegex(@"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$")]
    private static partial Regex TrimNonWord();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*")]
    private static partial Regex SpokenWord();
}
